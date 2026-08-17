using System.Text;
using Anthropic.Models.Messages;
using AutoCADMCP.Server;
using Newtonsoft.Json.Linq;

namespace AutoCADMCP.Agent;

/// <summary>Everything the agent reads from the environment, in one place.</summary>
public sealed record Config(string Host, int Port, string Model, Effort Effort, bool AllowExec)
{
    public static Config FromEnvironment()
    {
        string host = Environment.GetEnvironmentVariable("AUTOCAD_MCP_HOST") ?? "localhost";

        int port = int.TryParse(Environment.GetEnvironmentVariable("AUTOCAD_MCP_PORT"), out int p)
                   && p is > 0 and <= 65535 ? p : 8081;

        string model = Environment.GetEnvironmentVariable("AUTOCAD_AGENT_MODEL") ?? "claude-opus-5";

        Effort effort = (Environment.GetEnvironmentVariable("AUTOCAD_AGENT_EFFORT") ?? "high")
                        .Trim().ToLowerInvariant() switch
        {
            "low" => Effort.Low,
            "medium" => Effort.Medium,
            "max" => Effort.Max,
            _ => Effort.High,
        };

        bool allowExec = (Environment.GetEnvironmentVariable("AUTOCAD_AGENT_ALLOW_EXEC") ?? "")
                         .Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

        return new Config(host, port, model, effort, allowExec);
    }
}

/// <summary>
/// AutoCode Agent — an MCP server that turns a drawing request into AutoCAD
/// code, then optionally runs it against a live AutoCAD.
///
/// Execution is OFF unless AUTOCAD_AGENT_ALLOW_EXEC=1. Generated code is not
/// sandboxed: it runs in this process with full filesystem and network access.
/// It reaches AutoCAD only through the plugin's JSON-RPC port, so the plugin's
/// read-only mode and destructive-confirmation gates still apply to what it
/// draws.
///
/// Credentials are resolved by the Anthropic SDK (ANTHROPIC_API_KEY, or an
/// `ant auth login` profile). Nothing here reads or stores a key.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            return 0;
        }

        var config = Config.FromEnvironment();
        var plugin = new PluginClient(config.Host, config.Port, 120_000);
        var tools = new AgentTools(config, plugin);

        var stderr = Console.Error;
        stderr.WriteLine($"AutoCode Agent: model={config.Model} effort={config.Effort.ToString().ToLowerInvariant()} " +
                         $"plugin={config.Host}:{config.Port} execution={(config.AllowExec ? "on" : "off")}");

        if (args.Contains("--check"))
        {
            var status = await tools.CallAsync("agent_status", new JObject(), CancellationToken.None);
            stderr.WriteLine(status.ToString());
            return 0;
        }

        // stdout is protocol traffic only, so buffer it and flush per message.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = false,
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            await new AgentMcpServer(tools, stdout, stderr).RunAsync(stdin, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"fatal: {ex.Message}");
            return 1;
        }
        finally
        {
            stdout.Flush();
        }

        return 0;
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine(
"""
autocad-mcp-agent - turns a drawing request into AutoCAD code

  An MCP server exposing three tools:
    agent_status            what is configured and reachable
    generate_drawing_code   plan + code, nothing executed  - always available
    draw                    generate, then execute         - opt-in

  Needs the AutoCAD MCP plugin running (MCPSTART) for `draw` and for
  agent_status to report the plugin as reachable. Code generation needs only
  Anthropic credentials.

Usage:
  autocad-mcp-agent                Speak MCP over stdio
  autocad-mcp-agent --check        Print agent status, then exit
  autocad-mcp-agent --help

Environment:
  ANTHROPIC_API_KEY            resolved by the Anthropic SDK
  AUTOCAD_MCP_HOST             default: localhost
  AUTOCAD_MCP_PORT             default: 8081
  AUTOCAD_AGENT_MODEL          default: claude-opus-5
  AUTOCAD_AGENT_EFFORT         low | medium | high | max   default: high
  AUTOCAD_AGENT_ALLOW_EXEC     set to 1 to enable `draw`   default: off
""");
    }
}
