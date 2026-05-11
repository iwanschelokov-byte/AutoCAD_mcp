# AutoCAD MCP Plugin

AI-powered AutoCAD automation via the **Model Context Protocol (MCP)**. Enables Claude and other AI assistants to create, modify, query, search, and visually verify AutoCAD drawings through natural language.

> "Draw a floor plan with 3 bedrooms" — and it does.
> "Find the battery room nearest to the toilet" — and it navigates there.
> "Take a screenshot and check if the layout looks correct" — and it verifies visually.

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

1. **TCP socket on `localhost:8081`** — used by the Python MCP server, which marshals 71 tools over stdio for Claude / Claude Code / Claude Desktop.
2. **HTTP loopback on `localhost:8082`** — used by browser apps that can't open raw TCP sockets (a `fetch()` call to `http://127.0.0.1:8082/jsonrpc` reaches every command in the registry, with CORS headers + Chrome Private-Network-Access support out of the box).

Both transports route through the same `JsonRpcHandler`, so the 71 tools (`create_line`, `create_table`, `capture_screenshot`, …) are reachable identically from either path. Commands are marshaled to AutoCAD's main UI thread via `Application.Idle` + `DocumentLock`.

### Thread Safety

AutoCAD's .NET API is single-threaded. The plugin uses `Application.Idle` event + `DocumentLock` to safely execute commands from the socket handler threads on the main thread.

## Features — 71 MCP Tools

### System (5)
| Tool | Description |
|------|-------------|
| `system_status` | Plugin version, AutoCAD version, active document |
| `list_methods` | All available commands |
| `set_system_variable` | Set AutoCAD system variables (DIMTXT, LTSCALE, etc.) |
| `get_system_variable` | Read system variable values |
| `execute_command` | Run raw AutoCAD command strings |

### Drawing Management (7)
| Tool | Description |
|------|-------------|
| `drawing_new` | Create new drawing (optional template) |
| `drawing_open` | Open existing .dwg file |
| `drawing_save` | Save / Save As |
| `drawing_info` | Entity count, layers, file path |
| `set_units` | Set linear and angular units |
| `purge_drawing` | Remove unused blocks, layers, styles |
| `plot_to_pdf` | Plot current layout to PDF |

### Entity Creation (14)
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
| `create_block` | Define a new block from geometry |
| `bulk_create` | Create multiple entities in one call |

### Text Measurement (2)
| Tool | Description |
|------|-------------|
| `measure_text` | Bounding box of one text string at a given height + style. SHX glyphs are proportional, so JS estimates miss; this returns the real width AutoCAD will render. |
| `measure_texts` | Batched variant — one transaction for up to 2000 strings. Use when sizing many cells before drawing. |

### Entity Query (8)
| Tool | Description |
|------|-------------|
| `list_entities` | List entities with layer/type filters |
| `get_entity` | Detailed entity info by handle |
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

### Blocks (3)
| Tool | Description |
|------|-------------|
| `list_blocks` | Block definitions with attributes |
| `insert_block` | Insert with position/rotation/scale/attributes |
| `create_block` | Define a new block from geometry |

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

## Supported AutoCAD Versions

| AutoCAD Version | .NET Target | NuGet Package Version |
|----------------|-------------|----------------------|
| **2022, 2023, 2024** | .NET Framework 4.8 (`net48`) | AutoCAD.NET 24.2.x |
| **2025, 2026** | .NET 8 (`net8.0-windows`) | AutoCAD.NET 25.x |

Both targets are built simultaneously. The bundle manifest auto-selects the correct DLL.

## Installation

### Prerequisites

- **AutoCAD 2022–2026** (any edition including LT with .NET support)
- **Python 3.10+** (for MCP server)
- **Windows** (AutoCAD is Windows-only)
- **.NET SDK 8.0+** (only if building from source)

### Option A: Install Pre-built Plugin (No Build Tools Needed)

**Close AutoCAD first**, then run:

```bash
cd autocad-plugin
install-prebuilt.bat
```

This copies the pre-built DLLs from `dist/` to `%APPDATA%\Autodesk\ApplicationPlugins\` and AutoCAD will load the plugin automatically on startup.

### Option B: Build from Source

If you have .NET SDK installed:

```bash
cd autocad-plugin
install.bat
```

This builds both .NET targets, copies the DLLs to `%APPDATA%\Autodesk\ApplicationPlugins\AutoCADMCPPlugin.bundle\`, and AutoCAD will load it automatically on startup.

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

## Calling from a Browser / Web App

The HTTP shim makes the full 71-tool surface reachable from any browser-based internal tool — no MCP client, no Claude in the loop, no per-engineer license. Engineers run the plugin once (`MCPSTART` in AutoCAD), and your web app calls `localhost:8082` directly.

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
├── install.bat                    # Build + deploy to ApplicationPlugins
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
    │   │   ├── SearchCommands.cs          # search_text, find_nearest, measure_between
    │   │   └── ScreenshotCommand.cs       # capture_screenshot (Windows API viewport capture)
    │   └── Models/
    │       ├── ICommand.cs        # Command interface
    │       └── CommandResult.cs   # Result wrapper
    └── mcp_server/                # Python MCP Server
        ├── server.py              # 71 MCP tools via FastMCP
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
