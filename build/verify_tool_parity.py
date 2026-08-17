"""
Verify that the MCP server and the AutoCAD plugin agree on the tool surface.

Two halves of this project are maintained by hand and must stay in sync:

  * C#     - one ICommand class per tool, registered in Core/CommandRegistry.cs
  * Python - one @mcp.tool() wrapper per tool in mcp_server/server.py

Drift is silent and nasty in both directions: a Python tool calling a method the
plugin does not implement fails only at runtime, and a registered C# command with
no wrapper is unreachable by any AI client (which is exactly how measure_text sat
unused for a whole release).

This runs in CI and needs neither AutoCAD nor a build.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
COMMANDS_DIR = REPO / "autocad-plugin" / "src" / "AutoCADMCPPlugin" / "Commands"
REGISTRY = REPO / "autocad-plugin" / "src" / "AutoCADMCPPlugin" / "Core" / "CommandRegistry.cs"
SERVER = REPO / "autocad-plugin" / "src" / "mcp_server" / "server.py"

# Tools implemented entirely in the Python layer, with no plugin counterpart.
PYTHON_ONLY = {"create_table_from_excel"}

# Helpers in server.py that dispatch a method to the plugin. Both must be listed
# or a tool routed through the unlisted one looks unreachable:
#   _call -> returns the response as formatted JSON text
#   _raw  -> returns the decoded object, for tools that post-process it
DISPATCHERS = ("_call", "_raw")


def fail(msg: str, items: list[str] | None = None) -> None:
    print(f"  FAIL  {msg}")
    for i in sorted(items or []):
        print(f"          - {i}")


def main() -> int:
    for p in (COMMANDS_DIR, REGISTRY, SERVER):
        if not p.exists():
            print(f"missing expected path: {p}")
            return 2

    # --- C# side ----------------------------------------------------------
    declared: dict[str, str] = {}      # method name -> file it was declared in
    duplicates: list[str] = []
    for cs in sorted(COMMANDS_DIR.glob("*.cs")):
        text = cs.read_text(encoding="utf-8", errors="replace")
        for m in re.findall(r'MethodName\s*=>\s*"([a-z0-9_]+)"', text):
            if m in declared:
                duplicates.append(f"{m} (in {declared[m]} and {cs.name})")
            declared[m] = cs.name

    # Class names that are actually registered.
    reg_text = REGISTRY.read_text(encoding="utf-8", errors="replace")
    registered_classes = re.findall(r"Register\(\s*new\s+([A-Za-z0-9_]+)\s*\(", reg_text)

    # Concrete command classes. Some derive from an intermediate abstract base
    # (e.g. XrefActionCommand), so inheritance is resolved transitively and
    # abstract classes are excluded - they are never registered by design.
    bases: dict[str, str] = {}
    abstract: set[str] = set()
    for cs in sorted(COMMANDS_DIR.glob("*.cs")):
        text = cs.read_text(encoding="utf-8", errors="replace")
        for mods, cls, base in re.findall(
            r"(?:public\s+)?((?:abstract\s+)?)class\s+([A-Za-z0-9_]+)\s*:\s*([A-Za-z0-9_]+)",
            text,
        ):
            bases[cls] = base
            if "abstract" in mods:
                abstract.add(cls)

    ROOTS = {"AcadCommand", "DirectCommand"}

    def is_command(cls: str, _seen: set[str] | None = None) -> bool:
        _seen = _seen or set()
        if cls in _seen:
            return False
        _seen.add(cls)
        base = bases.get(cls)
        if base is None:
            return False
        return base in ROOTS or is_command(base, _seen)

    declared_classes = {c for c in bases if c not in abstract and is_command(c)}

    # --- Python side ------------------------------------------------------
    srv = SERVER.read_text(encoding="utf-8", errors="replace")
    tool_names = set(re.findall(r"@mcp\.tool\(\)\s*\nasync def ([a-z0-9_]+)", srv))
    dispatch_re = r'(?:' + '|'.join(DISPATCHERS) + r')\(\s*"([a-z0-9_]+)"'
    call_targets = set(re.findall(dispatch_re, srv))

    # --- Checks -----------------------------------------------------------
    ok = True
    print(f"\n  C# command classes declared : {len(declared_classes)}")
    print(f"  C# methods declared         : {len(declared)}")
    print(f"  C# Register(new ...) calls  : {len(registered_classes)}")
    print(f"  Python @mcp.tool()          : {len(tool_names)}")
    print(f"  Python dispatch targets     : {len(call_targets)}\n")

    if duplicates:
        fail("duplicate MethodName declarations (later registration silently wins)",
             duplicates)
        ok = False

    dup_reg = [c for c in registered_classes if registered_classes.count(c) > 1]
    if dup_reg:
        fail("classes registered more than once", sorted(set(dup_reg)))
        ok = False

    unregistered = declared_classes - set(registered_classes)
    if unregistered:
        fail("command classes never registered - unreachable dead code", sorted(unregistered))
        ok = False

    ghost = set(registered_classes) - declared_classes
    if ghost:
        fail("registered classes with no MethodName declaration", sorted(ghost))
        ok = False

    orphan_calls = call_targets - set(declared) - PYTHON_ONLY
    if orphan_calls:
        fail("Python tools calling methods the plugin does not implement", sorted(orphan_calls))
        ok = False

    unexposed = set(declared) - call_targets
    if unexposed:
        fail("plugin commands with no MCP tool - unreachable by AI clients", sorted(unexposed))
        ok = False

    # Every wrapper should route to the plugin (or be a known Python-only tool).
    unrouted = tool_names - call_targets - PYTHON_ONLY
    if unrouted:
        fail("@mcp.tool() functions that never dispatch to the plugin", sorted(unrouted))
        ok = False

    if ok:
        total = len(declared) + len(PYTHON_ONLY)
        print(f"  PASS  tool surfaces agree - {len(declared)} plugin commands, "
              f"{len(PYTHON_ONLY)} python-only, {total} MCP tools total\n")
        return 0

    print("\n  Tool surface parity check FAILED\n")
    return 1


if __name__ == "__main__":
    sys.exit(main())
