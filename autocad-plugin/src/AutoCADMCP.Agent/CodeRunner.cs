using System.Text;
using AutoCADMCP.Server;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoCADMCP.Agent;

/// <summary>The plugin reported an error, or could not be reached.</summary>
public sealed class PluginException : Exception
{
    public PluginException(string message) : base(message) { }
}

/// <summary>
/// What generated code can see. Every member here is deliberate: the script gets
/// a way to reach AutoCAD and a place to put its answer, and nothing else.
///
/// It can still call anything in the BCL — this is not a sandbox, which is why
/// execution is opt-in. What it protects is the shape of the contract, so the
/// model has one obvious way to do the thing it is being asked to do.
/// </summary>
public sealed class ScriptHost
{
    private readonly PluginClient _plugin;
    private readonly int _timeoutMs;
    private readonly StringBuilder _output;

    internal ScriptHost(PluginClient plugin, int timeoutMs, StringBuilder output)
    {
        _plugin = plugin;
        _timeoutMs = timeoutMs;
        _output = output;
    }

    /// <summary>The script's answer, if the task asked a question.</summary>
    public object? Result { get; set; }

    /// <summary>Invoke a plugin method. Throws <see cref="PluginException"/> on failure.</summary>
    public JObject Call(string method, object? parameters = null)
    {
        JObject payload = parameters switch
        {
            null => new JObject(),
            JObject o => o,
            _ => JObject.FromObject(parameters),
        };

        using var cts = new CancellationTokenSource(_timeoutMs);

        JObject response;
        try
        {
            // The script is synchronous by design - generated code reads far better
            // without await on every call - so block here deliberately.
            response = _plugin.CallAsync(method, payload, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new PluginException($"'{method}' timed out after {_timeoutMs} ms.");
        }
        catch (Exception ex)
        {
            throw new PluginException(
                $"Could not reach the AutoCAD plugin ({ex.Message}). " +
                "Start AutoCAD, load the plugin, and run MCPSTART.");
        }

        if (response["error"] is JObject err)
        {
            string? code = err["data"]?["errorCode"]?.ToString();
            string message = err["message"]?.ToString() ?? "unknown plugin error";
            throw new PluginException(string.IsNullOrEmpty(code) ? message : $"[{code}] {message}");
        }

        return response["result"] as JObject ?? new JObject();
    }

    /// <summary>Captured so the caller sees it even though stdout is MCP traffic.</summary>
    public void Print(object? value) => _output.Append(value).Append('\n');
}

/// <summary>
/// Compiles and runs the C# the model wrote, capturing output and failures as
/// data rather than letting them escape into the MCP transport.
/// </summary>
public static class CodeRunner
{
    private static readonly ScriptOptions Options = ScriptOptions.Default
        .WithImports(
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "System.Text",
            "System.Math",
            "Newtonsoft.Json",
            "Newtonsoft.Json.Linq")
        .WithReferences(
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(JObject).Assembly,
            typeof(JsonConvert).Assembly,
            typeof(ScriptHost).Assembly);

    public static async Task<JObject> RunAsync(string code, PluginClient plugin, int timeoutMs)
    {
        var output = new StringBuilder();
        var host = new ScriptHost(plugin, timeoutMs, output);

        // Console.Out is the MCP channel; a stray Console.WriteLine in generated
        // code would corrupt the protocol, so redirect it for the duration.
        TextWriter savedOut = Console.Out, savedErr = Console.Error;
        var captured = new StringWriter();
        Console.SetOut(captured);
        Console.SetError(captured);

        try
        {
            await CSharpScript.RunAsync(code, Options, host, typeof(ScriptHost));
        }
        catch (CompilationErrorException ex)
        {
            return Failure("The generated code did not compile.",
                           string.Join("\n", ex.Diagnostics), output, captured);
        }
        catch (PluginException ex)
        {
            return Failure(ex.Message, null, output, captured);
        }
        catch (Exception ex)
        {
            return Failure($"{ex.GetType().Name}: {ex.Message}",
                           ex.StackTrace, output, captured);
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }

        var result = new JObject
        {
            ["executed"] = true,
            ["success"] = true,
            ["output"] = Tail(output + captured.ToString(), 8000),
        };

        if (host.Result != null)
        {
            try { result["result"] = JToken.FromObject(host.Result); }
            catch (JsonException) { result["result"] = Tail(host.Result.ToString() ?? "", 2000); }
        }
        return result;
    }

    private static JObject Failure(string error, string? detail, StringBuilder output, StringWriter captured)
    {
        var o = new JObject
        {
            ["executed"] = true,
            ["success"] = false,
            ["error"] = error,
            ["output"] = Tail(output + captured.ToString(), 4000),
        };
        if (!string.IsNullOrEmpty(detail)) o["detail"] = Tail(detail, 4000);
        return o;
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : s[^max..];
}
