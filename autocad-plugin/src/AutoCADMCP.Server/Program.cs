using System.Text;

namespace AutoCADMCP.Server;

/// <summary>
/// Entry point for the self-contained AutoCAD MCP server.
///
/// Speaks MCP over stdio to the AI client and JSON-RPC over TCP to the AutoCAD
/// plugin. stdout is reserved for protocol traffic; all diagnostics go to stderr,
/// because a stray byte on stdout breaks the client's parser.
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

        if (args.Contains("--list-tools"))
        {
            // Machine-readable tool surface, for build/verify-assembly.ps1. That
            // script runs under Windows PowerShell, which cannot reflect over a
            // net8.0 assembly, so it asks the server instead of reading its DLL.
            PrintToolSurface();
            return 0;
        }

        string host = GetOption(args, "--host")
                      ?? Environment.GetEnvironmentVariable("AUTOCAD_MCP_HOST")
                      ?? "localhost";

        int port = ParsePort(GetOption(args, "--port")
                             ?? Environment.GetEnvironmentVariable("AUTOCAD_MCP_PORT"));

        int timeout = ParseTimeout(GetOption(args, "--timeout")
                                   ?? Environment.GetEnvironmentVariable("AUTOCAD_MCP_TIMEOUT_MS"));

        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = false
        };
        var stderr = Console.Error;

        var plugin = new PluginClient(host, port, timeout);
        var server = new McpServer(plugin, stdout, stderr);

        stderr.WriteLine($"autocad-mcp server: {server.ToolCount} tools, plugin at {host}:{port}");

        if (args.Contains("--check"))
        {
            // Diagnostic mode: report reachability and exit without speaking MCP.
            bool up = await plugin.IsReachableAsync();
            stderr.WriteLine(up
                ? $"plugin reachable at {host}:{port}"
                : $"plugin NOT reachable at {host}:{port} - start AutoCAD and run MCPSTART");
            return up ? 0 : 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            await server.RunAsync(stdin, cts.Token);
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

    /// <summary>
    /// Emit the tool surface as JSON on stdout: everything advertised to the
    /// client, and the subset this process serves itself rather than proxying
    /// to the plugin.
    /// </summary>
    private static void PrintToolSurface()
    {
        var advertised = new Newtonsoft.Json.Linq.JArray(
            McpServer.LoadToolCatalogue().Select(t => t["name"]?.ToString() ?? ""));
        var local = new Newtonsoft.Json.Linq.JArray(Tools.ServerTools.All.Keys);

        Console.WriteLine(new Newtonsoft.Json.Linq.JObject
        {
            ["advertised"] = advertised,
            ["local"] = local,
        }.ToString(Newtonsoft.Json.Formatting.None));
    }

    private static string? GetOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        if (i >= 0 && i + 1 < args.Length) return args[i + 1];

        string prefix = name + "=";
        return args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    private static int ParsePort(string? raw)
    {
        const int fallback = 8081;
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out int p) && p is > 0 and <= 65535 ? p : fallback;
    }

    private static int ParseTimeout(string? raw)
    {
        const int fallback = 120_000;
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out int t) && t > 0 ? t : fallback;
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine(
"""
autocad-mcp-server - MCP server for AutoCAD

  Bridges an MCP client (Claude Desktop, Claude Code, ...) to the AutoCAD MCP
  plugin. Requires AutoCAD running with the plugin loaded and MCPSTART issued.

Usage:
  autocad-mcp-server [--host <host>] [--port <port>] [--timeout <ms>]
  autocad-mcp-server --check      Report whether the plugin is reachable, then exit
  autocad-mcp-server --help

Environment:
  AUTOCAD_MCP_HOST        default: localhost
  AUTOCAD_MCP_PORT        default: 8081
  AUTOCAD_MCP_TIMEOUT_MS  default: 120000

MCP client configuration:
  {
    "mcpServers": {
      "autocad": { "command": "C:\\path\\to\\autocad-mcp-server.exe" }
    }
  }
""");
    }
}
