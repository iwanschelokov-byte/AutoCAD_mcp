using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoCADMCP.Server;

/// <summary>
/// Minimal Model Context Protocol server over stdio.
///
/// Hand-rolled rather than taking an SDK dependency: the surface actually needed
/// is initialize / tools/list / tools/call / ping, and a small stable
/// implementation keeps the published artifact to a single dependency-free exe
/// (the same reasoning documented in the sister Revit MCP).
/// </summary>
public sealed class McpServer
{
    // Protocol revisions this server knows how to speak, newest first.
    private static readonly string[] SupportedProtocols =
    {
        "2025-06-18", "2025-03-26", "2024-11-05"
    };

    private const string DefaultProtocol = "2024-11-05";

    private readonly PluginClient _plugin;
    private readonly JArray _tools;
    private readonly TextWriter _out;
    private readonly TextWriter _log;

    public McpServer(PluginClient plugin, TextWriter output, TextWriter log)
    {
        _plugin = plugin;
        _out = output;
        _log = log;
        _tools = LoadTools();
    }

    public int ToolCount => _tools.Count;

    private JArray LoadTools()
    {
        var tools = LoadToolCatalogue();
        if (tools.Count == 0)
            _log.WriteLine("[warn] tools.json resource not found; no tools will be advertised.");
        return tools;
    }

    /// <summary>
    /// The advertised tool surface, read from the embedded tools.json.
    ///
    /// tools.json is committed source of truth: every entry must resolve either
    /// to a command in the plugin registry or to a <see cref="Tools.ServerTools"/>
    /// entry served in this process. build/verify-assembly.ps1 enforces that via
    /// --list-tools, which is why this is public and static.
    /// </summary>
    public static JArray LoadToolCatalogue()
    {
        var asm = Assembly.GetExecutingAssembly();
        string? resource = asm.GetManifestResourceNames()
                              .FirstOrDefault(n => n.EndsWith("tools.json", StringComparison.OrdinalIgnoreCase));

        if (resource == null) return new JArray();

        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var raw = JArray.Parse(reader.ReadToEnd());

        // Project the generator's records into MCP tool descriptors.
        var tools = new JArray();
        foreach (var t in raw)
        {
            tools.Add(new JObject
            {
                ["name"] = t["name"],
                ["description"] = t["description"],
                ["inputSchema"] = t["inputSchema"]
            });
        }
        return tools;
    }

    /// <summary>Read newline-delimited JSON-RPC from stdin until EOF.</summary>
    public async Task RunAsync(TextReader input, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await input.ReadLineAsync();
            if (line == null) break;              // client closed stdin
            if (string.IsNullOrWhiteSpace(line)) continue;

            JObject request;
            try
            {
                request = JObject.Parse(line);
            }
            catch (JsonReaderException)
            {
                WriteResponse(Error(null, -32700, "Parse error"));
                continue;
            }

            JObject? response = await HandleAsync(request, ct);

            // Notifications (no id) must not be answered.
            if (response != null) WriteResponse(response);
        }
    }

    private async Task<JObject?> HandleAsync(JObject request, CancellationToken ct)
    {
        JToken? id = request["id"];
        string method = request["method"]?.ToString() ?? "";
        bool isNotification = id == null || id.Type == JTokenType.Null;

        switch (method)
        {
            case "initialize":
            {
                string requested = request["params"]?["protocolVersion"]?.ToString() ?? DefaultProtocol;
                string agreed = SupportedProtocols.Contains(requested) ? requested : DefaultProtocol;

                return Result(id, new JObject
                {
                    ["protocolVersion"] = agreed,
                    ["capabilities"] = new JObject
                    {
                        ["tools"] = new JObject { ["listChanged"] = false }
                    },
                    ["serverInfo"] = new JObject
                    {
                        ["name"] = "autocad-mcp",
                        ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "2.0.1"
                    }
                });
            }

            case "notifications/initialized":
            case "initialized":
                return null;                       // notification: no reply

            case "ping":
                return Result(id, new JObject());

            case "tools/list":
                return Result(id, new JObject { ["tools"] = _tools });

            case "tools/call":
                return await CallToolAsync(id, request["params"] as JObject, ct);

            default:
                if (isNotification) return null;
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private async Task<JObject> CallToolAsync(JToken? id, JObject? parameters, CancellationToken ct)
    {
        string? name = parameters?["name"]?.ToString();
        if (string.IsNullOrEmpty(name))
            return Error(id, -32602, "tools/call requires a 'name'");

        var known = _tools.Any(t => string.Equals(t["name"]?.ToString(), name, StringComparison.Ordinal));
        if (!known)
            return Error(id, -32602, $"Unknown tool: {name}");

        var arguments = parameters?["arguments"] as JObject ?? new JObject();

        // A few tools are implemented here rather than in the plugin, because they
        // need work the plugin deliberately avoids (reading a spreadsheet,
        // rewriting a PDF). They may still call the plugin themselves.
        if (Tools.ServerTools.All.TryGetValue(name, out var local))
        {
            try
            {
                JObject localResult = await local.ExecuteAsync(arguments, _plugin, ct);
                bool failed = localResult["success"]?.Type == JTokenType.Boolean
                              && !localResult["success"]!.Value<bool>();

                return Result(id, new JObject
                {
                    ["content"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = localResult.ToString(Formatting.Indented),
                        },
                    },
                    ["isError"] = failed,
                });
            }
            catch (PluginUnavailableException ex)
            {
                return Result(id, ToolError(ex.Message));
            }
            catch (OperationCanceledException)
            {
                return Result(id, ToolError($"'{name}' was cancelled."));
            }
            catch (Exception ex)
            {
                return Result(id, ToolError($"'{name}' failed: {ex.GetType().Name}: {ex.Message}"));
            }
        }

        JObject pluginResponse;
        try
        {
            pluginResponse = await _plugin.CallAsync(name, arguments, ct);
        }
        catch (PluginUnavailableException ex)
        {
            // Surface transport problems as tool errors, not protocol errors:
            // the conversation should continue and the model should see why.
            return Result(id, ToolError(ex.Message));
        }
        catch (Exception ex)
        {
            return Result(id, ToolError($"Unexpected failure calling '{name}': {ex.Message}"));
        }

        if (pluginResponse["error"] is JObject err)
        {
            string message = err["message"]?.ToString() ?? "Unknown plugin error";
            string? code = err["data"]?["errorCode"]?.ToString();
            string text = code == null ? message : $"[{code}] {message}";
            return Result(id, ToolError(text));
        }

        JToken result = pluginResponse["result"] ?? JValue.CreateNull();
        return Result(id, new JObject
        {
            ["content"] = new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = result.Type == JTokenType.String
                        ? result.ToString()
                        : result.ToString(Formatting.Indented)
                }
            },
            ["isError"] = false
        });
    }

    private static JObject ToolError(string message) => new()
    {
        ["content"] = new JArray
        {
            new JObject { ["type"] = "text", ["text"] = message }
        },
        ["isError"] = true
    };

    private static JObject Result(JToken? id, JObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id ?? JValue.CreateNull(),
        ["result"] = result
    };

    private static JObject Error(JToken? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id ?? JValue.CreateNull(),
        ["error"] = new JObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };

    private void WriteResponse(JObject response)
    {
        // stdout carries protocol traffic only; anything else corrupts the stream.
        _out.WriteLine(response.ToString(Formatting.None));
        _out.Flush();
    }
}
