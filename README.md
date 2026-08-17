# AutoCAD MCP Plugin

AI-powered AutoCAD automation via the **Model Context Protocol (MCP)**. Enables Claude and other AI assistants to create, modify, query, search, and visually verify AutoCAD drawings through natural language.

> "Draw a floor plan with 3 bedrooms" — and it does.
> "Find the battery room nearest to the toilet" — and it navigates there.
> "Take a screenshot and check if the layout looks correct" — and it verifies visually.

## Architecture

```
┌─────────────┐                ┌────────────────────┐
│  Claude /   │ ─── stdio ───▶ │  MCP server        │
│  AI Client  │      MCP       │  C# exe  or  Python│ ─┐
└─────────────┘                └────────────────────┘  │
                                                        │  TCP JSON-RPC 2.0
                                                        │  localhost:8081
┌──────────────┐        HTTP / JSON-RPC 2.0             ▼
│   Browser    │ ──────────────────────────▶ ┌──────────────────┐
│   (web app)  │  127.0.0.1:8082/jsonrpc     │  C# Plugin       │
└──────────────┘  (CORS + Chrome PNA ok)     │ (inside AutoCAD) │
                                             └──────────────────┘
                                                        │
                                                AutoCAD .NET API
```

| Component | Language | Location |
|-----------|----------|----------|
| **AutoCADMCPPlugin.dll** | C# | `src/AutoCADMCPPlugin/` |
| **MCP server (recommended)** | C# | `src/AutoCADMCP.Server/` |
| **MCP server (alternative)** | Python | `src/mcp_server/` |
| **Bundle Manifest** | XML | `config/AutoCADMCPPlugin.bundle/` |

### Two MCP servers, same tools

Both expose the identical 184 tools; pick whichever suits your setup.

| | C# server | Python server |
|---|---|---|
| Artifact | one self-contained `.exe` | `server.py` |
| Requires Python on the machine | no | yes (3.10+) |
| Tool schemas | generated from the Python signatures at build time | inferred by FastMCP |
| Best for | end users, IT deployment | development, quick edits |

The C# server's schemas are generated from the Python server's typed signatures
by `build/generate_tool_schemas.py`, so the two surfaces cannot drift — CI fails
if the generated `tools.json` is stale.

### How It Works

The C# plugin loads inside AutoCAD as an addin and exposes the same JSON-RPC pipeline over **two transports** simultaneously:

1. **TCP socket on `localhost:8081`** — used by whichever MCP server you run, which marshals the 184 tools over stdio for Claude / Claude Code / Claude Desktop.
2. **HTTP loopback on `localhost:8082`** — used by browser apps that can't open raw TCP sockets (a `fetch()` call to `http://127.0.0.1:8082/jsonrpc` reaches every command in the registry, with CORS headers + Chrome Private-Network-Access support out of the box).

Both transports route through the same `JsonRpcHandler`, so every tool (`create_line`, `create_table`, `capture_screenshot`, …) is reachable identically from either path. Commands are marshaled to AutoCAD's main UI thread via `Application.Idle` + `DocumentLock` — except introspection tools, which answer directly so tool discovery keeps working while AutoCAD is busy.

### Thread Safety

AutoCAD's .NET API is single-threaded. The plugin uses `Application.Idle` event + `DocumentLock` to safely execute commands from the socket handler threads on the main thread.

## Features — 184 MCP Tools

### System (6)
| Tool | Description |
|------|-------------|
| `system_status` | Plugin version, build tag, AutoCAD version, active document |
| `list_methods` | All available commands |
| `set_system_variable` | Set AutoCAD system variables (DIMTXT, LTSCALE, etc.) |
| `get_system_variable` | Read system variable values |
| `execute_command` | Run raw AutoCAD command strings |
| `read_command_line` | Read back what the command line printed. `execute_command` is queued and asynchronous, so this is the only way to see what a queued command actually did |

### Drawing Management (11)
| Tool | Description |
|------|-------------|
| `drawing_new` | Create new drawing (optional template) |
| `drawing_open` | Open existing .dwg file |
| `drawing_save` | Save / Save As |
| `drawing_close` | Close a drawing, optionally discarding unsaved changes; reports whether anything was actually discarded |
| `drawing_list` | Every open drawing, which one is active, and which have unsaved changes |
| `close_all` | Close every open drawing |
| `drawing_info` | Entity count, layers, file path |
| `set_units` | Set linear and angular units |
| `purge_drawing` | Remove unused blocks, layers, styles |
| `plot_devices` | Plot devices, plot style tables, and a device's paper sizes with printable areas and margins, by canonical name |
| `plot_to_pdf` | Plot a layout or a model-space window to PDF, with the paper size chosen automatically |

### Entity Creation (12)
| Tool | Description |
|------|-------------|
| `create_line` | Line from start to end point |
| `create_circle` | Circle at center with radius |
| `create_arc` | Arc with center, radius, start/end angle |
| `create_polyline` | Polyline through points (open or closed) |
| `create_rectangle` | Rectangle from two corners |
| `create_ellipse` | Ellipse with major/minor radii |
| `create_text` | Single-line text |
| `create_mtext` | Multi-line text with width |
| `create_hatch` | Hatch with boundary, pattern, ACI colour, and optional true RGB |
| `create_spline` | Smooth spline curve through points |
| `create_table` | Table with rows, columns, and cell data |
| `create_table_from_excel` | Table built from a sheet in an `.xlsx` file (requires `openpyxl`) |

### Entity Query (9)
| Tool | Description |
|------|-------------|
| `list_entities` | List entities with layer/type filters |
| `get_entity` | Detailed entity info by handle |
| `get_entities` | Detailed info for several handles in one transaction |
| `select_by_properties` | Filter entities by layer, type, color |
| `select_by_window` | Find entities within a rectangular area |
| `get_bounding_box` | Get entity extents (min/max points, width, height) |
| `measure_distance` | Distance, dx, dy, angle between two points |
| `measure_area` | Area and perimeter of closed entities |
| `find_intersections` | Find intersection points between two curves |

### Entity Modification (11)
| Tool | Description |
|------|-------------|
| `erase_entity` | Delete entity |
| `move_entity` | Move entity between points |
| `copy_entity` | Copy entity to new location |
| `rotate_entity` | Rotate around base point |
| `scale_entity` | Scale from base point |
| `mirror_entity` | Mirror across a line |
| `set_entity_properties` | Change color, layer, linetype, thickness |
| `offset_entity` | Offset curve by distance (left/right/both) |
| `explode_entity` | Explode block/polyline into primitives |
| `array_rectangular` | Rectangular array (rows x columns) |
| `array_polar` | Polar array around center point |

### Bulk Operations (3)
| Tool | Description |
|------|-------------|
| `bulk_create` | Create multiple entities in one call |
| `bulk_erase` | Delete multiple entities by handles |
| `undo_last` | Undo last operation |

### Layers (6)
| Tool | Description |
|------|-------------|
| `list_layers` | All layers with properties |
| `create_layer` | New layer with color/linetype |
| `set_current_layer` | Switch active layer |
| `set_layer_properties` | Modify color, freeze, lock, etc. |
| `delete_layer` | Remove a layer |
| `rename_layer` | Rename a layer |

### Blocks (4)
| Tool | Description |
|------|-------------|
| `list_blocks` | Block definitions with attributes |
| `insert_block` | Insert with position/rotation/scale/attributes |
| `create_block` | Define a new block from geometry |
| `import_block` | Import a block definition from another .dwg |

### Annotations (7)
| Tool | Description |
|------|-------------|
| `create_linear_dimension` | Horizontal/vertical dimension |
| `create_aligned_dimension` | Aligned dimension along two points |
| `create_angular_dimension` | Angle between two lines |
| `create_radial_dimension` | Radius dimension on arc/circle |
| `create_diameter_dimension` | Diameter dimension on arc/circle |
| `create_leader` | Leader callout with text (MLeader) |
| `join_entities` | Join collinear/connected entities |

### Styles (4)
| Tool | Description |
|------|-------------|
| `create_dimension_style` | Create/modify dimension style (text height, arrows, scale, suffix) |
| `create_text_style` | Create/modify text style (font, height, width factor) |
| `list_dimension_styles` | List all dimension styles |
| `list_text_styles` | List all text styles |

### View (2)
| Tool | Description |
|------|-------------|
| `zoom_extents` | Fit all entities |
| `zoom_window` | Zoom to rectangular area |

### Screenshot (1)
| Tool | Description |
|------|-------------|
| `capture_screenshot` | Capture AutoCAD window as PNG for AI visual verification |

### Search & Spatial Query (3)
| Tool | Description |
|------|-------------|
| `search_text` | Find all text/mtext/block names matching a keyword |
| `find_nearest` | Find entities nearest to a point (by type/layer, sorted by distance) |
| `measure_between` | Measure distance between two entities by handle |

### Code Execution (1, opt-in)
| Tool | Description |
|------|-------------|
| `execute_python` | Run Python in the MCP server process with `call(method, params)` pre-wired — for multi-step geometry that would otherwise cost dozens of round-trips |

> **Disabled by default.** Set `AUTOCAD_MCP_ALLOW_EXEC=1` to enable. The code is
> not sandboxed and runs with the server process's privileges; drawing
> operations still pass through the read-only and confirmation gates.

### Server Introspection & Safety (3)
| Tool | Description |
|------|-------------|
| `get_capabilities` | Build info, tool counts, destructive-tool list, safety posture (answers even while AutoCAD is modal) |
| `get_server_options` | Read current read-only / confirmation / audit settings |
| `set_server_options` | Change and persist the safety posture |

### Layouts & Paper Space (12)
| Tool | Description |
|------|-------------|
| `list_layouts` | List sheets with paper size, plot device and scale |
| `create_layout` / `delete_layout` | Add or remove a layout tab |
| `rename_layout` / `copy_layout` | Rename or duplicate a layout with its setup |
| `set_current_layout` | Switch active sheet (`Model` for model space) |
| `get_page_setup` / `set_page_setup` | Read/configure device, paper size, plot type, scale, rotation, CTB |
| `list_viewports` / `create_viewport` | Inspect or add paper-space viewports |
| `set_viewport_scale` / `lock_viewport` | Set viewport scale (`"1:100"`); lock against zoom |

For plotting and device discovery use `plot_to_pdf` and `plot_devices` (listed
under Drawing Management) — they wait for the file and report printable areas.

### External References (9)
| Tool | Description |
|------|-------------|
| `attach_xref` | Attach or overlay an external DWG |
| `list_xrefs` | List xrefs with path, status and reference count |
| `reload_xref` / `unload_xref` / `detach_xref` | Manage xref load state |
| `bind_xref` | Bind an xref into the drawing permanently |
| `set_xref_path` | Repoint a broken or moved xref |
| `read_external_dwg` | Inspect a DWG **without opening it** (side database) |
| `batch_query_dwgs` | Sweep a whole folder of DWGs without opening any |

### Block Attributes & Dynamic Blocks (10)
| Tool | Description |
|------|-------------|
| `list_block_attributes` | Attribute definitions declared by a block |
| `get_attribute_values` | Read attributes of one reference or every reference |
| `set_attribute_values` | Write attributes by tag; reports unmatched tags |
| `sync_attributes` | Add attributes added to the definition after insertion (ATTSYNC) |
| `get_dynamic_block_properties` | Parameters, values and allowed values |
| `set_dynamic_block_property` | Set a visibility state, length or angle |
| `rename_block` / `delete_block_definition` | Manage block definitions |
| `count_block_references` | Per-block insert counts — a quick BOM |
| `export_block_to_file` | Export a block to its own DWG (WBLOCK) |

### Modify Operations (11)
| Tool | Description |
|------|-------------|
| `break_entity` | Split a curve at points (snapped onto the curve) |
| `fillet_entities` | Fillet two lines with a tangent arc, trimming both |
| `polyline_edit` | Open/close, width, elevation, add/remove vertices |
| `reverse_polyline` | Reverse curve direction |
| `set_draworder` | Move entities front/back/above/below |
| `flatten_entities` | Flatten to a single Z elevation |
| `divide_entity` / `measure_entity` | Place points or blocks along a curve |
| `create_region` / `create_boundary` | Build regions; trace a boundary (BPOLY) |
| `overkill` | Delete exact duplicate/overlapping entities |

### 2D Entities & 3D Solids (16)
| Tool | Description |
|------|-------------|
| `create_point` / `create_xline` / `create_ray` | Point and construction geometry |
| `create_polygon` / `create_donut` / `create_3d_polyline` | Regular polygon, donut, 3D polyline |
| `create_box` / `create_sphere` / `create_cylinder` | Primitive solids |
| `create_cone` / `create_wedge` / `create_torus` | More primitive solids |
| `extrude_profile` / `revolve_profile` | Build solids from closed profiles |
| `boolean_solids` | Union / subtract / intersect |
| `get_solid_properties` | Volume, centroid, moments of inertia, bbox |

### Groups, Layer States, Views, UCS (13)
| Tool | Description |
|------|-------------|
| `create_group` / `list_groups` / `add_to_group` / `ungroup` | Named groups |
| `save_layer_state` / `restore_layer_state` | Capture and restore layer settings |
| `list_layer_states` / `delete_layer_state` | Manage saved layer states |
| `create_named_view` / `list_named_views` / `restore_view` | Named views |
| `list_ucs` / `set_ucs` | User coordinate systems (by name or explicit axes) |

### Drawing Data & Audit (6)
| Tool | Description |
|------|-------------|
| `get_xdata` / `set_xdata` | Read/write extended entity data (auto-registers the app name) |
| `get_drawing_properties` / `set_drawing_properties` | Title block fields and custom properties |
| `entity_count_report` | Counts by type and by layer |
| `audit_drawing` | Empty layers, unused blocks, broken xrefs, zero-length curves |

### Annotation Completion (18)
| Tool | Description |
|------|-------------|
| `create_multileader` | Multileader with arrow, landing and text |
| `list_mleader_styles` / `create_mleader_style` | Multileader styles |
| `create_ordinate_dimension` | Ordinate dimension along the X or Y axis |
| `create_arclength_dimension` | Arc-length dimension |
| `create_tolerance` | GD&T feature control frame |
| `edit_dimension_text` | Override dimension text (`<>` embeds the measurement) |
| `update_dimensions` | Regenerate dimensions, optionally reassigning a style |
| `list_annotation_scales` / `set_annotation_scale` | Annotation scale (CANNOSCALE) |
| `add_annotation_scale_to_entity` | Add/remove a scale representation on annotative entities |
| `get_table_data` / `set_table_cell` | Read a whole table; write single or batched cells |
| `merge_table_cells` / `list_table_styles` | Merge/unmerge ranges; list styles |
| `edit_mtext` | Edit existing text or mtext in place |
| `create_wipeout` | Mask a region behind a polygon |
| `create_revision_cloud` | Rectangular revision cloud |

### Sheet Sets (4, COM-based)
| Tool | Description |
|------|-------------|
| `get_sheet_set_status` | Probe whether Sheet Set Manager automation is available |
| `open_sheet_set` | Open a `.dst` and report name, description, sheet count |
| `list_sheets` | List sheets with number, title, name, description |
| `close_sheet_set` | Close an open sheet set database |

> Sheet sets have **no managed .NET API** — these go through the `AcSmComponents`
> COM library via late binding, and are **read-only**. If the COM server is not
> registered they return `Unsupported` with an explanation rather than failing.
> Call `get_sheet_set_status` first. This path has not been verified against a
> live Sheet Set Manager.

## Supported AutoCAD Versions

| AutoCAD Version | Release Code | .NET Target | NuGet Reference |
|----------------|--------------|-------------|-----------------|
| **2021, 2022, 2023, 2024** | R24.0–R24.3 | .NET Framework 4.8 (`net48`) | AutoCAD.NET 24.0.x |
| **2025, 2026** | R25.0–R25.1 | .NET 8 (`net8.0-windows`) | AutoCAD.NET 25.0.x |
| **2027** | R26.0 | .NET 10 (`net10.0-windows`) | AutoCAD.NET 26.0.x |

The `net48` leg is compiled against the **oldest** reference assemblies in its ABI
family (2021), which is what lets one binary load on 2021 through 2024. Building
against newer refs would break loading on older releases.

`net48` and `net8.0-windows` build by default. The 2027 leg is added
**automatically when AutoCAD 2027 is installed** on the build machine, and can be
forced on where it is not (CI, for example) with:

```bash
dotnet build -c Release -p:IncludeNet10=true
```

One 2027 subtlety worth knowing: AutoCAD 2027 ships its own `Newtonsoft.Json` and
loads it into the default context. When 2027 is present the build references
*that* assembly and never copies its own, so compile-time and run-time signatures
match — otherwise you get a `MissingMethodException` at run time. Point the build
at a non-default install with `-p:AutoCADPath2027="D:\...\AutoCAD 2027"`.

The bundle manifest (`PackageContents.xml`) declares one `ComponentEntry` per
range, so AutoCAD auto-selects the right DLL. All version drift is isolated in
`Core/AcadCompat.cs` — no `#if` anywhere else in the codebase.

**AutoCAD LT is not supported** and cannot be: LT has no `NETLOAD` and cannot load
.NET plugins at any version. LT 2024+ supports AutoLISP/ActiveX only, which would
need a separate bridge.

Verticals built on AutoCAD (Civil 3D, Plant 3D, Map 3D, Mechanical, Architecture)
host the same bundle and work without changes.

## Installation

### Prerequisites

- **AutoCAD 2022–2027** (any edition including LT with .NET support)
- **Python 3.10+** (for MCP server)
- **Windows** (AutoCAD is Windows-only)
- **.NET SDK 8.0+** (only if building from source; **SDK 10.0** is needed for the AutoCAD 2027 target)

### Option A: Install Pre-built Plugin (No Build Tools Needed)

**Close AutoCAD first**, then run:

```bash
cd autocad-plugin
install-prebuilt.bat
```

This copies the pre-built DLLs from `dist/` to `%APPDATA%\Autodesk\ApplicationPlugins\` and AutoCAD will load the plugin automatically on startup.

> **AutoCAD 2027:** `dist/` currently ships `net48` and `net8.0-windows` only. The script says so plainly and skips the 2027 folder; for 2027 use Option B and build from source.

### Option B: Build from Source

If you have .NET SDK installed:

```bash
cd autocad-plugin
install.bat
```

This builds every target available on the machine, copies the DLLs to `%APPDATA%\Autodesk\ApplicationPlugins\AutoCADMCPPlugin.bundle\`, checks that each framework folder actually received its DLL, and AutoCAD will load the plugin automatically on startup. The closing summary says whether the AutoCAD 2027 folder was populated or skipped.

Both installers finish by checking whether `mcp`, `openpyxl` and `pikepdf` are importable and offering to install them for you; Step 3 below is that same `pip install` by hand. The check never installs anything without being asked, and skips itself if Python is not on `PATH`.

### Option C: Manual Install (Copy & Paste)

If the scripts don't work, you can install manually:

1. Copy the folder `autocad-plugin/config/AutoCADMCPPlugin.bundle` to:
   ```
   %APPDATA%\Autodesk\ApplicationPlugins\
   ```
   > Tip: Paste `%APPDATA%\Autodesk\ApplicationPlugins` in Windows Explorer's address bar — it expands automatically.

2. Copy the DLLs from `autocad-plugin/dist/net48/` into:
   ```
   %APPDATA%\Autodesk\ApplicationPlugins\AutoCADMCPPlugin.bundle\Contents\net48\
   ```

3. The final folder structure should be:
   ```
   %APPDATA%\Autodesk\ApplicationPlugins\
     └── AutoCADMCPPlugin.bundle\
           ├── PackageContents.xml
           └── Contents\
                 └── net48\
                       ├── AutoCADMCPPlugin.dll
                       └── Newtonsoft.Json.dll
   ```
   > For AutoCAD 2025–2026, also copy `dist/net8.0-windows/` into `Contents\net8.0-windows\`.
   >
   > For AutoCAD 2027, the folder must be `Contents\net10.0-windows\` and needs `AutoCADMCPPlugin.dll`, `System.Drawing.Common.dll`, `Microsoft.Win32.SystemEvents.dll` and `System.Private.Windows.Core.dll` from `bin/Release/net10.0-windows/`. Do **not** copy `Newtonsoft.Json.dll` there — AutoCAD 2027 ships its own and a second copy in the bundle can shadow it.

4. Open AutoCAD — the plugin loads automatically.

### Step 2: Start the Server in AutoCAD

```
Command: MCPSTART
[MCP] Server started on localhost:8081
```

Other commands:
- `MCPSTOP` — Stop the server
- `MCPSTATUS` — Show connection count

### Step 3: Install the MCP Server

```bash
cd autocad-plugin/src/mcp_server
pip install -r requirements.txt
```

This pulls in `mcp`, `openpyxl` (for `create_table_from_excel`) and `pikepdf` (used by `plot_to_pdf` to crop the finished page down to the plotted window — see [Plotting to PDF](#plotting-to-pdf)). Only `mcp` is strictly required; the other two disable one feature each if absent.

### Step 4: Configure Your MCP Client

Create a `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "autocad-mcp": {
      "command": "python",
      "args": ["<full-path-to>/autocad-plugin/src/mcp_server/server.py"],
      "env": {
        "AUTOCAD_MCP_HOST": "localhost",
        "AUTOCAD_MCP_PORT": "8081"
      }
    }
  }
}
```

For **Claude Desktop**, add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "autocad-mcp": {
      "command": "python",
      "args": ["C:/path/to/autocad-plugin/src/mcp_server/server.py"]
    }
  }
}
```

### Step 5: Test

In Claude Code or Claude Desktop:

> "Draw a circle at (50, 50) with radius 25"

Or verify manually over the TCP socket:

```python
import socket, json
sock = socket.socket()
sock.connect(('localhost', 8081))
sock.sendall(json.dumps({
    "jsonrpc": "2.0", "method": "system_status", "params": {}, "id": "1"
}).encode() + b"\n")
print(sock.recv(4096).decode())
```

Or over the HTTP shim (which is what browser/web-app integrations use):

```bash
curl -s -X POST http://127.0.0.1:8082/jsonrpc \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","method":"system_status","params":{},"id":"1"}'
```

`MCPSTATUS` will report both transports as running.

## Plotting to PDF

`plot_to_pdf` plots either a named layout or a rectangular window of model space through AutoCAD's own publishing engine, so what lands in the PDF is what the plot preview would have shown — plot styles, lineweights and all.

Paper size is the part that usually surprises people. AutoCAD can only plot onto a sheet the plot device actually defines, and those sheets are addressed by *canonical* media names such as `ISO_full_bleed_A1_(841.00_x_594.00_MM)`, not by the localised strings the Plot dialog shows. `plot_devices` lists what a device offers: call it with no arguments to get the plot devices and plot style tables installed on the machine plus the paper sizes of the default PDF driver, or pass `device` (`plotter` is accepted as a synonym, because some MCP hosts consume an argument literally named `device` before the tool ever sees it) together with an optional `filter` substring to narrow a long list. Every entry comes back with its printable area and margins, so a caller can tell a full-bleed sheet from one with a 5 mm border before choosing.

Leaving `paper` at its default of `auto` skips all of that: the command measures the window being plotted and picks the smallest defined sheet that contains it, preferring the orientation that needs no rotation when a sheet exists in both. Because the smallest sheet that *contains* a window is rarely the same size as the window, the finished page is then cropped down to the plotted extents and the surplus removed, and the answer reports `trimmed`, `trimmed_size_mm` and the `trim_box_mm` that was applied. Pass `trim: false` to keep the driver's page exactly as it came out.

Trimming is done in the Python MCP server with [pikepdf](https://pypi.org/project/pikepdf/), which `requirements.txt` installs. It is not required for plotting: without it the plot still succeeds and the answer simply reports `trimmed: false` with a `trim_error` naming the missing package. The crop is measured from the real `MediaBox` of the file AutoCAD produced rather than from the nominal sheet dimensions, because `DWG To PDF.pc3` quantises its page at roughly 0.042 mm per unit and a nominal 841 × 594 mm sheet arrives as 841.022 × 594.078 mm.

One prerequisite applies to both commands: a drawing has to be open. Plot devices, style tables and paper sizes are all read through `PlotSettingsValidator.Current`, which is current *for the active document*, so with no drawing open there is nothing to read them from. Both commands check for an active document before touching the plot API and return a plain error explaining what to do.

## Calling from a Browser / Web App

The HTTP shim makes the plugin's full 80-command surface reachable from any browser-based internal tool — no MCP client, no Claude in the loop, no per-engineer license. Engineers run the plugin once (`MCPSTART` in AutoCAD), and your web app calls `localhost:8082` directly.

### Quick example: insert a table

```js
async function insertBeamSchedule(beams) {
  const r = await fetch('http://127.0.0.1:8082/jsonrpc', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      jsonrpc: '2.0',
      id: '1',
      method: 'create_table',
      params: {
        position: [0, 0, 0],
        title: 'Beam schedule',
        rows: beams.length,
        columns: 8,
        row_height: 500,
        column_width: 2000,
        layer: 'BEAM_SCHEDULE',
        data: [
          ['Beam ID', 'b×D', 'L (m)', 'Top', 'Bot', 'Stirrups', 'Side-face', 'Status'],
          ...beams.map(b => [b.id, b.section, b.length, b.top, b.bot, b.stirrups, b.sideFace, b.status]),
        ],
      },
    }),
  });
  return r.json();
}
```

### Coloured fills (header bands, status swatches)

`create_hatch` and the `hatch` case in `bulk_create` accept `color` (ACI 0–255) and `true_color` `[r, g, b]` (precedence over ACI). Use a `SOLID` pattern for filled cells, the same boundary the cell already uses for its grid lines, and the colour will sit *behind* any text drawn on top:

```js
{
  jsonrpc: '2.0', id: '2', method: 'bulk_create',
  params: {
    entities: [
      // dark-green title-row band, RGB pinned to the brand swatch
      {
        type: 'hatch',
        params: {
          boundary: [[0, 0], [12000, 0], [12000, 600], [0, 600]],
          pattern: 'SOLID',
          true_color: [37, 78, 55],
          layer: 'TABLE_HEADER'
        }
      },
      // grid lines, text, etc. — drawn after so they sit on top
    ]
  }
}
```

The hatch's boundary polyline gets the same colour applied so it doesn't flash in a `ByLayer` outline against the fill.

### Sizing tables to real text widths

SHX-font glyphs (Romans, Standard, Romantic, RomanD, Italic, Italicc) are proportional and hand-tuned; a JS pixel approximation diverges noticeably from the real render. Browser callers building tables can ask AutoCAD what the text actually measures before sizing columns:

```js
const r = await fetch('http://127.0.0.1:8082/jsonrpc', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    jsonrpc: '2.0', id: '1', method: 'measure_texts',
    params: {
      items: [
        { text: 'BEAM',                 height: 225, style: 'Romans' },
        { text: 'SIZE',                 height: 225, style: 'Romans' },
        { text: 'BOTTOM REINFORCEMENT', height: 225, style: 'Romans' },
        { text: 'GB-400x700',           height: 225, style: 'Romans' },
      ],
    },
  }),
});
// → { results: [{ text, width, height }, ...] }   widths from real DBText extents
```

`measure_text` is the single-string variant. Both work by appending a temporary `DBText` probe to model space, reading `GeometricExtents`, and aborting the transaction so the probe never lands in the user's drawing. Batches are capped at 2000 items per call so a runaway client can't pin AutoCAD's main thread.

### CORS

By default the listener allows any origin (`*`) so internal tools work without configuration. **For production deployments, pin to your real origin** by setting an env var before AutoCAD launches:

```bat
setx AUTOCAD_MCP_HTTP_ORIGINS "https://your-internal-app.example.com"
```

Multiple origins can be comma-separated. The header `Access-Control-Allow-Private-Network: true` is always sent so Chrome's Private Network Access preflight passes when an HTTPS page calls `127.0.0.1`.

### Disabling the HTTP shim

Set `AUTOCAD_MCP_HTTP_PORT=0` in the user's environment to disable the HTTP listener entirely (TCP on 8081 keeps working for Claude).

## Uninstall

```bash
cd autocad-plugin
uninstall.bat
```

Or manually delete: `%APPDATA%\Autodesk\ApplicationPlugins\AutoCADMCPPlugin.bundle\`

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `AUTOCAD_MCP_HOST` | `localhost` | Plugin TCP socket host (Python MCP server) |
| `AUTOCAD_MCP_PORT` | `8081` | Plugin TCP socket port (Python MCP server) |
| `AUTOCAD_MCP_HTTP_PORT` | `8082` | Plugin HTTP shim port for browser apps. Set to `0` to disable. |
| `AUTOCAD_MCP_HTTP_ORIGINS` | `*` | Comma-separated CORS allow-list for the HTTP shim. Pin to a specific origin (e.g. `https://pdf.example.com`) in production. |

## Project Structure

```
autocad-plugin/
├── install.bat                    # Build all targets + deploy to ApplicationPlugins
├── install-prebuilt.bat           # Deploy the DLLs from dist/ without building
├── uninstall.bat                  # Remove from ApplicationPlugins
├── config/
│   └── AutoCADMCPPlugin.bundle/
│       └── PackageContents.xml    # AutoCAD auto-load manifest
└── src/
    ├── AutoCADMCPPlugin/          # C# .NET Plugin
    │   ├── AutoCADMCPPlugin.csproj
    │   ├── Core/
    │   │   ├── Plugin.cs            # IExtensionApplication entry point
    │   │   ├── SocketServer.cs      # TCP server on 8081 (Python MCP / Claude)
    │   │   ├── HttpListenerServer.cs# HTTP shim on 8082 (browser apps, CORS + PNA)
    │   │   ├── JsonRpcHandler.cs    # Protocol handler shared by both transports
    │   │   ├── IdleActionRunner.cs  # Thread marshaling via Application.Idle
    │   │   └── CommandRegistry.cs   # Auto-discovers ICommand implementations
    │   ├── Commands/
    │   │   ├── EntityCommands.cs          # Line, circle, arc, polyline, rectangle, ellipse, text, hatch
    │   │   ├── EntityModifyCommands.cs    # Move, copy, rotate, scale, mirror, erase
    │   │   ├── AdvancedModifyCommands.cs  # Set properties, offset, explode, array, join, bulk erase, undo
    │   │   ├── LayerCommands.cs           # Layer CRUD + delete/rename
    │   │   ├── BlockCommands.cs           # Block list/insert/create
    │   │   ├── AnnotationCommands.cs      # Linear and aligned dimensions
    │   │   ├── AdvancedAnnotationCommands.cs # Angular, radial, diameter dims, leader, spline, table
    │   │   ├── DrawingCommands.cs         # New, open, save, info
    │   │   ├── AdvancedDrawingCommands.cs  # Units, purge, plot, delete/rename layer, bulk create
    │   │   ├── ViewCommands.cs            # Zoom extents/window
    │   │   ├── SystemCommands.cs          # Status, list methods
    │   │   ├── SystemVariableCommand.cs   # Get/set system vars, execute command
    │   │   ├── StyleCommands.cs           # Dimension and text styles
    │   │   ├── QueryCommands.cs           # Measure, bounding box, select by window/properties, intersections
    │   │   ├── MeasureTextCommands.cs     # measure_text / measure_texts — real text bounding boxes
    │   │   ├── SearchCommands.cs          # search_text, find_nearest, measure_between
    │   │   └── ScreenshotCommand.cs       # capture_screenshot (Windows API viewport capture)
    │   └── Models/
    │       ├── ICommand.cs        # Command interface
    │       └── CommandResult.cs   # Result wrapper
    └── mcp_server/                # Python MCP Server
        ├── server.py              # 184 MCP tools via FastMCP
        ├── autocad_client.py      # Async TCP client with auto-reconnect
        └── requirements.txt
```

## Adding New Commands

1. Create a class extending `AcadCommand` in `Commands/`
2. **Register it** in `Core/CommandRegistry.cs` — registration is explicit, not
   reflection-based, so a new class does nothing until it is registered
3. Add the corresponding MCP tool in `server.py`
4. Rebuild and reinstall: `install.bat`

Example:

```csharp
public class MyCommand : AcadCommand
{
    public override string MethodName => "my_command";

    public override CommandResult Execute(JObject parameters)
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return CommandResult.NoDoc();

        // Forgiving aliases: accepts id | entity_id | handle | object_id
        string handle = EntityHelper.EntityIdArg(parameters);
        if (string.IsNullOrWhiteSpace(handle))
            return CommandResult.BadParam("Parameter 'id' is required");

        using (EntityHelper.LockDoc())
        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            ObjectId id = EntityHelper.ResolveHandle(doc.Database, handle);
            if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

            // Your AutoCAD API code here
            tr.Commit();
        }
        return CommandResult.Ok(new JObject { ["message"] = "Done" });
    }
}
```

Then in `Core/CommandRegistry.cs`:

```csharp
Register(new MyCommand());
```

**Read/write/destructive classification is automatic.** `CommandClassifier` infers
it from the method name (`list_`/`get_`/`measure_` → read; `erase_`/`delete_`/
`purge_` → destructive). Override `IsWrite` or `IsDestructive` on your class when
the name is misleading — e.g. `measure_entity` reads as read-only by prefix but
actually places markers, so it overrides `IsWrite => true`.

**Commands that never touch the AutoCAD API** (introspection, settings) should
extend `DirectCommand` instead. Those run on the socket thread and stay responsive
even while AutoCAD is busy or showing a modal dialog.

## Safety Model

Two gates run before any command executes:

| Gate | Behaviour | Default |
|------|-----------|---------|
| **Read-only mode** | Refuses every drawing-modifying tool with `errorCode: ReadOnly` | off |
| **Destructive confirmation** | `erase_*`/`delete_*`/`purge_*`/`overkill`/`ungroup`/`detach_*` require `"__confirm": true` | **on** |

Toggle both with `set_server_options`. There are 10 destructive tools; run
`get_capabilities` for the current list.

Every call is appended to an audit log at
`%APPDATA%\AutoCADMCP\activity.jsonl` (one JSON object per line, carrying
user/machine/drawing/duration/errorCode). Disable with
`set_server_options({"audit_log": false})`.

## Error Codes

Failures carry a typed code in `error.data.errorCode` so a client can branch on
the failure kind instead of matching error text:

`NotFound` · `InvalidParam` · `ReadOnly` · `NoDocument` · `NeedsConfirm` ·
`Unsupported` · `TxnFailed` · `Timeout` · `Internal`

## License

MIT
