using AutoCADMCP.Server;
using Newtonsoft.Json.Linq;

namespace AutoCADMCP.Agent;

/// <summary>
/// The three tools this server exposes. Every one returns JSON as a string —
/// failures included — so the caller never has to distinguish a transport error
/// from a drawing that could not be produced.
/// </summary>
public sealed class AgentTools
{
    private readonly Config _config;
    private readonly PluginClient _plugin;
    private readonly Lazy<Generator> _generator;

    public AgentTools(Config config, PluginClient plugin)
    {
        _config = config;
        _plugin = plugin;
        // Constructed on first use: the SDK looks for credentials eagerly, and
        // agent_status must keep working on a machine that has none.
        _generator = new Lazy<Generator>(() => new Generator(config.Model, config.Effort));
    }

    public static JArray Descriptors() =>
    [
        new JObject
        {
            ["name"] = "agent_status",
            ["description"] = "Report what the agent has available: model, tool catalogue, plugin, execution.",
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
            },
        },
        new JObject
        {
            ["name"] = "generate_drawing_code",
            ["description"] =
                "Generate AutoCAD C# for a drawing request, without running it. Returns a plan, " +
                "the code, and the plugin methods it calls, so you can read it before deciding " +
                "to run it. Needs no AutoCAD connection.",
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["request"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "What to draw.",
                    },
                    ["context"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional extra context: standards, existing layers, dimensions.",
                    },
                },
                ["required"] = new JArray("request"),
            },
        },
        new JObject
        {
            ["name"] = "draw",
            ["description"] =
                "Generate AutoCAD code for a request and run it against the open drawing. " +
                "Requires AUTOCAD_AGENT_ALLOW_EXEC=1 and a running AutoCAD with MCPSTART. " +
                "The generated code is not sandboxed. Drawing operations still pass through " +
                "the plugin's read-only and destructive-confirmation gates. Use " +
                "generate_drawing_code first if you want to read the code before it runs.",
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["request"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "What to draw.",
                    },
                    ["context"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional extra context: standards, existing layers, dimensions.",
                    },
                    ["timeout"] = new JObject
                    {
                        ["type"] = "number",
                        ["description"] = "Seconds to allow each plugin call. Default 120.",
                    },
                },
                ["required"] = new JArray("request"),
            },
        },
    ];

    public async Task<JObject> CallAsync(string name, JObject args, CancellationToken ct) => name switch
    {
        "agent_status" => await StatusAsync(ct),
        "generate_drawing_code" => await GenerateAsync(args, ct),
        "draw" => await DrawAsync(args, ct),
        _ => Fail($"Unknown tool '{name}'."),
    };

    private async Task<JObject> StatusAsync(CancellationToken ct)
    {
        int catalogue = Prompts.LoadTools().Count;
        bool reachable = await _plugin.IsReachableAsync();

        return new JObject
        {
            ["model"] = _config.Model,
            ["effort"] = _config.Effort.ToString().ToLowerInvariant(),
            ["tool_catalog"] = catalogue > 0 ? catalogue : "unavailable (tools.json was not embedded)",
            ["plugin"] = $"{_config.Host}:{_config.Port}",
            ["plugin_reachable"] = reachable,
            ["execution_enabled"] = _config.AllowExec,
            ["note"] = _config.AllowExec
                ? null
                : "Execution is off. Set AUTOCAD_AGENT_ALLOW_EXEC=1 to enable `draw`. " +
                  "generate_drawing_code works either way.",
        };
    }

    private async Task<JObject> GenerateAsync(JObject args, CancellationToken ct)
    {
        string request = args["request"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(request)) return Fail("No request supplied.");

        JObject payload;
        try
        {
            payload = await _generator.Value.GenerateAsync(request, args["context"]?.ToString() ?? "", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ex.Message);
        }

        payload["success"] = true;
        payload["executed"] = false;
        return payload;
    }

    private async Task<JObject> DrawAsync(JObject args, CancellationToken ct)
    {
        if (!_config.AllowExec)
        {
            var refusal = Fail("Execution is disabled.");
            refusal["hint"] =
                "Set AUTOCAD_AGENT_ALLOW_EXEC=1 to enable `draw`. It runs generated code " +
                "unsandboxed in this process. generate_drawing_code needs no such permission.";
            return refusal;
        }

        string request = args["request"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(request)) return Fail("No request supplied.");

        if (!await _plugin.IsReachableAsync())
        {
            var unreachable = Fail($"The AutoCAD plugin is not reachable at {_config.Host}:{_config.Port}.");
            unreachable["hint"] = "Start AutoCAD, load the plugin, and run MCPSTART.";
            return unreachable;
        }

        JObject payload;
        try
        {
            payload = await _generator.Value.GenerateAsync(request, args["context"]?.ToString() ?? "", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = Fail(ex.Message);
            failed["executed"] = false;
            return failed;
        }

        double seconds = args["timeout"]?.Value<double>() ?? 120.0;
        JObject outcome = await CodeRunner.RunAsync(
            payload["code"]?.ToString() ?? "", _plugin, (int)(seconds * 1000));

        foreach (var p in outcome.Properties()) payload[p.Name] = p.Value;
        payload["success"] = outcome["success"];
        return payload;
    }

    private static JObject Fail(string error) => new()
    {
        ["success"] = false,
        ["error"] = error,
    };
}
