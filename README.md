# AutoCAD MCP Plugin

AI-powered AutoCAD automation via the **Model Context Protocol (MCP)**. Enables Claude and other AI assistants to create, modify, query, search, and visually verify AutoCAD drawings through natural language.

> "Draw a floor plan with 3 bedrooms" — and it does.
> "Find the battery room nearest to the toilet" — and it navigates there.
> "Take a screenshot and check if the layout looks correct" — and it verifies visually.

> **This is a fork** of [NCO-1986/AutoCAD_mcp](https://github.com/NCO-1986/AutoCAD_mcp) by NCO-1986.
> It adds AutoCAD 2027 (.NET 10 / R26.0) support, a working `plot_to_pdf` that trims the page to
> the plotted window, hexadecimal entity handles, document control (`drawing_new` / `drawing_open` /
> `drawing_save` / `drawing_close` / `drawing_list` / `drawing_info`), command-line diagnostics and a
> `create_block` base-point fix. All of it has been offered back upstream as
> [PR #4](https://github.com/NCO-1986/AutoCAD_mcp/pull/4) — if it is merged, this fork becomes
> unnecessary. No Autodesk libraries or AutoCAD binaries are redistributed here: AutoCAD is
> proprietary software and licensing it is up to you.
>
> Русское описание: [README.ru.md](README.ru.md)

## Architecture

```
┌─────────────┐     stdio      ┌──────────────────┐     TCP socket      ┌──────────────────┐
│   Claude /   │ ──── MCP ────▶ │  Python MCP      │ ── JSON-RPC 2.0 ──▶│  C# Plugin       │
│   AI Client  │◀──────────────│  Server           │◀────────────────── │  (inside AutoCAD) │
└─────────────┘                └──────────────────┘   localhost:8081    └──────────────────┘
                                                                              │
┌──────────────┐                          HTTP / JSON-RPC 2.0                  │
│   Browser    │ ──────────────────────────────────────────────────────────────▶│
│   (web app)  │  POST http://127.0.0.1:8082/jsonrpc  (CORS + Chrome PNA ok)   │
└──────────────┘                                                                │
                                                                       AutoCAD .NET API
```

| Component | Language | Location |
|-----------|----------|----------|
| **AutoCADMCPPlugin.dll** | C# | `src/AutoCADMCPPlugin/` |
| **MCP Server** | Python | `src/mcp_server/` |
| **Bundle Manifest** | XML | `config/AutoCADMCPPlugin.bundle/` |

### How It Works

The C# plugin loads inside AutoCAD as an addin and exposes the same JSON-RPC pipeline over **two transports** simultaneously:

1. **TCP socket on `localhost:8081`** — used by the Python MCP server, which marshals 79 tools over stdio for Claude / Claude Code / Claude Desktop.
2. **HTTP loopback on `localhost:8082`** — used by browser apps that can't open raw TCP sockets (a `fetch()` call to `http://127.0.0.1:8082/jsonrpc` reaches every command in the registry, with CORS headers + Chrome Private-Network-Access support out of the box).

Both transports route through the same `JsonRpcHandler`, so every command in the registry (`create_line`, `create_table`, `capture_screenshot`, …) is reachable identically from either path. Commands are marshaled to AutoCAD's main UI thread via `Application.Idle` + `DocumentLock`.

### Thread Safety

AutoCAD's .NET API is single-threaded. The plugin uses `Application.Idle` event + `DocumentLock` to safely execute commands from the socket handler threads on the main thread.

## Features — 79 MCP Tools

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

## Two Extra Commands, JSON-RPC Only

The plugin's command registry holds **80** commands — the 79 MCP tools above plus two text-measurement
commands that are reachable over TCP/JSON-RPC and the HTTP shim but are deliberately not wrapped as MCP
tools, because they exist to size tables before drawing them and are of little use to a chat client. This
is why `list_methods` returns 80 while the MCP server exposes 79.

| Command | Description |
|---------|-------------|
| `measure_text` | Bounding box of one text string at a given height + style. SHX glyphs are proportional, so JS estimates miss; this returns the real width AutoCAD will render. |
| `measure_texts` | Batched variant — one transaction for up to 2000 strings. Use when sizing many cells before drawing. |

See [Sizing tables to real text widths](#sizing-tables-to-real-text-widths) for a worked example.

## Supported AutoCAD Versions

| AutoCAD Version | .NET Target | NuGet Package Version |
|----------------|-------------|----------------------|
| **2022, 2023, 2024** | .NET Framework 4.8 (`net48`) | AutoCAD.NET 24.2.x |
| **2025, 2026** | .NET 8 (`net8.0-windows`) | AutoCAD.NET 25.x |
| **2027** | .NET 10 (`net10.0-windows`) | AutoCAD.NET 26.0.0 |

The `net48` and `net8.0-windows` targets are always built. The 2027 target compiles against the `Newtonsoft.Json` that ships inside AutoCAD 2027 — so that the compile-time and run-time signatures match — and is therefore added only when AutoCAD 2027 is present on the build machine; elsewhere the build produces the two older targets instead of failing on a reference it cannot resolve. Point it at a non-default install with `dotnet build ... -p:AutoCADPath2027="D:\...\AutoCAD 2027"`.

The bundle manifest auto-selects the correct DLL by AutoCAD release series (`R24.1`–`R26.0`).

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
        ├── server.py              # 79 MCP tools via FastMCP
        ├── autocad_client.py      # Async TCP client with auto-reconnect
        └── requirements.txt
```

## Adding New Commands

1. Create a class implementing `ICommand` in `Commands/`
2. It's auto-discovered by `CommandRegistry` — no manual registration needed
3. Add the corresponding MCP tool in `server.py`
4. Rebuild and reinstall: `install.bat`

Example:

```csharp
public class MyCommand : ICommand
{
    public string MethodName => "my_command";

    public CommandResult Execute(JObject parameters)
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        using (EntityHelper.LockDoc())
        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            // Your AutoCAD API code here
            tr.Commit();
        }
        return CommandResult.Ok(new JObject { ["message"] = "Done" });
    }
}
```

## License

MIT
