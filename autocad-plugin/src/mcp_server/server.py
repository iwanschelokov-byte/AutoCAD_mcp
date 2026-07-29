"""
AutoCAD MCP Server

Exposes AutoCAD operations as MCP tools that AI assistants (Claude) can call.
Communicates with the AutoCAD .NET plugin via TCP socket using JSON-RPC 2.0.

Architecture:
    Claude (MCP Client) -> stdio -> This Server -> TCP socket -> AutoCAD Plugin -> AutoCAD API
"""

import os
import json
import asyncio
import logging
from mcp.server.fastmcp import FastMCP
from autocad_client import get_client

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("autocad-mcp")

HOST = os.environ.get("AUTOCAD_MCP_HOST", "localhost")
PORT = int(os.environ.get("AUTOCAD_MCP_PORT", "8081"))

mcp = FastMCP("AutoCAD MCP Server")


# =============================================================================
# Helper
# =============================================================================

async def _raw(method: str, params: dict | None = None):
    """Send a command to AutoCAD and return the decoded result object."""
    client = await get_client(HOST, PORT)
    return await client.send_command(method, params)


async def _call(method: str, params: dict | None = None) -> str:
    """Send a command to AutoCAD and return the result as formatted text."""
    return json.dumps(await _raw(method, params), indent=2)


def _field(result, name):
    """Read a field from a plugin result, whether or not it is wrapped in 'data'."""
    if isinstance(result, dict):
        if name in result:
            return result[name]
        data = result.get("data")
        if isinstance(data, dict):
            return data.get(name)
    return None


# =============================================================================
# System Tools
# =============================================================================

@mcp.tool()
async def system_status() -> str:
    """Get the AutoCAD plugin status, version, and active document info."""
    return await _call("system_status")


@mcp.tool()
async def list_methods() -> str:
    """List all available methods/commands the AutoCAD plugin supports."""
    return await _call("list_methods")


@mcp.tool()
async def set_system_variable(name: str, value: float | int | str) -> str:
    """Set an AutoCAD system variable (e.g., DIMTXT, LTSCALE, OSMODE). Value can be number or string."""
    return await _call("set_system_variable", {"name": name, "value": value})


@mcp.tool()
async def get_system_variable(name: str) -> str:
    """Get the current value of an AutoCAD system variable."""
    return await _call("get_system_variable", {"name": name})


@mcp.tool()
async def execute_command(
    command: str,
    inputs: list[str] | None = None,
    check: bool = True,
    wait: float = 0.5,
) -> str:
    """Execute a raw AutoCAD command, optionally with all of its interactive inputs.

    The command and every item of `inputs` are joined into ONE space-separated
    string and sent to AutoCAD in a single call. Interactive / multi-step
    commands MUST be passed this way (command + all prompt responses together);
    splitting them across several execute_command calls does not work, because
    AutoCAD cancels a command that is still awaiting input when the next string
    is queued.

    The command runs asynchronously, so the send itself cannot report success.
    With check=True (default) this tool waits `wait` seconds and then reads the
    command log back, so the answer contains what AutoCAD actually did — an
    unknown command or a rejected input shows up under "outcome" instead of
    failing silently. Set check=False for fire-and-forget.

    Examples:
        execute_command("ZOOM E")
        execute_command("_.CIRCLE", ["100,100", "40"])
        execute_command("_.POLYGON", ["6", "400,250", "_I", "60"])
    """
    params: dict = {"command": command}
    if inputs:
        params["inputs"] = [str(i) for i in inputs]

    sent = await _raw("execute_command", params)
    if not check:
        return json.dumps(sent, indent=2)

    since = _field(sent, "since") or 0
    if wait > 0:
        await asyncio.sleep(min(wait, 10.0))

    try:
        outcome = await _raw("read_command_line", {"since": since, "limit": 20})
    except Exception as exc:  # the plugin may predate read_command_line
        outcome = {"error": f"could not read command log: {exc}"}

    return json.dumps({"sent": sent, "outcome": outcome}, indent=2)


@mcp.tool()
async def read_command_line(since: int = 0, limit: int = 20) -> str:
    """Read AutoCAD's recent command activity and the last command-line prompt.

    Pass the "since" value returned by execute_command to see exactly what that
    call produced: which commands started, ended, failed or were cancelled, plus
    the last line AutoCAD echoed (where messages like "Unknown command" appear).
    Call with since=0 for the whole recent history.
    """
    return await _call("read_command_line", {"since": since, "limit": limit})


# =============================================================================
# Drawing / Document Tools
# =============================================================================

@mcp.tool()
async def drawing_new(template: str = "") -> str:
    """Create a new drawing, optionally from a template file path."""
    return await _call("drawing_new", {"template": template})


@mcp.tool()
async def drawing_open(path: str, read_only: bool = False) -> str:
    """Open an existing .dwg file and make it the active drawing.

    If the file is already open it is simply activated (AutoCAD cannot open a
    file it already holds a lock on); the answer then says already_open=true.
    """
    return await _call("drawing_open", {"path": path, "read_only": read_only})


@mcp.tool()
async def drawing_save(path: str = "", mode: str = "copy") -> str:
    """Save the current drawing.

    With no path: saves in place (QSAVE).
    With a path, `mode` decides what "save as" means:
      - "copy" (default): writes the file, but the editing session keeps
        pointing at the original drawing.
      - "saveas": switches the editing session to the new file, like the
        SAVEAS command in the UI.
    """
    params = {}
    if path:
        params["path"] = path
        params["mode"] = mode
    return await _call("drawing_save", params)


@mcp.tool()
async def drawing_info() -> str:
    """Get info about the current drawing: name, path, entity count, layers."""
    return await _call("drawing_info")


@mcp.tool()
async def drawing_list() -> str:
    """List every drawing currently open in AutoCAD, marking the active one."""
    return await _call("drawing_list")


@mcp.tool()
async def drawing_close(path: str = "", save: bool = False) -> str:
    """Close a drawing. Closes the active drawing unless `path` names another one.

    save defaults to False: the drawing is closed and unsaved changes are
    DISCARDED. Pass save=True to write the file first. Closing is what releases
    AutoCAD's lock on the .dwg, so do it before another tool needs the file.
    """
    params: dict = {"save": save}
    if path:
        params["path"] = path
    return await _call("drawing_close", params)


@mcp.tool()
async def close_all(save: bool = False, keep: str = "") -> str:
    """Close every open drawing. `keep` optionally names one to leave open.

    save defaults to False — unsaved changes in every closed drawing are
    DISCARDED.
    """
    params: dict = {"save": save}
    if keep:
        params["keep"] = keep
    return await _call("close_all", params)


# =============================================================================
# Entity Creation Tools
# =============================================================================

@mcp.tool()
async def create_line(
    start: list[float],
    end: list[float],
    layer: str = "",
    color: int = -1
) -> str:
    """Draw a line from start [x,y] to end [x,y]. Optionally set layer and color (0-255 ACI)."""
    params = {"start": start, "end": end}
    if layer: params["layer"] = layer
    if color >= 0: params["color"] = color
    return await _call("create_line", params)


@mcp.tool()
async def create_circle(
    center: list[float],
    radius: float,
    layer: str = "",
    color: int = -1
) -> str:
    """Draw a circle at center [x,y] with given radius."""
    params = {"center": center, "radius": radius}
    if layer: params["layer"] = layer
    if color >= 0: params["color"] = color
    return await _call("create_circle", params)


@mcp.tool()
async def create_arc(
    center: list[float],
    radius: float,
    start_angle: float,
    end_angle: float,
    degrees: bool = True,
    layer: str = ""
) -> str:
    """Draw an arc. Angles in degrees by default. Set degrees=false for radians."""
    params = {
        "center": center, "radius": radius,
        "start_angle": start_angle, "end_angle": end_angle,
        "degrees": degrees
    }
    if layer: params["layer"] = layer
    return await _call("create_arc", params)


@mcp.tool()
async def create_polyline(
    points: list[list[float]],
    closed: bool = False,
    layer: str = ""
) -> str:
    """Draw a polyline through a list of points [[x1,y1], [x2,y2], ...]. Set closed=true to close it."""
    params = {"points": points, "closed": closed}
    if layer: params["layer"] = layer
    return await _call("create_polyline", params)


@mcp.tool()
async def create_rectangle(
    corner1: list[float],
    corner2: list[float],
    layer: str = ""
) -> str:
    """Draw a rectangle from corner1 [x,y] to corner2 [x,y]."""
    params = {"corner1": corner1, "corner2": corner2}
    if layer: params["layer"] = layer
    return await _call("create_rectangle", params)


@mcp.tool()
async def create_ellipse(
    center: list[float],
    major_radius: float,
    minor_radius: float,
    layer: str = ""
) -> str:
    """Draw an ellipse at center with major and minor radii."""
    params = {"center": center, "major_radius": major_radius, "minor_radius": minor_radius}
    if layer: params["layer"] = layer
    return await _call("create_ellipse", params)


@mcp.tool()
async def create_text(
    text: str,
    position: list[float],
    height: float = 2.5,
    rotation: float = 0,
    layer: str = ""
) -> str:
    """Place single-line text at position [x,y]. Height in drawing units, rotation in degrees."""
    params = {"text": text, "position": position, "height": height, "rotation": rotation}
    if layer: params["layer"] = layer
    return await _call("create_text", params)


@mcp.tool()
async def create_mtext(
    text: str,
    position: list[float],
    height: float = 2.5,
    width: float = 0,
    layer: str = ""
) -> str:
    """Place multi-line text at position [x,y]. Width=0 means auto-width."""
    params = {"text": text, "position": position, "height": height, "width": width}
    if layer: params["layer"] = layer
    return await _call("create_mtext", params)


@mcp.tool()
async def create_hatch(
    boundary: list[list[float]],
    pattern: str = "ANSI31",
    scale: float = 1.0,
    layer: str = ""
) -> str:
    """Create a hatch inside a boundary defined by points. Pattern examples: ANSI31, SOLID, DOTS."""
    params = {"boundary": boundary, "pattern": pattern, "scale": scale}
    if layer: params["layer"] = layer
    return await _call("create_hatch", params)


# =============================================================================
# Advanced Entity Creation Tools
# =============================================================================

@mcp.tool()
async def create_spline(
    points: list[list[float]],
    closed: bool = False,
    layer: str = "",
    color: int = -1
) -> str:
    """Draw a smooth spline curve through points [[x1,y1], ...]. Set closed=true for closed spline."""
    params = {"points": points, "closed": closed}
    if layer: params["layer"] = layer
    if color >= 0: params["color"] = color
    return await _call("create_spline", params)


@mcp.tool()
async def create_table(
    position: list[float],
    rows: int = 3,
    columns: int = 3,
    row_height: float = 500,
    column_width: float = 2000,
    title: str = "",
    data: list[list[str]] | None = None,
    layer: str = ""
) -> str:
    """Create a table at position [x,y] with rows/columns. Data is 2D array of cell strings."""
    params: dict = {"position": position, "rows": rows, "columns": columns,
                    "row_height": row_height, "column_width": column_width}
    if title: params["title"] = title
    if data: params["data"] = data
    if layer: params["layer"] = layer
    return await _call("create_table", params)


@mcp.tool()
async def bulk_create(entities: list[dict]) -> str:
    """Create multiple entities in one call for performance. Each item: {type: 'line'|'circle'|'arc'|'polyline'|'rectangle'|'text'|'mtext'|'ellipse', params: {...}}."""
    return await _call("bulk_create", {"entities": entities})


# =============================================================================
# Entity Query & Modification Tools
# =============================================================================

@mcp.tool()
async def list_entities(
    layer: str = "",
    type: str = "",
    limit: int = 500,
    offset: int = 0,
    detailed: bool = False,
    min_point: list[float] | None = None,
    max_point: list[float] | None = None,
    mode: str = "crossing",
) -> str:
    """List entities in model space, with filtering and paging.

    `type` accepts any spelling of the type: "BlockReference",
    "AcDbBlockReference", "INSERT" or the alias "block" all work, and several
    can be given comma-separated ("Line,Arc,LWPOLYLINE").

    Supply min_point and max_point to list only a region — "crossing" (default)
    includes anything the box touches, "window" only what is fully inside.

    The answer carries `total` and `truncated`, so you can tell "that is all of
    them" from "ask for the next page with offset".
    """
    params: dict = {"limit": limit, "offset": offset, "detailed": detailed}
    if layer: params["layer"] = layer
    if type: params["type"] = type
    if min_point and max_point:
        params["min_point"] = min_point
        params["max_point"] = max_point
        params["mode"] = mode
    return await _call("list_entities", params)


@mcp.tool()
async def get_entity(handle: str) -> str:
    """Get detailed info about a specific entity by its handle ID."""
    return await _call("get_entity", {"handle": handle})


@mcp.tool()
async def get_entities(handles: list[str], detailed: bool = True) -> str:
    """Get info about several entities at once, by handle. One call instead of N."""
    return await _call("get_entities", {"handles": [str(h) for h in handles], "detailed": detailed})


@mcp.tool()
async def erase_entity(handle: str) -> str:
    """Delete an entity by its handle ID."""
    return await _call("erase_entity", {"handle": handle})


@mcp.tool()
async def move_entity(
    handle: str,
    from_point: list[float],
    to_point: list[float]
) -> str:
    """Move an entity from one point to another."""
    return await _call("move_entity", {"handle": handle, "from": from_point, "to": to_point})


@mcp.tool()
async def copy_entity(
    handle: str,
    from_point: list[float],
    to_point: list[float]
) -> str:
    """Copy an entity from one point to another. Returns the new entity's handle."""
    return await _call("copy_entity", {"handle": handle, "from": from_point, "to": to_point})


@mcp.tool()
async def rotate_entity(
    handle: str,
    base_point: list[float],
    angle: float
) -> str:
    """Rotate an entity around a base point by an angle in degrees."""
    return await _call("rotate_entity", {"handle": handle, "base_point": base_point, "angle": angle})


@mcp.tool()
async def scale_entity(
    handle: str,
    base_point: list[float],
    factor: float
) -> str:
    """Scale an entity from a base point by a scale factor."""
    return await _call("scale_entity", {"handle": handle, "base_point": base_point, "factor": factor})


@mcp.tool()
async def mirror_entity(
    handle: str,
    mirror_line_start: list[float],
    mirror_line_end: list[float],
    erase_source: bool = False
) -> str:
    """Mirror an entity across a line. Set erase_source=true to remove the original."""
    return await _call("mirror_entity", {
        "handle": handle,
        "mirror_line_start": mirror_line_start,
        "mirror_line_end": mirror_line_end,
        "erase_source": erase_source
    })


@mcp.tool()
async def set_entity_properties(
    handle: str,
    layer: str = "",
    color: int = -1,
    linetype: str = "",
    lineweight: int = -1
) -> str:
    """Change properties of an existing entity: layer, color (ACI 0-255), linetype, lineweight."""
    params: dict = {"handle": handle}
    if layer: params["layer"] = layer
    if color >= 0: params["color"] = color
    if linetype: params["linetype"] = linetype
    if lineweight >= 0: params["lineweight"] = lineweight
    return await _call("set_entity_properties", params)


@mcp.tool()
async def offset_entity(
    handle: str,
    distance: float,
    side: str = "both"
) -> str:
    """Offset a curve entity (line/arc/polyline/circle) by distance. Side: 'left', 'right', or 'both'."""
    return await _call("offset_entity", {"handle": handle, "distance": distance, "side": side})


@mcp.tool()
async def explode_entity(handle: str, erase_original: bool = True) -> str:
    """Explode a block/polyline/dimension into primitive entities. Returns new entity handles."""
    return await _call("explode_entity", {"handle": handle, "erase_original": erase_original})


@mcp.tool()
async def array_rectangular(
    handle: str,
    rows: int,
    columns: int,
    row_spacing: float,
    column_spacing: float
) -> str:
    """Create rectangular array of entity. Returns handles of new copies (original stays)."""
    return await _call("array_rectangular", {
        "handle": handle, "rows": rows, "columns": columns,
        "row_spacing": row_spacing, "column_spacing": column_spacing
    })


@mcp.tool()
async def array_polar(
    handle: str,
    center: list[float],
    count: int,
    total_angle: float = 360,
    rotate_items: bool = True
) -> str:
    """Create polar (circular) array of entity around center point."""
    return await _call("array_polar", {
        "handle": handle, "center": center, "count": count,
        "total_angle": total_angle, "rotate_items": rotate_items
    })


@mcp.tool()
async def join_entities(handles: list[str]) -> str:
    """Join contiguous lines/arcs into a single polyline. First handle must be a polyline."""
    return await _call("join_entities", {"handles": handles})


@mcp.tool()
async def bulk_erase(
    handles: list[str] | None = None,
    layer: str = "",
    type: str = ""
) -> str:
    """Erase multiple entities at once. By handles list, or by layer/type filter."""
    params: dict = {}
    if handles: params["handles"] = handles
    if layer: params["layer"] = layer
    if type: params["type"] = type
    return await _call("bulk_erase", params)


@mcp.tool()
async def undo_last(count: int = 1) -> str:
    """Undo the last N operations in AutoCAD."""
    return await _call("undo_last", {"count": count})


# =============================================================================
# Query & Measurement Tools
# =============================================================================

@mcp.tool()
async def measure_distance(point1: list[float], point2: list[float]) -> str:
    """Measure distance between two points. Returns distance, dx, dy, angle."""
    return await _call("measure_distance", {"point1": point1, "point2": point2})


@mcp.tool()
async def measure_area(handle: str) -> str:
    """Measure area of a closed entity (polyline, circle, ellipse, hatch). Returns area and perimeter."""
    return await _call("measure_area", {"handle": handle})


@mcp.tool()
async def get_bounding_box(handle: str) -> str:
    """Get the bounding box of an entity. Returns min_point, max_point, width, height."""
    return await _call("get_bounding_box", {"handle": handle})


@mcp.tool()
async def select_by_window(
    min_point: list[float],
    max_point: list[float],
    limit: int = 500,
    mode: str = "window",
    layer: str = "",
    type: str = "",
    offset: int = 0,
    detailed: bool = False,
) -> str:
    """Find entities in a rectangular region.

    mode="window" (default) returns only entities fully inside the box;
    mode="crossing" also returns anything the box touches — use it when picking
    a title block or a sheet region, whose entities stick out past the frame.

    Optional layer/type filters narrow the result; `type` accepts "INSERT",
    "AcDbBlockReference", "BlockReference" or "block" alike. The answer carries
    `total` and `truncated` so a cut-off list is never mistaken for a complete one.
    """
    params: dict = {
        "min_point": min_point,
        "max_point": max_point,
        "limit": limit,
        "offset": offset,
        "mode": mode,
        "detailed": detailed,
    }
    if layer: params["layer"] = layer
    if type: params["type"] = type
    return await _call("select_by_window", params)


@mcp.tool()
async def select_by_properties(
    layer: str = "",
    type: str = "",
    color: int = -1,
    linetype: str = "",
    limit: int = 500,
    offset: int = 0,
    detailed: bool = False,
    block_name: str = "",
) -> str:
    """Find entities matching property filters (AND logic).

    `type` accepts the .NET name ("BlockReference"), the AutoCAD class name
    ("AcDbBlockReference"), the DXF name ("INSERT") or an alias ("block",
    "text", "anytext", "pline", "dimension", "curve"), and a comma-separated
    list of any of those. `block_name` further restricts block references to
    one block definition.
    """
    params: dict = {"limit": limit, "offset": offset, "detailed": detailed}
    if layer: params["layer"] = layer
    if type: params["type"] = type
    if color >= 0: params["color"] = color
    if linetype: params["linetype"] = linetype
    if block_name: params["block_name"] = block_name
    return await _call("select_by_properties", params)


@mcp.tool()
async def find_intersections(handle1: str, handle2: str) -> str:
    """Find intersection points between two curve entities."""
    return await _call("find_intersections", {"handle1": handle1, "handle2": handle2})


@mcp.tool()
async def search_text(keyword: str, case_sensitive: bool = False, limit: int = 100) -> str:
    """Search ALL text in the drawing (DBText, MText, block attributes, block names) for a keyword. Returns matching text with positions. Use this to find rooms, labels, equipment, annotations by name."""
    return await _call("search_text", {"keyword": keyword, "case_sensitive": case_sensitive, "limit": limit})


@mcp.tool()
async def find_nearest(
    point: list[float],
    radius: float = 0,
    type: str = "",
    layer: str = "",
    limit: int = 20
) -> str:
    """Find entities nearest to a point [x,y], sorted by distance. Filter by type/layer. Set radius=0 for unlimited range."""
    params: dict = {"point": point, "limit": limit}
    if radius > 0:
        params["radius"] = radius
    if type:
        params["type"] = type
    if layer:
        params["layer"] = layer
    return await _call("find_nearest", params)


@mcp.tool()
async def measure_between(handle1: str, handle2: str) -> str:
    """Measure distance between two entities (center-to-center and closest approach). Returns dx, dy, distances, and entity descriptions."""
    return await _call("measure_between", {"handle1": handle1, "handle2": handle2})


# =============================================================================
# Layer Tools
# =============================================================================

@mcp.tool()
async def list_layers() -> str:
    """List all layers with their properties (color, frozen, locked, current)."""
    return await _call("list_layers")


@mcp.tool()
async def create_layer(
    name: str,
    color: int = 7,
    set_current: bool = False,
    linetype: str = ""
) -> str:
    """Create a new layer with optional color (ACI 0-255) and linetype."""
    params = {"name": name, "color": color, "set_current": set_current}
    if linetype: params["linetype"] = linetype
    return await _call("create_layer", params)


@mcp.tool()
async def set_current_layer(name: str) -> str:
    """Set the active/current layer by name."""
    return await _call("set_current_layer", {"name": name})


@mcp.tool()
async def set_layer_properties(
    name: str,
    color: int = -1,
    is_off: bool | None = None,
    is_frozen: bool | None = None,
    is_locked: bool | None = None
) -> str:
    """Modify layer properties. Only specified parameters are changed."""
    params: dict = {"name": name}
    if color >= 0: params["color"] = color
    if is_off is not None: params["is_off"] = is_off
    if is_frozen is not None: params["is_frozen"] = is_frozen
    if is_locked is not None: params["is_locked"] = is_locked
    return await _call("set_layer_properties", params)


@mcp.tool()
async def delete_layer(name: str) -> str:
    """Delete a layer. Entities on it are moved to layer '0'. Cannot delete '0' or current layer."""
    return await _call("delete_layer", {"name": name})


@mcp.tool()
async def rename_layer(old_name: str, new_name: str) -> str:
    """Rename a layer. Cannot rename layer '0'."""
    return await _call("rename_layer", {"old_name": old_name, "new_name": new_name})


# =============================================================================
# Style Tools
# =============================================================================

@mcp.tool()
async def create_dimension_style(
    name: str,
    text_height: float = 0,
    arrow_size: float = 0,
    linear_scale_factor: float = 0,
    decimal_places: int = -1,
    text_above: bool | None = None,
    text_color: int = -1,
    dim_line_color: int = -1,
    ext_line_color: int = -1,
    suffix: str = "",
    text_style: str = "",
    extension_offset: float = 0,
    extension_extend: float = 0,
    text_gap: float = 0,
    set_current: bool = False
) -> str:
    """Create or update a dimension style. Fixes invisible dim text by setting proper text_height and arrow_size."""
    params: dict = {"name": name, "set_current": set_current}
    if text_height > 0: params["text_height"] = text_height
    if arrow_size > 0: params["arrow_size"] = arrow_size
    if linear_scale_factor > 0: params["linear_scale_factor"] = linear_scale_factor
    if decimal_places >= 0: params["decimal_places"] = decimal_places
    if text_above is not None: params["text_above"] = text_above
    if text_color >= 0: params["text_color"] = text_color
    if dim_line_color >= 0: params["dim_line_color"] = dim_line_color
    if ext_line_color >= 0: params["ext_line_color"] = ext_line_color
    if suffix: params["suffix"] = suffix
    if text_style: params["text_style"] = text_style
    if extension_offset > 0: params["extension_offset"] = extension_offset
    if extension_extend > 0: params["extension_extend"] = extension_extend
    if text_gap > 0: params["text_gap"] = text_gap
    return await _call("create_dimension_style", params)


@mcp.tool()
async def create_text_style(
    name: str,
    font: str = "Arial",
    height: float = 0,
    width_factor: float = 1.0,
    oblique_angle: float = 0,
    set_current: bool = False
) -> str:
    """Create or update a text style. Height=0 means variable (uses DIMTXT for dimensions)."""
    return await _call("create_text_style", {
        "name": name, "font": font, "height": height,
        "width_factor": width_factor, "oblique_angle": oblique_angle, "set_current": set_current
    })


@mcp.tool()
async def list_dimension_styles() -> str:
    """List all dimension styles with their properties."""
    return await _call("list_dimension_styles")


@mcp.tool()
async def list_text_styles() -> str:
    """List all text styles with their font, height, and width factor."""
    return await _call("list_text_styles")


# =============================================================================
# Block Tools
# =============================================================================

@mcp.tool()
async def list_blocks() -> str:
    """List all block definitions in the drawing with their attributes."""
    return await _call("list_blocks")


@mcp.tool()
async def create_block(
    name: str,
    handles: list[str],
    base_point: list[float] | None = None,
    erase_originals: bool = False
) -> str:
    """Create a new block definition from existing entities. Provide entity handles and a block name."""
    params: dict = {"name": name, "handles": handles, "erase_originals": erase_originals}
    if base_point: params["base_point"] = base_point
    return await _call("create_block", params)


@mcp.tool()
async def insert_block(
    name: str,
    position: list[float],
    rotation: float = 0,
    scale_x: float = 1.0,
    scale_y: float = 1.0,
    scale_z: float = 1.0,
    layer: str = "",
    attributes: dict | None = None
) -> str:
    """Insert a block by name at position [x,y]. Rotation in degrees. Attributes as {TAG: value}."""
    params: dict = {
        "name": name, "position": position, "rotation": rotation,
        "scale_x": scale_x, "scale_y": scale_y, "scale_z": scale_z
    }
    if layer: params["layer"] = layer
    if attributes: params["attributes"] = attributes
    return await _call("insert_block", params)


@mcp.tool()
async def import_block(
    source_path: str,
    block_names: list[str] | None = None
) -> str:
    """Import block definitions from an external DWG file into the current drawing.
    Use this to bring in blocks from other drawings without needing Design Center.
    After importing, use insert_block to place them. If block_names is omitted, all user blocks are imported."""
    params: dict = {"source_path": source_path}
    if block_names: params["block_names"] = block_names
    return await _call("import_block", params)


# =============================================================================
# Annotation Tools
# =============================================================================

@mcp.tool()
async def create_linear_dimension(
    point1: list[float],
    point2: list[float],
    dimension_line_position: list[float],
    rotation: float = 0,
    text: str = "",
    layer: str = ""
) -> str:
    """Create a linear (horizontal/vertical) dimension between two points."""
    params: dict = {
        "point1": point1, "point2": point2,
        "dimension_line_position": dimension_line_position,
        "rotation": rotation
    }
    if text: params["text"] = text
    if layer: params["layer"] = layer
    return await _call("create_linear_dimension", params)


@mcp.tool()
async def create_angular_dimension(
    center: list[float],
    point1: list[float],
    point2: list[float],
    dimension_arc_position: list[float],
    text: str = "",
    layer: str = ""
) -> str:
    """Create an angular dimension measuring the angle at center between two lines."""
    params: dict = {"center": center, "point1": point1, "point2": point2,
                    "dimension_arc_position": dimension_arc_position}
    if text: params["text"] = text
    if layer: params["layer"] = layer
    return await _call("create_angular_dimension", params)


@mcp.tool()
async def create_radial_dimension(
    center: list[float],
    chord_point: list[float],
    leader_length: float = 0,
    text: str = "",
    layer: str = ""
) -> str:
    """Create a radial dimension for a circle or arc."""
    params: dict = {"center": center, "chord_point": chord_point, "leader_length": leader_length}
    if text: params["text"] = text
    if layer: params["layer"] = layer
    return await _call("create_radial_dimension", params)


@mcp.tool()
async def create_diameter_dimension(
    center: list[float],
    chord_point: list[float],
    leader_length: float = 0,
    text: str = "",
    layer: str = ""
) -> str:
    """Create a diameter dimension for a circle."""
    params: dict = {"center": center, "chord_point": chord_point, "leader_length": leader_length}
    if text: params["text"] = text
    if layer: params["layer"] = layer
    return await _call("create_diameter_dimension", params)


@mcp.tool()
async def create_leader(
    points: list[list[float]],
    text: str = "",
    text_height: float = 2.5,
    layer: str = ""
) -> str:
    """Create a leader (callout arrow) with text. Points: [[arrow_tip], ..., [landing_point]]."""
    params: dict = {"points": points, "text": text, "text_height": text_height}
    if layer: params["layer"] = layer
    return await _call("create_leader", params)


@mcp.tool()
async def create_aligned_dimension(
    point1: list[float],
    point2: list[float],
    dimension_line_position: list[float],
    text: str = "",
    layer: str = ""
) -> str:
    """Create an aligned dimension between two points (measures along the line between them)."""
    params: dict = {
        "point1": point1, "point2": point2,
        "dimension_line_position": dimension_line_position
    }
    if text: params["text"] = text
    if layer: params["layer"] = layer
    return await _call("create_aligned_dimension", params)


# =============================================================================
# View Tools
# =============================================================================

@mcp.tool()
async def zoom_extents() -> str:
    """Zoom to show all entities in the drawing."""
    return await _call("zoom_extents")


@mcp.tool()
async def zoom_window(min_point: list[float], max_point: list[float]) -> str:
    """Zoom to a specific rectangular area defined by min [x,y] and max [x,y] corners."""
    return await _call("zoom_window", {"min": min_point, "max": max_point})


# =============================================================================
# Drawing Utility Tools
# =============================================================================

@mcp.tool()
async def purge_drawing() -> str:
    """Remove all unused layers, blocks, styles, and linetypes from the drawing."""
    return await _call("purge_drawing")


@mcp.tool()
async def set_units(
    linear_units: int = -1,
    precision: int = -1,
    insert_units: int = -1,
    angle_units: int = -1,
    angle_precision: int = -1
) -> str:
    """Set drawing units. linear_units: 1=Scientific,2=Decimal,3=Engineering,4=Architectural. insert_units: 4=mm,5=cm,6=m."""
    params: dict = {}
    if linear_units >= 0: params["linear_units"] = linear_units
    if precision >= 0: params["precision"] = precision
    if insert_units >= 0: params["insert_units"] = insert_units
    if angle_units >= 0: params["angle_units"] = angle_units
    if angle_precision >= 0: params["angle_precision"] = angle_precision
    return await _call("set_units", params)


@mcp.tool()
async def plot_to_pdf(output_path: str) -> str:
    """Export current drawing to PDF file."""
    return await _call("plot_to_pdf", {"output_path": output_path})


# =============================================================================
# Screenshot
# =============================================================================

@mcp.tool()
async def capture_screenshot(output_path: str = None) -> str:
    """Capture a screenshot of the current AutoCAD window and save as PNG.
    Returns the file path of the saved image. Use this to visually verify
    that drawing operations were completed correctly. The AI can view the
    returned image file to check the result."""
    params: dict = {}
    if output_path:
        params["output_path"] = output_path
    return await _call("capture_screenshot", params)


# =============================================================================
# Excel Table Import
# =============================================================================

@mcp.tool()
async def create_table_from_excel(
    excel_path: str,
    position: list[float],
    sheet_name: str = "",
    title: str = "",
    text_height: float = 120,
    header_text_height: float = 140,
    title_text_height: float = 250,
    row_height: float = 350,
    header_row_height: float = 400,
    title_row_height: float = 600,
    min_col_width: float = 2000,
    char_width: float = 80,
    color: int = 3,
    layer: str = "TABLE",
    start_row: int = 1,
    end_row: int = 0,
    start_col: int = 1,
    end_col: int = 0
) -> str:
    """Create a table in AutoCAD from an Excel file. Reads the spreadsheet, auto-sizes columns, and draws grid lines + text.

    Args:
        excel_path: Absolute path to the .xlsx file.
        position: Insertion point [x, y] for top-left corner of the table.
        sheet_name: Sheet name to read (default: first sheet).
        title: Optional title row text spanning full width.
        text_height: Text height for data cells.
        header_text_height: Text height for the first row (header).
        title_text_height: Text height for the title row.
        row_height: Row height for data cells.
        header_row_height: Row height for the first (header) row.
        title_row_height: Row height for the title row.
        min_col_width: Minimum column width.
        char_width: Approximate width per character for auto-sizing.
        color: ACI color index (default 3 = green).
        layer: Layer name for all entities.
        start_row: First Excel row to read (1-based, default 1).
        end_row: Last Excel row to read (0 = last row).
        start_col: First Excel column to read (1-based, default 1).
        end_col: Last Excel column to read (0 = last column).
    """
    import json as _json

    try:
        import openpyxl
    except ImportError:
        return _json.dumps({"error": "openpyxl is not installed. Run: pip install openpyxl"})

    if not os.path.exists(excel_path):
        return _json.dumps({"error": f"File not found: {excel_path}"})

    # --- Read Excel ---
    wb = openpyxl.load_workbook(excel_path, data_only=True)
    if sheet_name:
        if sheet_name not in wb.sheetnames:
            return _json.dumps({"error": f"Sheet '{sheet_name}' not found. Available: {wb.sheetnames}"})
        ws = wb[sheet_name]
    else:
        ws = wb.active

    sr = start_row
    er = end_row if end_row > 0 else ws.max_row
    sc = start_col
    ec = end_col if end_col > 0 else ws.max_column

    rows_data: list[list[str]] = []
    for row in ws.iter_rows(min_row=sr, max_row=er, min_col=sc, max_col=ec, values_only=True):
        rows_data.append([str(v) if v is not None else "" for v in row])

    if not rows_data:
        return _json.dumps({"error": "No data found in the specified range."})

    num_cols = len(rows_data[0])
    num_rows = len(rows_data)

    # --- Auto-size columns ---
    # Use header text height for header row width calc, data text height for data rows
    col_widths: list[float] = []
    for c in range(num_cols):
        max_w = 0.0
        for r in range(num_rows):
            if c < len(rows_data[r]) and rows_data[r][c]:
                txt_len = len(rows_data[r][c])
                # Scale char_width by text height ratio (larger text needs wider columns)
                h = header_text_height if r == 0 else text_height
                w = txt_len * char_width * (h / 120.0) + 400
                max_w = max(max_w, w)
        col_widths.append(max(min_col_width, max_w))

    total_w = sum(col_widths)
    x0, y0 = position[0], position[1]

    # Column X positions
    col_x = [x0]
    for w in col_widths:
        col_x.append(col_x[-1] + w)

    # Total height
    has_title = bool(title)
    total_h = (title_row_height if has_title else 0) + header_row_height + row_height * (num_rows - 1)

    # --- Build entities ---
    entities: list[dict] = []

    # Horizontal lines
    y = y0
    entities.append({"type": "line", "params": {"start": [x0, y], "end": [x0 + total_w, y], "layer": layer, "color": color}})

    if has_title:
        y -= title_row_height
        entities.append({"type": "line", "params": {"start": [x0, y], "end": [x0 + total_w, y], "layer": layer, "color": color}})

    # Header bottom
    y -= header_row_height
    entities.append({"type": "line", "params": {"start": [x0, y], "end": [x0 + total_w, y], "layer": layer, "color": color}})

    # Data rows
    for i in range(1, num_rows):
        y -= row_height
        entities.append({"type": "line", "params": {"start": [x0, y], "end": [x0 + total_w, y], "layer": layer, "color": color}})

    # Vertical lines
    vert_top = y0 - (title_row_height if has_title else 0)
    for idx, cx in enumerate(col_x):
        # Outer edges span full height including title row
        if has_title and (idx == 0 or idx == len(col_x) - 1):
            entities.append({"type": "line", "params": {"start": [cx, y0], "end": [cx, y0 - total_h], "layer": layer, "color": color}})
        else:
            entities.append({"type": "line", "params": {"start": [cx, vert_top], "end": [cx, y0 - total_h], "layer": layer, "color": color}})

    # Title text (centered via middle-center justification)
    if has_title:
        entities.append({"type": "text", "params": {
            "position": [x0 + total_w / 2, y0 - title_row_height / 2],
            "text": title, "height": title_text_height,
            "layer": layer, "color": color, "justification": "middle-center"
        }})

    # Padding from left cell edge
    cell_pad = 100

    # Header row (first data row)
    header_y_start = y0 - (title_row_height if has_title else 0)
    hy = header_y_start - header_row_height * 0.65
    for c in range(num_cols):
        txt = rows_data[0][c] if c < len(rows_data[0]) else ""
        if txt:
            cx = col_x[c] + cell_pad
            entities.append({"type": "text", "params": {
                "position": [cx, hy], "text": txt, "height": header_text_height,
                "layer": layer, "color": color
            }})

    # Data rows
    data_y_start = header_y_start - header_row_height
    for r in range(1, num_rows):
        ry = data_y_start - (r - 1) * row_height - row_height * 0.65
        for c in range(num_cols):
            txt = rows_data[r][c] if c < len(rows_data[r]) else ""
            if txt:
                cx = col_x[c] + cell_pad
                entities.append({"type": "text", "params": {
                    "position": [cx, ry], "text": txt, "height": text_height,
                    "layer": layer, "color": color
                }})

    # --- Send to AutoCAD in batches ---
    batch_size = 500
    all_handles: list[str] = []
    total_created = 0

    for i in range(0, len(entities), batch_size):
        batch = entities[i:i + batch_size]
        result_str = await _call("bulk_create", {"entities": batch})
        result = _json.loads(result_str)
        if "handles" in result:
            all_handles.extend(result["handles"])
            total_created += result.get("count", len(result["handles"]))

    summary = {
        "success": True,
        "message": f"Table created from '{os.path.basename(excel_path)}'",
        "sheet": ws.title,
        "data_rows": num_rows,
        "columns": num_cols,
        "entities_created": total_created,
        "table_width": total_w,
        "table_height": total_h,
        "position": [x0, y0],
    }
    return _json.dumps(summary, indent=2)


# =============================================================================
# Entry point
# =============================================================================

if __name__ == "__main__":
    mcp.run(transport="stdio")
