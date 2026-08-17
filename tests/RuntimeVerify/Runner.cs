using AutoCADMCP.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoCADMCP.RuntimeVerify;

/// <summary>
/// Calls tools and records what happened. Every outcome is one of PASS, FAIL
/// (with the plugin's typed errorCode), or SKIP, so a run reads as a report
/// rather than a stack trace.
/// </summary>
public sealed class Runner
{
    private readonly PluginClient _client;
    private readonly bool _verbose;

    public Runner(PluginClient client, bool verbose)
    {
        _client = client;
        _verbose = verbose;
    }

    public List<string> Passed { get; } = [];
    public List<(string Name, string Why)> Failed { get; } = [];
    public List<string> Skipped { get; } = [];

    /// <summary>Raw call, for the cases that assert on the error itself.</summary>
    public Task<JObject> RawAsync(string method, object? parameters = null) =>
        _client.CallAsync(method, ToParams(parameters), CancellationToken.None);

    /// <summary>Call a tool; record the outcome and return its result payload.</summary>
    public async Task<JObject?> RunAsync(string label, string method, object? parameters = null)
    {
        JObject response;
        try
        {
            response = await RawAsync(method, parameters);
        }
        catch (Exception ex)
        {
            Failed.Add((label, $"transport: {ex.Message}"));
            Console.WriteLine($"  FAIL  {label,-34} transport error: {ex.Message}");
            return null;
        }

        if (response["error"] is JObject err)
        {
            string code = err["data"]?["errorCode"]?.ToString() ?? "?";
            string message = err["message"]?.ToString() ?? "";
            Failed.Add((label, $"{code}: {message}"));
            Console.WriteLine($"  FAIL  {label,-34} [{code}] {Truncate(message, 70)}");
            return null;
        }

        Passed.Add(label);
        var result = response["result"] as JObject;

        if (_verbose)
            Console.WriteLine($"  PASS  {label,-34} " +
                              Truncate(result?.ToString(Formatting.None) ?? "", 80));
        else
            Console.WriteLine($"  PASS  {label}");

        return result;
    }

    public void Skip(string label, string why)
    {
        Skipped.Add(label);
        Console.WriteLine($"  SKIP  {label,-34} {why}");
    }

    public void Note(string label, bool ok, string why)
    {
        if (ok) { Passed.Add(label); Console.WriteLine($"  PASS  {label}"); }
        else { Failed.Add((label, why)); Console.WriteLine($"  FAIL  {label,-34} {why}"); }
    }

    public int Summary()
    {
        int total = Passed.Count + Failed.Count;
        Console.WriteLine();
        Console.WriteLine(new string('=', 62));
        Console.WriteLine($"  {Passed.Count}/{total} passed   |  {Failed.Count} failed  " +
                          $"|  {Skipped.Count} skipped");

        if (Failed.Count > 0)
        {
            Console.WriteLine("\n  Failures:");
            foreach (var (name, why) in Failed) Console.WriteLine($"    - {name}: {why}");
        }

        Console.WriteLine(new string('=', 62));
        return Failed.Count > 0 ? 1 : 0;
    }

    internal static JObject ToParams(object? parameters) => parameters switch
    {
        null => new JObject(),
        JObject o => o,
        _ => JObject.FromObject(parameters),
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
