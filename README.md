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
                                                                       AutoCAD .NET API
```

| Component | Language | Location |
|-----------|----------|----------|
| **AutoCADMCPPlugin.dll** | C# | `src/AutoCADMCPPlugin/` |
| **MCP Server** | Python | `src/mcp_server/` |
| **Bundle Manifest** | XML | `config/AutoCADMCPPlugin.bundle/` |

### How It Works

1. The **C# plugin** loads inside AutoCAD as an addin and starts a TCP socket server on `localhost:8081`
2. The **Python MCP server** connects to the plugin socket and exposes 71 tools via the MCP protocol
3. **Claude** (or any MCP client) calls tools like `create_line`, `search_text`, `capture_screenshot`, etc.
4. Commands are marshaled to AutoCAD's main UI thread via `Application.Idle` + `DocumentLock`

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
| `create_hatch` | Hatch with boundary and pattern |
| `create_spline` | Smooth spline curve through points |
| `create_table` | Table with rows, columns, and cell data |
| `create_block` | Define a new block from geometry |
| `bulk_create` | Create multiple entities in one call |

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
- **.NET SDK 8.0+** (for building)
- **Python 3.10+** (for MCP server)
- **Windows** (AutoCAD is Windows-only)

### Step 1: Build & Install the Plugin

```bash
cd autocad-plugin
install.bat
```

This builds both .NET targets, copies the DLLs to `%APPDATA%\Autodesk\ApplicationPlugins\AutoCADMCPPlugin.bundle\`, and AutoCAD will load it automatically on startup.

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

Or verify manually:

```python
import socket, json
sock = socket.socket()
sock.connect(('localhost', 8081))
sock.sendall(json.dumps({
    "jsonrpc": "2.0", "method": "system_status", "params": {}, "id": "1"
}).encode() + b"\n")
print(sock.recv(4096).decode())
```

## Uninstall

```bash
cd autocad-plugin
uninstall.bat
```

Or manually delete: `%APPDATA%\Autodesk\ApplicationPlugins\AutoCADMCPPlugin.bundle\`

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `AUTOCAD_MCP_HOST` | `localhost` | Plugin socket host |
| `AUTOCAD_MCP_PORT` | `8081` | Plugin socket port |

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
    │   │   ├── Plugin.cs          # IExtensionApplication entry point
    │   │   ├── SocketServer.cs    # TCP server (JSON-RPC 2.0)
    │   │   ├── JsonRpcHandler.cs  # Protocol handler
    │   │   ├── IdleActionRunner.cs# Thread marshaling via Application.Idle
    │   │   └── CommandRegistry.cs # Auto-discovers ICommand implementations
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
