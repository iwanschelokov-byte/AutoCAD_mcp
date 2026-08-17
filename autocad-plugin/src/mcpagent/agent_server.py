"""
AutoCode Agent — an MCP server that turns a drawing request into AutoCAD code.

Two-stage: it asks Claude to write Python against the AutoCAD plugin's tool
surface, then (optionally) runs that Python against a live AutoCAD.

    generate_drawing_code   plan + code, nothing executed  — always available
    draw                    generate, then execute          — opt-in, see below
    agent_status            what is configured and reachable

Execution is OFF unless AUTOCAD_AGENT_ALLOW_EXEC=1. Generated code is not
sandboxed: it runs in this process with full filesystem and network access. It
reaches AutoCAD only through the plugin's JSON-RPC port, so the plugin's
read-only mode and destructive-confirmation gates still apply to what it draws.

Credentials: the Anthropic SDK resolves them itself (ANTHROPIC_API_KEY, or an
`ant auth login` profile). Nothing here reads or stores a key.

    pip install -r requirements.txt
    python agent_server.py
"""

from __future__ import annotations

import contextlib
import io
import json
import math
import os
import socket
import sys
import traceback

import anthropic
from mcp.server.fastmcp import FastMCP

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from code_prompts import RESPONSE_SCHEMA, build_system_prompt, load_tools  # noqa: E402

HOST = os.environ.get("AUTOCAD_MCP_HOST", "localhost")
PORT = int(os.environ.get("AUTOCAD_MCP_PORT", "8081"))
MODEL = os.environ.get("AUTOCAD_AGENT_MODEL", "claude-opus-5")
EFFORT = os.environ.get("AUTOCAD_AGENT_EFFORT", "high")

_ALLOW_EXEC = os.environ.get("AUTOCAD_AGENT_ALLOW_EXEC", "").strip().lower() in (
    "1", "true", "yes", "on",
)

mcp = FastMCP("AutoCAD AutoCode Agent")
_client = anthropic.Anthropic()


# =============================================================================
# Plugin transport
# =============================================================================

class PluginError(RuntimeError):
    """The plugin reported an error, or could not be reached."""


def call_plugin(method: str, params: dict | None = None, timeout: float = 120.0) -> dict:
    """One JSON-RPC call over a fresh connection.

    A connection per call keeps a stalled response from desynchronising later
    ones — the same reasoning as the C# server's PluginClient.
    """
    request = json.dumps({
        "jsonrpc": "2.0", "method": method, "params": params or {}, "id": 1,
    }) + "\n"

    try:
        with socket.create_connection((HOST, PORT), timeout=timeout) as sock:
            sock.settimeout(timeout)
            sock.sendall(request.encode("utf-8"))

            buffer = b""
            while b"\n" not in buffer:
                chunk = sock.recv(65536)
                if not chunk:
                    raise PluginError("The AutoCAD plugin closed the connection.")
                buffer += chunk
    except OSError as exc:
        raise PluginError(
            f"Could not reach the AutoCAD plugin at {HOST}:{PORT} ({exc}). "
            "Start AutoCAD, load the plugin, and run MCPSTART."
        ) from exc

    response = json.loads(buffer.split(b"\n", 1)[0].decode("utf-8"))

    if "error" in response:
        err = response["error"]
        code = (err.get("data") or {}).get("errorCode")
        message = err.get("message", "unknown plugin error")
        raise PluginError(f"[{code}] {message}" if code else message)

    return response.get("result", {})


def plugin_reachable() -> bool:
    try:
        with socket.create_connection((HOST, PORT), timeout=2):
            return True
    except OSError:
        return False


# =============================================================================
# Code generation
# =============================================================================

def _generate(request: str, context: str = "") -> dict:
    """Ask Claude for a plan and code. Returns the validated structured output."""
    user_content = request if not context else f"{request}\n\nAdditional context:\n{context}"

    try:
        with _client.beta.messages.stream(
            model=MODEL,
            max_tokens=32000,
            # The catalogue is large and identical between calls, so cache it.
            system=[{
                "type": "text",
                "text": build_system_prompt(),
                "cache_control": {"type": "ephemeral"},
            }],
            thinking={"type": "adaptive"},
            output_config={
                "effort": EFFORT,
                "format": {"type": "json_schema", "schema": RESPONSE_SCHEMA},
            },
            # Safety classifiers can decline a request; route it to the
            # recommended fallback rather than failing the call.
            betas=["server-side-fallback-2026-07-01"],
            fallbacks="default",
            messages=[{"role": "user", "content": user_content}],
        ) as stream:
            message = stream.get_final_message()
    except anthropic.APIStatusError as exc:
        raise RuntimeError(f"Claude API error {exc.status_code}: {exc.message}") from exc
    except anthropic.APIConnectionError as exc:
        raise RuntimeError(f"Could not reach the Claude API: {exc}") from exc

    # Check why generation stopped before reading content — a refusal has no
    # usable content block, and max_tokens leaves truncated JSON.
    if message.stop_reason == "refusal":
        category = getattr(getattr(message, "stop_details", None), "category", None)
        raise RuntimeError(
            "Claude declined this request"
            + (f" (category: {category})" if category else "")
            + ". Rephrase it, or handle the drawing manually."
        )
    if message.stop_reason == "max_tokens":
        raise RuntimeError(
            "The generated code was cut off at the token limit. Split the request "
            "into smaller pieces."
        )

    text = next((b.text for b in message.content if b.type == "text"), None)
    if not text:
        raise RuntimeError(f"Claude returned no code (stop_reason={message.stop_reason}).")

    payload = json.loads(text)
    payload["model"] = message.model
    payload["usage"] = {
        "input_tokens": message.usage.input_tokens,
        "output_tokens": message.usage.output_tokens,
        "cache_read_input_tokens": getattr(message.usage, "cache_read_input_tokens", 0),
    }
    return payload


def _execute(code: str, timeout: float) -> dict:
    """Run generated code with `call` pre-bound. Captures output and failures."""
    namespace: dict = {
        "call": lambda m, p=None: call_plugin(m, p, timeout=timeout),
        "json": json,
        "math": math,
        "__name__": "__autocad_generated__",
    }
    buffer = io.StringIO()

    try:
        with contextlib.redirect_stdout(buffer), contextlib.redirect_stderr(buffer):
            exec(code, namespace)  # noqa: S102 - the point of the tool, gated above
    except Exception as exc:  # noqa: BLE001 - report anything the code raises
        return {
            "executed": True,
            "success": False,
            "error": f"{type(exc).__name__}: {exc}",
            "traceback": traceback.format_exc()[-4000:],
            "output": buffer.getvalue()[-4000:],
        }

    out: dict = {"executed": True, "success": True, "output": buffer.getvalue()[-8000:]}
    if "result" in namespace:
        try:
            json.dumps(namespace["result"])
            out["result"] = namespace["result"]
        except (TypeError, ValueError):
            out["result"] = repr(namespace["result"])[:2000]
    return out


# =============================================================================
# MCP tools
# =============================================================================

@mcp.tool()
async def agent_status() -> str:
    """Report what the agent has available: model, tool catalogue, plugin, execution."""
    tools = load_tools()
    return json.dumps({
        "model": MODEL,
        "effort": EFFORT,
        "tool_catalog": len(tools) or "unavailable (run build/generate_tool_schemas.py)",
        "plugin": f"{HOST}:{PORT}",
        "plugin_reachable": plugin_reachable(),
        "execution_enabled": _ALLOW_EXEC,
        "note": None if _ALLOW_EXEC else
                "Execution is off. Set AUTOCAD_AGENT_ALLOW_EXEC=1 to enable `draw`. "
                "generate_drawing_code works either way.",
    }, indent=2)


@mcp.tool()
async def generate_drawing_code(request: str, context: str = "") -> str:
    """Generate AutoCAD Python for a drawing request, without running it.

    Returns a plan, the code, and the plugin methods it calls, so you can read it
    before deciding to run it. Needs no AutoCAD connection.
    """
    if not request.strip():
        return json.dumps({"success": False, "error": "No request supplied."}, indent=2)

    try:
        payload = _generate(request, context)
    except Exception as exc:  # noqa: BLE001 - surface generation failures as data
        return json.dumps({"success": False, "error": str(exc)}, indent=2)

    payload["success"] = True
    payload["executed"] = False
    return json.dumps(payload, indent=2)


@mcp.tool()
async def draw(request: str, context: str = "", timeout: float = 120.0) -> str:
    """Generate AutoCAD code for a request and run it against the open drawing.

    Requires AUTOCAD_AGENT_ALLOW_EXEC=1 and a running AutoCAD with MCPSTART.
    The generated code is not sandboxed. Drawing operations still pass through
    the plugin's read-only and destructive-confirmation gates.

    Use generate_drawing_code first if you want to read the code before it runs.
    """
    if not _ALLOW_EXEC:
        return json.dumps({
            "success": False,
            "error": "Execution is disabled.",
            "hint": "Set AUTOCAD_AGENT_ALLOW_EXEC=1 to enable `draw`. It runs "
                    "generated Python unsandboxed in this process. "
                    "generate_drawing_code needs no such permission.",
        }, indent=2)

    if not request.strip():
        return json.dumps({"success": False, "error": "No request supplied."}, indent=2)

    if not plugin_reachable():
        return json.dumps({
            "success": False,
            "error": f"The AutoCAD plugin is not reachable at {HOST}:{PORT}.",
            "hint": "Start AutoCAD, load the plugin, and run MCPSTART.",
        }, indent=2)

    try:
        payload = _generate(request, context)
    except Exception as exc:  # noqa: BLE001
        return json.dumps({"success": False, "executed": False, "error": str(exc)}, indent=2)

    outcome = _execute(payload["code"], timeout=timeout)
    payload.update(outcome)
    payload["success"] = outcome["success"]
    return json.dumps(payload, indent=2)


if __name__ == "__main__":
    print(
        f"AutoCode Agent: model={MODEL} effort={EFFORT} "
        f"plugin={HOST}:{PORT} execution={'on' if _ALLOW_EXEC else 'off'}",
        file=sys.stderr,
    )
    mcp.run(transport="stdio")
