"""
Generate MCP tool schemas from the Python server's typed signatures.

The Python FastMCP server already declares every tool with real type hints and a
docstring. Rather than hand-maintaining a second copy of 180 JSON schemas for the
C# server (which is how they drift apart), this parses server.py's AST and emits
a single tools.json that the C# server embeds.

    python build/generate_tool_schemas.py            # write tools.json
    python build/generate_tool_schemas.py --check    # fail if out of date (CI)

Keeping the generator honest matters more than covering exotic types: anything it
cannot map confidently is emitted as a permissive schema rather than a wrong one.
"""

from __future__ import annotations

import argparse
import ast
import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SERVER = REPO / "autocad-plugin" / "src" / "mcp_server" / "server.py"
OUTPUT = REPO / "autocad-plugin" / "src" / "AutoCADMCP.Server" / "tools.json"

# Parameters that are transport plumbing, not user-facing arguments.
SKIP_PARAMS = {"self"}


def type_to_schema(annotation: ast.expr | None) -> dict:
    """Map a Python annotation to a JSON Schema fragment."""
    if annotation is None:
        return {}

    # Optional[X] / X | None  ->  schema of X (nullability is implied by absence)
    if isinstance(annotation, ast.BinOp) and isinstance(annotation.op, ast.BitOr):
        parts = [annotation.left, annotation.right]
        non_none = [
            p for p in parts
            if not (isinstance(p, ast.Constant) and p.value is None)
        ]
        if len(non_none) == 1:
            return type_to_schema(non_none[0])
        return {}

    if isinstance(annotation, ast.Name):
        return {
            "str": {"type": "string"},
            "int": {"type": "integer"},
            "float": {"type": "number"},
            "bool": {"type": "boolean"},
            "dict": {"type": "object"},
            "list": {"type": "array"},
        }.get(annotation.id, {})

    # list[float], list[str], list[list[float]], list[dict]
    if isinstance(annotation, ast.Subscript):
        base = annotation.value
        if isinstance(base, ast.Name) and base.id == "list":
            inner = type_to_schema(annotation.slice)
            return {"type": "array", "items": inner} if inner else {"type": "array"}
        if isinstance(base, ast.Name) and base.id == "dict":
            return {"type": "object"}

    return {}


def default_of(node: ast.expr) -> object:
    try:
        return ast.literal_eval(node)
    except (ValueError, SyntaxError):
        return None


def first_line(doc: str | None) -> str:
    if not doc:
        return ""
    for line in doc.strip().splitlines():
        line = line.strip()
        if line:
            return line
    return ""


def build() -> list[dict]:
    tree = ast.parse(SERVER.read_text(encoding="utf-8"))
    tools: list[dict] = []

    for node in tree.body:
        if not isinstance(node, (ast.AsyncFunctionDef, ast.FunctionDef)):
            continue

        is_tool = any(
            (isinstance(d, ast.Call) and getattr(d.func, "attr", "") == "tool")
            or getattr(d, "attr", "") == "tool"
            for d in node.decorator_list
        )
        if not is_tool:
            continue

        args = node.args
        positional = args.args
        # Defaults align to the tail of the positional list.
        pad = len(positional) - len(args.defaults)
        defaults = [None] * pad + list(args.defaults)

        properties: dict[str, dict] = {}
        required: list[str] = []

        for i, arg in enumerate(positional):
            name = arg.arg
            if name in SKIP_PARAMS:
                continue

            schema = type_to_schema(arg.annotation)
            default_node = defaults[i]

            if default_node is None:
                required.append(name)
            else:
                value = default_of(default_node)
                # Sentinel defaults ("" / -1 / 0.0 / None) mean "not supplied",
                # so advertising them as real defaults would be misleading.
                if value not in ("", -1, None):
                    schema = dict(schema)
                    schema["default"] = value

            properties[name] = schema

        docstring = ast.get_docstring(node)
        tools.append({
            "name": node.name,
            "description": (docstring or "").strip(),
            "summary": first_line(docstring),
            "inputSchema": {
                "type": "object",
                "properties": properties,
                "required": required,
                # Extra keys are tolerated so __confirm can always be passed.
                "additionalProperties": True,
            },
        })

    tools.sort(key=lambda t: t["name"])
    return tools


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--check", action="store_true",
                    help="verify tools.json is up to date instead of writing it")
    args = ap.parse_args()

    if not SERVER.exists():
        print(f"server.py not found at {SERVER}")
        return 2

    tools = build()
    payload = json.dumps(tools, indent=2, ensure_ascii=False) + "\n"

    untyped = [
        t["name"] for t in tools
        if any(not p for p in t["inputSchema"]["properties"].values())
    ]

    if args.check:
        if not OUTPUT.exists():
            print(f"FAIL  {OUTPUT.name} does not exist - run without --check")
            return 1
        if OUTPUT.read_text(encoding="utf-8") != payload:
            print(f"FAIL  {OUTPUT.name} is stale - regenerate with:")
            print("        python build/generate_tool_schemas.py")
            return 1
        print(f"  PASS  {OUTPUT.name} is up to date ({len(tools)} tools)")
        return 0

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(payload, encoding="utf-8")

    print(f"  wrote {OUTPUT.relative_to(REPO)}")
    print(f"  tools: {len(tools)}")
    required_total = sum(len(t["inputSchema"]["required"]) for t in tools)
    props_total = sum(len(t["inputSchema"]["properties"]) for t in tools)
    print(f"  parameters: {props_total} ({required_total} required)")
    if untyped:
        print(f"  note: {len(untyped)} tool(s) have at least one untyped parameter "
              f"(permissive schema): {', '.join(untyped[:5])}"
              + (" ..." if len(untyped) > 5 else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
