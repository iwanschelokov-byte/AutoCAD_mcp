"""
System prompt and tool catalogue for the AutoCode Agent.

The agent asks Claude to write Python that drives AutoCAD through the plugin's
JSON-RPC port. The catalogue is built from the same generated `tools.json` the
C# MCP server embeds, so the agent, the C# server, and the Python server all
describe one tool surface — there is no third hand-maintained copy to drift.
"""

from __future__ import annotations

import json
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
TOOLS_JSON = REPO / "autocad-plugin" / "src" / "AutoCADMCP.Server" / "tools.json"


def load_tools() -> list[dict]:
    """Load the generated tool catalogue. Returns [] when it has not been built."""
    if not TOOLS_JSON.exists():
        return []
    try:
        return json.loads(TOOLS_JSON.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return []


def format_catalog(tools: list[dict], include_schemas: bool = True) -> str:
    """Render the catalogue compactly enough to sit in a cached system prompt."""
    if not tools:
        return "(tool catalogue unavailable — call list_methods at runtime to discover tools)"

    lines: list[str] = []
    for t in tools:
        name = t.get("name", "")
        summary = t.get("summary") or (t.get("description") or "").strip().splitlines()[:1]
        if isinstance(summary, list):
            summary = summary[0] if summary else ""

        if not include_schemas:
            lines.append(f"- {name}: {summary}")
            continue

        schema = t.get("inputSchema", {})
        props = schema.get("properties", {})
        required = set(schema.get("required", []))

        params = []
        for pname, pschema in props.items():
            ptype = pschema.get("type", "any")
            if ptype == "array":
                item_type = (pschema.get("items") or {}).get("type", "any")
                ptype = f"{item_type}[]"
            params.append(f"{pname}: {ptype}" + ("" if pname in required else "?"))

        sig = ", ".join(params)
        lines.append(f"- {name}({sig})\n    {summary}")

    return "\n".join(lines)


# The traps below are not guesses — each one is a real behaviour of this plugin
# that has bitten a caller, and each is cheap to state and expensive to discover.
TRAPS = """\
Known behaviours that will trip you up if you do not account for them:

- Handles: every create_* tool returns {"id": "<handle>"}. Pass that handle back
  as `id` (or `handle` for the older modify tools). Handles are decimal here;
  AutoCAD's own UI shows hexadecimal — the plugin accepts both.
- Parameter aliases: move_entity/copy_entity accept `from`/`to` AND
  `from_point`/`to_point`; zoom_window and select_by_window accept `min`/`max`
  AND `min_point`/`max_point`. Either spelling works.
- Destructive tools require confirmation: erase_entity, bulk_erase, delete_layer,
  delete_layout, delete_layer_state, delete_block_definition, purge_drawing,
  detach_xref, overkill and ungroup all refuse unless you pass
  {"__confirm": true}. They return errorCode "NeedsConfirm" otherwise.
- Read-only mode: if the server is read-only, every drawing-modifying tool
  returns errorCode "ReadOnly". Do not try to work around it — report it.
- execute_command is asynchronous. It queues a command and returns immediately;
  use read_command_line afterwards to see what actually happened.
- plot_to_pdf waits for the file; it is the tool to use for plotting. Do not use
  it to "check" whether plotting works — it is slow.
- measure_between returns center_distance: null (with a center_distance_note)
  when an entity has no geometric extents. Check for null before doing maths.
- Layers must exist before you draw on them. create_layer first, then pass
  `layer` to the create_* call — passing an unknown layer name silently draws on
  the current layer instead.
- Points are [x, y] or [x, y, z]. Angles are degrees unless a tool says otherwise.
"""

CONTRACT = """\
You are writing Python that runs inside a host process which has already
connected to AutoCAD. Exactly one function is pre-bound for you:

    call(method: str, params: dict | None = None) -> dict

It invokes a plugin method and returns the decoded result. It raises RuntimeError
if the plugin reports an error, so you do not need to check a status field — let
it raise, or catch it if you intend to recover.

`json` and `math` are imported. You may use loops, comprehensions, helper
functions, and any pure-Python standard library. Do not import anything that
touches the network or filesystem unless the task explicitly requires it.

Assign your final answer to a variable named `result` if the task asks a
question; otherwise leave it unset. Anything you print is captured.
"""


def build_system_prompt(include_schemas: bool = True) -> str:
    """Assemble the full system prompt. Stable across calls, so it caches well."""
    tools = load_tools()
    catalog = format_catalog(tools, include_schemas=include_schemas)
    count = len(tools) if tools else "an unknown number of"

    return f"""\
You write Python that draws in AutoCAD through a plugin's JSON-RPC interface.

{CONTRACT}

# Available tools ({count} total)

{catalog}

# {TRAPS}

# How to approach a request

Work out the geometry before you write the code. Compute coordinates rather than
hard-coding a grid of magic numbers, and name the dimensions you derive so the
code reads as the drawing it produces.

Prefer bulk_create for many similar entities — it is one round trip instead of
hundreds. Prefer measure_text/measure_texts over estimating text width; SHX
glyphs are proportional and estimates overlap.

If the request is ambiguous in a way that changes the drawing, pick the reading a
draughtsman would and say which you picked in your plan. Do not ask a question
back — you are producing code, not holding a conversation.

If the request cannot be done with the available tools, say so in your plan and
return the closest thing that can be done, rather than inventing a tool name.
Every method you call must appear in the catalogue above."""


# Structured-output schema. The API validates the response against this, so the
# host never has to parse a fenced code block out of prose.
RESPONSE_SCHEMA = {
    "type": "object",
    "properties": {
        "plan": {
            "type": "string",
            "description": "Two or three sentences: the geometry, and any ambiguity resolved.",
        },
        "code": {
            "type": "string",
            "description": "The Python to run. No markdown fences, no commentary.",
        },
        "tools_used": {
            "type": "array",
            "items": {"type": "string"},
            "description": "Plugin method names the code calls.",
        },
    },
    "required": ["plan", "code", "tools_used"],
    "additionalProperties": False,
}
