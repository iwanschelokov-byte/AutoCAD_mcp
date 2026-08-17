# AutoCode Agent

An MCP server that turns a drawing request into AutoCAD code. It asks Claude to
write C# against the plugin's tool surface, then optionally runs it.

This is a **second-stage** component and is entirely optional — the AutoCAD MCP
plugin and its server work without it. Use it when you want a request like
*"lay out a 3-bay rack elevation with dimensions"* handled as one generated
program rather than a few dozen individual tool calls.

## Why a separate server

A tool call is one round trip. Drawing anything with structure — a grid, a
schedule, a repeated detail — is dozens of them, and the model has to hold the
whole geometry in conversation while it makes them. Generating a short program
instead means the loop runs locally at full speed and only the result comes back.

It is a separate executable from `autocad-mcp-server` because it pulls in the
Anthropic SDK and the Roslyn scripting engine, and most users want the plain
tool bridge without either.

## Tools

| Tool | Needs AutoCAD | Needs execution enabled |
|---|---|---|
| `agent_status` | no | no |
| `generate_drawing_code` | no | no |
| `draw` | yes | yes |

`generate_drawing_code` returns a plan, the code, and the plugin methods it
calls, without running anything — read it, then decide. `draw` does the same and
then executes.

## Setup

```
dotnet build -c Release autocad-plugin/src/AutoCADMCP.Agent
```

Credentials are resolved by the Anthropic SDK — set `ANTHROPIC_API_KEY`, or run
`ant auth login` and let it use the stored profile. This server never reads or
stores a key itself.

The tool catalogue is embedded at build time from the same `tools.json` the MCP
server uses, so both surfaces describe one tool set and adding a tool to the
plugin makes it available here with no edit.

Then add it to your MCP client alongside the AutoCAD server:

```json
{
  "mcpServers": {
    "autocad-agent": {
      "command": "C:\\path\\to\\autocad-mcp-agent.exe",
      "env": {
        "AUTOCAD_AGENT_ALLOW_EXEC": "1"
      }
    }
  }
}
```

`autocad-mcp-agent --check` prints what is configured and reachable without
speaking MCP, which is the quickest way to confirm an install.

## Execution is off by default

`draw` refuses unless `AUTOCAD_AGENT_ALLOW_EXEC=1`. This is deliberate:
installing an AutoCAD plugin should not hand anyone a code evaluator.

What the flag does and does not protect:

- Generated code is **not sandboxed** — it runs in this process with full
  filesystem and network access.
- It reaches AutoCAD **only** through the plugin's JSON-RPC port, so the
  plugin's read-only mode and destructive-confirmation gates still apply to
  anything it draws. Enabling execution does not bypass them.

`generate_drawing_code` needs no permission and is the safe way to see what the
agent would do.

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `AUTOCAD_AGENT_ALLOW_EXEC` | off | Enables `draw` |
| `AUTOCAD_AGENT_MODEL` | `claude-opus-5` | Model used for generation |
| `AUTOCAD_AGENT_EFFORT` | `high` | Reasoning effort (`low`…`max`) |
| `AUTOCAD_MCP_HOST` | `localhost` | Plugin host |
| `AUTOCAD_MCP_PORT` | `8081` | Plugin port |

`AUTOCAD_AGENT_EFFORT` is the main cost lever. `medium` is noticeably cheaper and
usually fine for straightforward geometry; keep `high` for layouts that need real
spatial reasoning.

## What the generated code sees

`CodeRunner` runs the script with one method pre-bound:

```csharp
JObject Call(string method, object? parameters = null)
```

`System`, `System.Collections.Generic`, `System.Linq`, `System.Text`, the
Newtonsoft JSON namespaces, and `using static System.Math` are all imported, and
a `Result` property is available for the script's answer. A plugin error throws
`PluginException`, so generated code does not need to check a status field.

## How the prompt is built

`Prompts.cs` assembles the system prompt from three parts:

1. **The contract** — the single `Call` method the generated code gets, and what
   is pre-imported.
2. **The tool catalogue** — every tool with its full parameter signature, read
   from the embedded `tools.json`. About 7.5k tokens, and cached, so repeat
   requests only pay for the request itself.
3. **The traps** — behaviours that reliably catch callers out: handle
   round-tripping, parameter aliases, which tools need `__confirm`, that
   `execute_command` is asynchronous, that `measure_between` can return a null
   centre. Each one is a real behaviour of this plugin, not a guess.

## Status

`CodeRunner` and the prompt builder are covered by `tests/ServerToolTests`,
which executes real generated-style C# against a fake plugin on a real socket
and checks the compile-error, plugin-error, and runtime-exception paths.

The **code-generation path is unverified against the live API** — it was built
and shape-checked without credentials on the development machine. The request
shape compiles against the SDK and every parameter is accepted, but no real
generation has been run. Expect to iterate on the prompt when you first use it
in anger.
