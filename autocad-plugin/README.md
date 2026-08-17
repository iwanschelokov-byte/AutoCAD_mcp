# AutoCAD MCP Plugin

AI-to-AutoCAD bridge using the Model Context Protocol (MCP). Enables Claude and other AI assistants to create and modify AutoCAD drawings through natural language.

## Architecture

```
Claude (AI) ──MCP stdio──> Python MCP Server ──TCP socket──> C# Plugin ──API──> AutoCAD
```

| Component | Language | Role |
|-----------|----------|------|
| **AutoCADMCPPlugin.dll** | C# (.NET Framework 4.8 / .NET 8 / .NET 10) | Loads inside AutoCAD, exposes TCP socket server on localhost:8081 |
| **mcp_server/server.py** | Python | Translates MCP tool calls into JSON-RPC 2.0 over TCP |
| **Claude / Claude Code** | — | AI client that calls MCP tools |

### Thread Safety

AutoCAD's .NET API is single-threaded (UI thread only). The plugin uses `Application.Idle` event marshaling (similar to Revit's `ExternalEvent` pattern) to safely execute commands from socket handler threads on the main thread.

## Supported Tools (33 total)

### System
- `system_status` — Plugin version, AutoCAD version, active document
- `list_methods` — All available commands

### Drawing Management
- `drawing_new` — Create new drawing (optional template)
- `drawing_open` — Open .dwg file
- `drawing_save` — Save / Save As
- `drawing_info` — Entity count, layers, file path

### Entity Creation (9 tools)
- `create_line` — Line from start to end point
- `create_circle` — Circle at center with radius
- `create_arc` — Arc with center, radius, start/end angle
- `create_polyline` — Polyline through points (open or closed)
- `create_rectangle` — Rectangle from two corners
- `create_ellipse` — Ellipse with major/minor radii
- `create_text` — Single-line text
- `create_mtext` — Multi-line text
- `create_hatch` — Hatch with boundary and pattern

### Entity Query & Modification (7 tools)
- `list_entities` — List entities (filter by layer/type)
- `get_entity` — Detailed entity info by handle
- `erase_entity` — Delete entity
- `move_entity` — Move entity
- `copy_entity` — Copy entity
- `rotate_entity` — Rotate entity
- `scale_entity` — Scale entity
- `mirror_entity` — Mirror entity

### Layers (4 tools)
- `list_layers` — All layers with properties
- `create_layer` — New layer with color/linetype
- `set_current_layer` — Switch active layer
- `set_layer_properties` — Modify color, freeze, lock, etc.

### Blocks (2 tools)
- `list_blocks` — Block definitions with attributes
- `insert_block` — Insert block with position/rotation/scale/attributes

### Annotations (2 tools)
- `create_linear_dimension` — Horizontal/vertical dimension
- `create_aligned_dimension` — Aligned dimension

### View (2 tools)
- `zoom_extents` — Fit all entities
- `zoom_window` — Zoom to area

## Setup

### Prerequisites
- **AutoCAD 2025** (or 2024 with .NET Framework 4.8 — adjust .csproj TargetFramework)
- **Visual Studio 2022** or `dotnet` CLI
- **Python 3.10+**

### 1. Build the Plugin

```bash
cd src/AutoCADMCPPlugin
dotnet build -c Release
```

Output: `bin/Release/net8.0-windows/AutoCADMCPPlugin.dll`

### 2. Load into AutoCAD

**Option A: Manual (NETLOAD)**
1. Open AutoCAD
2. Type `NETLOAD` in the command line
3. Browse to `AutoCADMCPPlugin.dll`
4. Type `MCPSTART` to start the socket server

**Option B: Auto-load (Bundle)**
1. Copy `config/AutoCADMCPPlugin.bundle/` to `%APPDATA%\Autodesk\ApplicationPlugins\`
2. Copy the built DLL into `AutoCADMCPPlugin.bundle/Contents/`
3. Restart AutoCAD — plugin loads automatically
4. Type `MCPSTART` to start the server

### 3. Install MCP Server

```bash
cd src/mcp_server
pip install -r requirements.txt
```

### 4. Configure Claude

Add to your `claude_desktop_config.json` or `.mcp.json`:

```json
{
  "mcpServers": {
    "autocad": {
      "command": "python",
      "args": ["<full-path-to>/src/mcp_server/server.py"],
      "env": {
        "AUTOCAD_MCP_HOST": "localhost",
        "AUTOCAD_MCP_PORT": "8081"
      }
    }
  }
}
```

### 5. Test

In Claude Code or Claude Desktop:
> "Draw a circle at (50, 50) with radius 25"

## AutoCAD Commands

| Command | Description |
|---------|-------------|
| `MCPSTART` | Start the TCP socket server (prompts for port) |
| `MCPSTOP` | Stop the server |
| `MCPSTATUS` | Show server status and connection count |

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `AUTOCAD_MCP_HOST` | `localhost` | Plugin socket host |
| `AUTOCAD_MCP_PORT` | `8081` | Plugin socket port |

## Adapting for Other AutoCAD Versions

| AutoCAD Version | .NET Version | Change in .csproj |
|----------------|-------------|-------------------|
| 2027 | .NET 10 | `net10.0-windows` + `AutoCAD.NET` 26.0.0 |
| 2025–2026 | .NET 8 | `net8.0-windows` (default) |
| 2024 | .NET Framework 4.8 | `net48` |
| 2023 and below | .NET Framework 4.8 | `net48` + adjust NuGet versions |

The `net48` and `net8.0-windows` targets are always built. The 2027 target is added only when AutoCAD 2027 is installed on the build machine, because it compiles against the `Newtonsoft.Json` that ships inside AutoCAD 2027 so the compile-time and run-time signatures match; on a machine without it the build produces the two older targets rather than failing on a reference it cannot resolve.

For AutoCAD 2024 and below, switch the NuGet packages:
```xml
<PackageReference Include="AutoCAD.NET.Core" Version="24.*" />
<PackageReference Include="AutoCAD.NET.Model" Version="24.*" />
```

Or reference DLLs directly from the AutoCAD install directory (see comments in .csproj).
