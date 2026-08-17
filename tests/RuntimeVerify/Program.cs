using AutoCADMCP.Server;

namespace AutoCADMCP.RuntimeVerify;

/// <summary>
/// Runtime verification harness for the AutoCAD MCP plugin.
///
/// Talks straight to the plugin's TCP JSON-RPC port, exercising a representative
/// tool from every category and chaining real entity handles through
/// create -> query -> modify, exactly as an AI client would.
///
/// This is an INTEGRATION test against a live AutoCAD, not a unit test. It needs
/// AutoCAD running with a drawing open, the plugin loaded, and MCPSTART issued.
/// The hermetic checks that need none of that live in tests/ServerToolTests and
/// build/verify-assembly.ps1.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine(
"""
runtime-verify - integration check against a live AutoCAD

  Requires AutoCAD running with a drawing open, the plugin loaded, and
  MCPSTART issued (listening on :8081).

Usage:
  dotnet run --project tests/RuntimeVerify                 core smoke run
  dotnet run --project tests/RuntimeVerify -- --all        also sweep every tool
  dotnet run --project tests/RuntimeVerify -- --port 8081

Options:
  --host <host>     default: localhost
  --port <port>     default: 8081
  --all             sweep every registered tool with no arguments
  -v, --verbose     print each result payload
""");
            return 0;
        }

        string host = Option(args, "--host") ?? "localhost";
        int port = int.TryParse(Option(args, "--port"), out int p) && p is > 0 and <= 65535 ? p : 8081;
        bool all = args.Contains("--all");
        bool verbose = args.Any(a => a is "-v" or "--verbose");

        Console.WriteLine(new string('=', 62));
        Console.WriteLine("  AutoCAD MCP - runtime verification");
        Console.WriteLine($"  connecting to {host}:{port}");
        Console.WriteLine(new string('=', 62));

        var client = new PluginClient(host, port, 30_000);

        if (!await client.IsReachableAsync())
        {
            Console.WriteLine($"\n  Could not connect to {host}:{port}.");
            Console.WriteLine("  Is AutoCAD running with the plugin loaded and MCPSTART issued?");
            return 2;
        }

        var runner = new Runner(client, verbose);
        await Suites.CoreAsync(runner);
        if (all) await Suites.AllToolsAsync(runner);

        return runner.Summary();
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        if (i >= 0 && i + 1 < args.Length) return args[i + 1];

        string prefix = name + "=";
        return args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }
}
