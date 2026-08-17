# AutoCAD MCP Plugin

AI-to-AutoCAD bridge using the Model Context Protocol (MCP). Enables Claude and other AI assistants to create and modify AutoCAD drawings through natural language.

## Architecture

```
Claude (AI) ──MCP stdio──> MCP Server (C# exe) ──TCP socket──> C# Plugin ──API──> AutoCAD
```

| Component | Language | Role |
|-----------|----------|------|
| **AutoCADMCPPlugin.dll** | C# (.NET Framework 4.8 / .NET 8 / .NET 10) | Loads inside AutoCAD, exposes TCP socket server on localhost:8081 |
| **AutoCADMCP.Server** | C# (.NET 8, self-contained exe) | Translates MCP tool calls into JSON-RPC 2.0 over TCP |
| **AutoCADMCP.Agent** | C# (.NET 8, optional) | Turns a request into generated AutoCAD code |
| **Claude / Claude Code** | — | AI client that calls MCP tools |

### Thread Safety

AutoCAD's .NET API is single-threaded (UI thread only). The plugin uses `Application.Idle` event marshaling (similar to Revit's `ExternalEvent` pattern) to safely execute commands from socket handler threads on the main thread.

## Supported Tools

**183 MCP tools** across system, drawing, entities, layers, blocks, annotations,
layouts/paper space, xrefs, block attributes, modify operations, 3D solids,
groups/layer states/views/UCS, and drawing data/audit.

The authoritative tool table lives in the [root README](../README.md#features--183-mcp-tools) —
it is kept in sync with `Core/CommandRegistry.cs` and is not duplicated here.

At runtime, ask the plugin itself:

- `list_methods` — every registered method name
- `get_capabilities` — counts, build target, supported AutoCAD range, destructive-tool list

## Setup

### Prerequisites
- **AutoCAD 2025** (or 2024 with .NET Framework 4.8 — adjust .csproj TargetFramework)
- **Visual Studio 2022** or `dotnet` CLI (SDK 8.0+)

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

### 3. Build the MCP Server

```bash
dotnet publish src/AutoCADMCP.Server -c Release -o dist/server
```

Produces one self-contained `autocad-mcp-server.exe`; nothing else to install.
`--check` reports whether it can reach the plugin.

### 4. Configure Claude

Add to your `claude_desktop_config.json` or `.mcp.json`:

```json
{
  "mcpServers": {
    "autocad": {
      "command": "<full-path-to>/dist/server/autocad-mcp-server.exe",
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

## AutoCAD Version Support

No .csproj editing is needed — the project multi-targets and the bundle manifest
selects the right binary for whichever AutoCAD is running.

| AutoCAD | Release | Target | Built by default |
|---------|---------|--------|------------------|
| 2021–2024 | R24.0–R24.3 | `net48` | yes |
| 2025–2026 | R25.0–R25.1 | `net8.0-windows` | yes |
| 2027 | R26.0 | `net10.0-windows` | no — needs .NET 10 SDK |

```bash
dotnet build -c Release                       # net48 + net8.0
dotnet build -c Release -p:IncludeNet10=true  # + net10.0 (AutoCAD 2027)
```

The `net48` leg is compiled against AutoCAD **2021** reference assemblies on
purpose: a plugin may only use APIs present in the oldest release it targets, so
this is what allows one binary to load on 2021 through 2024. If someone uses a
newer API by accident, the build fails — that is the guardrail.

All version-conditional code lives in `Core/AcadCompat.cs`. Keep it that way.

**AutoCAD LT is not supported and cannot be** — LT has no `NETLOAD` and cannot
load .NET plugins at any version.
