"""
Runtime verification harness for the AutoCAD MCP plugin.

Talks straight to the plugin's TCP JSON-RPC port, exercising a representative
tool from every category and chaining real entity handles through
create -> query -> modify, exactly as an AI client would.

This is an INTEGRATION test against a live AutoCAD, not a unit test.

Prerequisites
-------------
  1. AutoCAD is running with a drawing open.
  2. The plugin is loaded and MCPSTART has been issued (listening on :8081).

Usage
-----
    python tests/runtime_verify.py                # core smoke run
    python tests/runtime_verify.py --all          # every registered tool
    python tests/runtime_verify.py --port 8081

Results are reported as PASS / FAIL(errorCode) / SKIP with a summary, and the
exit code is non-zero if anything failed, so CI can gate on it.
"""

from __future__ import annotations

import argparse
import json
import socket
import sys

HOST = "localhost"
DEFAULT_PORT = 8081

# Tools deliberately not auto-exercised: they need real files, real plotters,
# or would disturb the user's session.
SKIP_ALWAYS = {
    "drawing_new", "drawing_open", "drawing_save",   # would swap the document
    "execute_command",                                # arbitrary command string
    "plot_to_pdf", "plot_layout",                     # needs a real output path
    "attach_xref", "import_block", "export_block_to_file",
    "read_external_dwg", "batch_query_dwgs",          # need real DWG paths
    "create_table_from_excel",                        # python-side, needs .xlsx
    "capture_screenshot",                             # returns a large blob
    "set_server_options",                             # would change safety posture
    "purge_drawing", "overkill", "undo_last",         # destructive on a real drawing
}


class Client:
    def __init__(self, host: str = HOST, port: int = DEFAULT_PORT, timeout: float = 30.0):
        self.sock = socket.create_connection((host, port), timeout=timeout)
        self.sock.settimeout(timeout)
        self._buf = b""
        self._id = 0

    def call(self, method: str, params: dict | None = None) -> dict:
        self._id += 1
        req = {"jsonrpc": "2.0", "method": method, "params": params or {}, "id": self._id}
        self.sock.sendall((json.dumps(req) + "\n").encode("utf-8"))

        while b"\n" not in self._buf:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise ConnectionError("plugin closed the connection")
            self._buf += chunk

        line, self._buf = self._buf.split(b"\n", 1)
        return json.loads(line.decode("utf-8"))

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass


class Runner:
    def __init__(self, client: Client, verbose: bool = False):
        self.c = client
        self.verbose = verbose
        self.passed: list[str] = []
        self.failed: list[tuple[str, str]] = []
        self.skipped: list[str] = []

    def run(self, label: str, method: str, params: dict | None = None) -> dict | None:
        """Call a tool; record the outcome and return its result payload."""
        try:
            resp = self.c.call(method, params)
        except Exception as exc:  # noqa: BLE001 - report any transport failure
            self.failed.append((label, f"transport: {exc}"))
            print(f"  FAIL  {label:<34} transport error: {exc}")
            return None

        if "error" in resp:
            err = resp["error"]
            code = (err.get("data") or {}).get("errorCode", "?")
            msg = err.get("message", "")
            self.failed.append((label, f"{code}: {msg}"))
            print(f"  FAIL  {label:<34} [{code}] {msg[:70]}")
            return None

        self.passed.append(label)
        result = resp.get("result")
        if self.verbose:
            print(f"  PASS  {label:<34} {json.dumps(result)[:80]}")
        else:
            print(f"  PASS  {label}")
        return result

    def skip(self, label: str, why: str) -> None:
        self.skipped.append(label)
        print(f"  SKIP  {label:<34} {why}")

    def summary(self) -> int:
        total = len(self.passed) + len(self.failed)
        print()
        print("=" * 62)
        print(f"  {len(self.passed)}/{total} passed"
              f"   |  {len(self.failed)} failed  |  {len(self.skipped)} skipped")
        if self.failed:
            print("\n  Failures:")
            for name, why in self.failed:
                print(f"    - {name}: {why}")
        print("=" * 62)
        return 1 if self.failed else 0


def core_suite(r: Runner) -> None:
    """Representative tool from every category, chaining real handles."""

    print("\n[ System / introspection ]")
    caps = r.run("get_capabilities", "get_capabilities")
    if caps:
        print(f"        -> {caps.get('tool_count')} tools, "
              f"{caps.get('target_framework')}, {caps.get('supports')}")
    r.run("list_methods", "list_methods")
    r.run("system_status", "system_status")
    r.run("get_server_options", "get_server_options")

    print("\n[ Layers ]")
    r.run("create_layer", "create_layer", {"name": "MCP_VERIFY", "color": 3})
    r.run("list_layers", "list_layers")
    r.run("set_layer_properties", "set_layer_properties",
          {"name": "MCP_VERIFY", "color": 5})
    r.run("save_layer_state", "save_layer_state",
          {"name": "MCP_VERIFY_STATE", "overwrite": True})
    r.run("list_layer_states", "list_layer_states")
    r.run("restore_layer_state", "restore_layer_state", {"name": "MCP_VERIFY_STATE"})

    print("\n[ Entity creation ]")
    line = r.run("create_line", "create_line",
                 {"start": [0, 0], "end": [100, 0], "layer": "MCP_VERIFY"})
    circle = r.run("create_circle", "create_circle",
                   {"center": [50, 50], "radius": 25, "layer": "MCP_VERIFY"})
    r.run("create_point", "create_point", {"position": [10, 10], "layer": "MCP_VERIFY"})
    r.run("create_polygon", "create_polygon",
          {"center": [200, 0], "sides": 6, "radius": 30, "layer": "MCP_VERIFY"})
    r.run("create_donut", "create_donut",
          {"center": [300, 0], "outer_diameter": 40, "inner_diameter": 20})
    r.run("create_xline", "create_xline", {"point": [0, 200], "angle": 45})
    rect = r.run("create_rectangle", "create_rectangle",
                 {"corner1": [0, 300], "corner2": [100, 400], "layer": "MCP_VERIFY"})
    r.run("create_text", "create_text",
          {"position": [0, 500], "text": "MCP verify", "height": 10})

    print("\n[ Text measurement ]")
    r.run("measure_text", "measure_text", {"text": "MCP verify", "height": 10})
    r.run("measure_texts", "measure_texts",
          {"items": [{"text": "A", "height": 5}, {"text": "BB", "height": 5}]})

    print("\n[ Query / measurement ]")
    r.run("list_entities", "list_entities", {"layer": "MCP_VERIFY"})
    if line:
        lid = line["id"]
        r.run("get_entity", "get_entity", {"id": lid})
        r.run("get_bounding_box", "get_bounding_box", {"id": lid})
        r.run("divide_entity", "divide_entity", {"id": lid, "segments": 4})
        r.run("measure_entity", "measure_entity", {"id": lid, "interval": 25})
    r.run("measure_distance", "measure_distance", {"point1": [0, 0], "point2": [3, 4]})
    r.run("select_by_window", "select_by_window",
          {"corner1": [-10, -10], "corner2": [400, 600]})
    r.run("entity_count_report", "entity_count_report", {"by_layer": True})
    r.run("audit_drawing", "audit_drawing")

    print("\n[ Modify ]")
    if circle:
        cid = circle["id"]
        r.run("move_entity", "move_entity", {"id": cid, "offset": [10, 10]})
        r.run("copy_entity", "copy_entity", {"id": cid, "offset": [0, 100]})
        r.run("set_entity_properties", "set_entity_properties", {"id": cid, "color": 1})
        r.run("set_draworder", "set_draworder", {"ids": [cid], "position": "top"})
        r.run("flatten_entities", "flatten_entities", {"ids": [cid], "z": 0})
    if rect:
        r.run("polyline_edit", "polyline_edit", {"id": rect["id"], "closed": True})
        r.run("reverse_polyline", "reverse_polyline", {"id": rect["id"]})
        r.run("create_region", "create_region", {"ids": [rect["id"]]})

    print("\n[ Fillet (real geometry) ]")
    fa = r.run("fillet:line_a", "create_line", {"start": [600, 0], "end": [700, 0]})
    fb = r.run("fillet:line_b", "create_line", {"start": [700, 0], "end": [700, 100]})
    if fa and fb:
        r.run("fillet_entities", "fillet_entities",
              {"id1": fa["id"], "id2": fb["id"], "radius": 20})

    print("\n[ Blocks ]")
    r.run("list_blocks", "list_blocks")
    r.run("count_block_references", "count_block_references")

    print("\n[ Layouts / paper space ]")
    layouts = r.run("list_layouts", "list_layouts")
    r.run("create_layout", "create_layout", {"name": "MCP_VERIFY_LAYOUT"})
    r.run("get_page_setup", "get_page_setup", {"layout": "MCP_VERIFY_LAYOUT"})
    r.run("list_plot_devices", "list_plot_devices")
    r.run("list_paper_sizes", "list_paper_sizes", {"layout": "MCP_VERIFY_LAYOUT"})
    vp = r.run("create_viewport", "create_viewport",
               {"layout": "MCP_VERIFY_LAYOUT", "center": [420, 297],
                "width": 200, "height": 150, "scale": "1:100"})
    r.run("list_viewports", "list_viewports", {"layout": "MCP_VERIFY_LAYOUT"})
    if vp:
        r.run("set_viewport_scale", "set_viewport_scale",
              {"id": vp["id"], "scale": "1:50"})
        r.run("lock_viewport", "lock_viewport", {"id": vp["id"], "locked": True})

    print("\n[ 3D solids ]")
    box = r.run("create_box", "create_box",
                {"center": [1000, 0, 0], "length": 50, "width": 50, "height": 50})
    sph = r.run("create_sphere", "create_sphere", {"center": [1030, 0, 0], "radius": 30})
    r.run("create_cylinder", "create_cylinder",
          {"center": [1100, 0, 0], "radius": 20, "height": 60})
    if box:
        r.run("get_solid_properties", "get_solid_properties", {"id": box["id"]})
    if box and sph:
        r.run("boolean_solids", "boolean_solids",
              {"target": box["id"], "others": [sph["id"]], "operation": "union"})

    print("\n[ Groups / views / UCS ]")
    if line and circle:
        r.run("create_group", "create_group",
              {"name": "MCP_VERIFY_GROUP", "ids": [line["id"], circle["id"]]})
    r.run("list_groups", "list_groups")
    r.run("create_named_view", "create_named_view",
          {"name": "MCP_VERIFY_VIEW", "min": [0, 0], "max": [500, 500]})
    r.run("list_named_views", "list_named_views")
    r.run("list_ucs", "list_ucs")
    r.run("set_ucs", "set_ucs", {"name": "World"})

    print("\n[ Drawing data ]")
    if line:
        r.run("set_xdata", "set_xdata",
              {"id": line["id"], "app_name": "MCP_VERIFY", "values": ["tag", 42]})
        r.run("get_xdata", "get_xdata", {"id": line["id"], "app_name": "MCP_VERIFY"})
    r.run("get_drawing_properties", "get_drawing_properties")
    r.run("set_drawing_properties", "set_drawing_properties",
          {"comments": "Touched by AutoCAD MCP runtime verification"})

    print("\n[ Xrefs ]")
    r.run("list_xrefs", "list_xrefs")

    print("\n[ Safety gate: destructive confirmation ]")
    # Expect this to be REFUSED with NeedsConfirm — that is the passing outcome.
    if circle:
        resp = r.c.call("erase_entity", {"id": circle["id"]})
        code = (resp.get("error", {}).get("data") or {}).get("errorCode")
        if code == "NeedsConfirm":
            r.passed.append("destructive gate blocks unconfirmed erase")
            print("  PASS  destructive gate blocks unconfirmed erase")
        elif "error" not in resp:
            r.failed.append(("destructive gate", "erase succeeded WITHOUT __confirm"))
            print("  FAIL  destructive gate       erase succeeded without __confirm")
        else:
            r.failed.append(("destructive gate", f"unexpected error {code}"))
            print(f"  FAIL  destructive gate       unexpected errorCode {code}")

        # And it should go through once confirmed.
        r.run("erase_entity (confirmed)", "erase_entity",
              {"id": circle["id"], "__confirm": True})

    print("\n[ View ]")
    r.run("zoom_extents", "zoom_extents")


def all_tools_suite(r: Runner) -> None:
    """Invoke every read-only registered tool with no arguments as a crash check."""
    listing = r.run("list_methods", "list_methods")
    if not listing:
        return

    methods = sorted(listing.get("methods", []))
    print(f"\n[ Sweeping {len(methods)} registered tools (read-only, no args) ]")

    for m in methods:
        if m in SKIP_ALWAYS:
            r.skip(m, "excluded from automated sweep")
            continue

        resp = r.c.call(m, {})
        if "error" not in resp:
            r.passed.append(m)
            print(f"  PASS  {m}")
            continue

        err = resp["error"]
        code = (err.get("data") or {}).get("errorCode", "?")
        # Missing arguments / confirmation are correct behaviour for a bare call.
        if code in ("InvalidParam", "NeedsConfirm", "NotFound", "Unsupported"):
            r.skipped.append(m)
            print(f"  ARGS  {m:<34} needs arguments ({code}) - expected")
        else:
            r.failed.append((m, f"{code}: {err.get('message','')}"))
            print(f"  FAIL  {m:<34} [{code}] {err.get('message','')[:60]}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--port", type=int, default=DEFAULT_PORT)
    ap.add_argument("--host", default=HOST)
    ap.add_argument("--all", action="store_true",
                    help="also sweep every registered tool with no arguments")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    print("=" * 62)
    print("  AutoCAD MCP - runtime verification")
    print(f"  connecting to {args.host}:{args.port}")
    print("=" * 62)

    try:
        client = Client(args.host, args.port)
    except OSError as exc:
        print(f"\n  Could not connect: {exc}")
        print("  Is AutoCAD running with the plugin loaded and MCPSTART issued?")
        return 2

    r = Runner(client, verbose=args.verbose)
    try:
        core_suite(r)
        if args.all:
            all_tools_suite(r)
    finally:
        client.close()

    return r.summary()


if __name__ == "__main__":
    sys.exit(main())
