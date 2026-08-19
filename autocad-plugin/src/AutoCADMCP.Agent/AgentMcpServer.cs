using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoCADMCP.Agent;

/// <summary>
/// Newline-delimited JSON-RPC over stdio, speaking MCP.
///
/// Deliberately its own small loop rather than a reuse of the server's: this
/// serves three locally-implemented tools and proxies nothing, so sharing the
/// server's dispatch would mean parameterising it for one caller's benefit.
/// </summary>
public sealed class AgentMcpServer
{
    private const string ProtocolVersion = "2025-06-18";

    private static readonly string[] SupportedProtocols =
        ["2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly AgentTools _tools;
    private readonly TextWriter _out;
    private readonly TextWriter _log;

    public AgentMcpServer(AgentTools tools, TextWriter output, TextWriter log)
    {
        _tools = tools;
        _out = output;
        _log = log;
    }

    public async Task RunAsync(TextReader input, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await input.ReadLineAsync();
            if (line == null) return;            // client closed stdin
            if (line.Length == 0) continue;

            JObject? response;
            try
            {
                response = await HandleAsync(JObject.Parse(line), ct);
            }
            catch (JsonReaderException)
            {
                response = Error(null, -32700, "Parse error");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.WriteLine($"[error] {ex}");
                response = Error(null, -32603, $"Internal error: {ex.Message}");
            }

            // Notifications get no reply at all - sending one is a protocol error.
            if (response == null) continue;

            await _out.WriteLineAsync(response.ToString(Formatting.None));
            await _out.FlushAsync();
        }
    }

    private async Task<JObject?> HandleAsync(JObject request, CancellationToken ct)
    {
        string method = request["method"]?.ToString() ?? "";
        JToken? id = request["id"];
        var parameters = request["params"] as JObject ?? new JObject();

        if (id == null || id.Type == JTokenType.Null)
        {
            // A notification. "notifications/initialized" is the expected one.
            return null;
        }

        switch (method)
        {
            case "initialize":
            {
                string asked = parameters["protocolVersion"]?.ToString() ?? ProtocolVersion;
                string agreed = SupportedProtocols.Contains(asked) ? asked : ProtocolVersion;

                return Result(id, new JObject
                {
                    ["protocolVersion"] = agreed,
                    ["capabilities"] = new JObject { ["tools"] = new JObject() },
                    ["serverInfo"] = new JObject
                    {
                        ["name"] = "autocad-autocode-agent",
                        ["version"] = "2.0.1",
                    },
                });
            }

            case "tools/list":
                return Result(id, new JObject { ["tools"] = AgentTools.Descriptors() });

            case "tools/call":
            {
                string name = parameters["name"]?.ToString() ?? "";
                var arguments = parameters["arguments"] as JObject ?? new JObject();

                JObject outcome = await _tools.CallAsync(name, arguments, ct);
                bool failed = outcome["success"]?.Type == JTokenType.Boolean &&
                              !outcome["success"]!.Value<bool>();

                return Result(id, new JObject
                {
                    ["content"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = outcome.ToString(Formatting.Indented),
                        },
                    },
                    ["isError"] = failed,
                });
            }

            case "ping":
                return Result(id, new JObject());

            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static JObject Result(JToken? id, JObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JObject Error(JToken? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JObject { ["code"] = code, ["message"] = message },
    };
}
