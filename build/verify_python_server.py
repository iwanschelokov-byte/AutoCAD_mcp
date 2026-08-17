"""
Verify the Python MCP server actually loads and registers its tools.

Syntax checks are not enough. FastMCP validates every tool signature at import
time and rejects things a parser is perfectly happy with - most notably a
parameter whose name starts with an underscore:

    InvalidSignature: Parameter __confirm of delete_layout cannot start with '_'

That shipped undetected because `ast.parse` and `compileall` only prove the file
is syntactically valid, not that FastMCP will accept it. The server was dead on
arrival and nothing caught it. This gate imports the module for real.

Needs the `mcp` package installed; it does NOT need AutoCAD.
"""

from __future__ import annotations

import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SERVER_DIR = REPO / "autocad-plugin" / "src" / "mcp_server"


def main() -> int:
    if not SERVER_DIR.exists():
        print(f"  server directory not found: {SERVER_DIR}")
        return 2

    sys.path.insert(0, str(SERVER_DIR))

    try:
        import server  # noqa: PLC0415 - the import IS the test
    except ImportError as exc:
        # A missing third-party package is an environment problem, not a defect
        # in the server, so do not fail the build over it.
        if "mcp" in str(exc).lower():
            print(f"  SKIP  mcp package not installed ({exc})")
            return 0
        print(f"  FAIL  server.py could not be imported: {exc}")
        return 1
    except Exception as exc:  # noqa: BLE001 - surface whatever FastMCP objected to
        print(f"  FAIL  server.py failed to load: {type(exc).__name__}: {exc}")
        return 1

    failures = 0

    tools = server.mcp._tool_manager.list_tools()
    if not tools:
        print("  FAIL  no tools registered")
        failures += 1
    else:
        print(f"  PASS  server imports and registers {len(tools)} tools")

    # Every tool needs a description; an empty one leaves the model guessing.
    undescribed = [t.name for t in tools if not (t.description or "").strip()]
    if undescribed:
        print(f"  FAIL  {len(undescribed)} tool(s) have no description: "
              f"{', '.join(sorted(undescribed)[:5])}")
        failures += 1
    else:
        print(f"  PASS  every tool has a description")

    # FastMCP rejects leading underscores; catch it here rather than at runtime.
    bad_params = []
    for t in tools:
        props = (t.parameters or {}).get("properties", {})
        bad_params += [f"{t.name}.{p}" for p in props if p.startswith("_")]
    if bad_params:
        print(f"  FAIL  parameters starting with '_': {', '.join(bad_params[:5])}")
        failures += 1
    else:
        print("  PASS  no parameter names start with '_'")

    duplicates = {t.name for t in tools if [x.name for x in tools].count(t.name) > 1}
    if duplicates:
        print(f"  FAIL  duplicate tool names: {', '.join(sorted(duplicates))}")
        failures += 1
    else:
        print("  PASS  no duplicate tool names")

    print()
    if failures:
        print(f"  {failures} check(s) FAILED\n")
        return 1
    print("  All Python server checks passed.\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
