using System.Text;
using AutoCADMCP.Server;
using Newtonsoft.Json.Linq;

namespace AutoCADMCP.Agent;

/// <summary>
/// System prompt and tool catalogue for the AutoCode Agent.
///
/// The catalogue comes from the same tools.json the MCP server embeds, so the
/// agent and the server describe one tool surface — there is no second
/// hand-maintained copy to drift.
/// </summary>
public static class Prompts
{
    private static string? _cached;

    public static JArray LoadTools() => McpServer.LoadToolCatalogue();

    /// <summary>
    /// Render the catalogue compactly enough to sit in a cached system prompt.
    /// </summary>
    public static string FormatCatalogue(JArray tools, bool includeSchemas = true)
    {
        if (tools.Count == 0)
            return "(tool catalogue unavailable — call list_methods at runtime to discover tools)";

        var sb = new StringBuilder();
        foreach (var t in tools)
        {
            string name = t["name"]?.ToString() ?? "";
            string summary = FirstLine(t["description"]?.ToString());

            if (!includeSchemas)
            {
                sb.Append("- ").Append(name).Append(": ").Append(summary).Append('\n');
                continue;
            }

            var schema = t["inputSchema"] as JObject;
            var props = schema?["properties"] as JObject;
            var required = new HashSet<string>(
                (schema?["required"] as JArray)?.Select(r => r.ToString()) ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);

            var parameters = new List<string>();
            foreach (var p in props?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                var ps = p.Value as JObject;
                string type = ps?["type"]?.ToString() ?? "any";
                if (type == "array")
                    type = ((ps?["items"] as JObject)?["type"]?.ToString() ?? "any") + "[]";

                parameters.Add(p.Name + ": " + type + (required.Contains(p.Name) ? "" : "?"));
            }

            sb.Append("- ").Append(name).Append('(').Append(string.Join(", ", parameters)).Append(")\n")
              .Append("    ").Append(summary).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        int nl = text.IndexOf('\n');
        return (nl < 0 ? text : text[..nl]).Trim();
    }

    // The traps below are not guesses — each one is a real behaviour of this
    // plugin that has bitten a caller, and each is cheap to state and expensive
    // to discover.
    private const string Traps = """
        Known behaviours that will trip you up if you do not account for them:

        - Handles: every create_* tool returns {"id": "<handle>"}. Pass that handle back
          as `id` (or `handle` for the older modify tools). Handles are decimal here;
          AutoCAD's own UI shows hexadecimal — the plugin accepts both.
        - Parameter aliases: move_entity/copy_entity accept `from`/`to` AND
          `from_point`/`to_point`; zoom_window and select_by_window accept `min`/`max`
          AND `min_point`/`max_point`. Either spelling works.
        - Destructive tools require confirmation: erase_entity, bulk_erase, delete_layer,
          delete_layout, delete_layer_state, delete_block_definition, purge_drawing,
          detach_xref, overkill and ungroup all refuse unless you pass
          {"__confirm": true}. They return errorCode "NeedsConfirm" otherwise.
        - Read-only mode: if the server is read-only, every drawing-modifying tool
          returns errorCode "ReadOnly". Do not try to work around it — report it.
        - execute_command is asynchronous. It queues a command and returns immediately;
          use read_command_line afterwards to see what actually happened.
        - plot_to_pdf waits for the file; it is the tool to use for plotting. Do not use
          it to "check" whether plotting works — it is slow.
        - measure_between returns center_distance: null (with a center_distance_note)
          when an entity has no geometric extents. Check for null before doing maths.
        - Layers must exist before you draw on them. create_layer first, then pass
          `layer` to the create_* call — passing an unknown layer name silently draws on
          the current layer instead.
        - Points are [x, y] or [x, y, z]. Angles are degrees unless a tool says otherwise.
        """;

    private const string Contract = """
        You are writing C# that runs inside a host process which has already connected
        to AutoCAD. One method is pre-bound for you:

            JObject Call(string method, object? parameters = null)

        `parameters` is anything Newtonsoft can serialise into an object — an anonymous
        type is the natural choice: Call("create_line", new { start = new[]{0,0}, end = new[]{100,0} }).
        It returns the decoded result and throws PluginException if the plugin reports
        an error, so you do not need to check a status field — let it throw, or catch it
        if you intend to recover.

        The script runs as a sequence of statements, not inside a method body. These are
        already imported: System, System.Collections.Generic, System.Linq, System.Text,
        Newtonsoft.Json, Newtonsoft.Json.Linq, and `using static System.Math` (so Sin,
        Cos, PI, Sqrt are unqualified). You may declare local functions and use loops
        and LINQ freely.

        Assign your final answer to the pre-declared variable `Result` if the task asks
        a question; otherwise leave it alone. Anything you Console.WriteLine is captured.

        Reading a handle out of a result: Call(...)["id"]!.ToString().
        """;

    /// <summary>
    /// Assemble the full system prompt. Stable across calls, so it caches well.
    /// </summary>
    public static string BuildSystemPrompt(bool includeSchemas = true)
    {
        if (_cached != null && includeSchemas) return _cached;

        var tools = LoadTools();
        string catalogue = FormatCatalogue(tools, includeSchemas);
        string count = tools.Count > 0 ? tools.Count.ToString() : "an unknown number of";

        string prompt = $"""
            You write C# that draws in AutoCAD through a plugin's JSON-RPC interface.

            {Contract}

            # Available tools ({count} total)

            {catalogue}

            # {Traps}

            # How to approach a request

            Work out the geometry before you write the code. Compute coordinates rather than
            hard-coding a grid of magic numbers, and name the dimensions you derive so the
            code reads as the drawing it produces.

            Prefer bulk_create for many similar entities — it is one round trip instead of
            hundreds. Prefer measure_text/measure_texts over estimating text width; SHX
            glyphs are proportional and estimates overlap.

            If the request is ambiguous in a way that changes the drawing, pick the reading a
            draughtsman would and say which you picked in your plan. Do not ask a question
            back — you are producing code, not holding a conversation.

            If the request cannot be done with the available tools, say so in your plan and
            return the closest thing that can be done, rather than inventing a tool name.
            Every method you call must appear in the catalogue above.
            """;

        if (includeSchemas) _cached = prompt;
        return prompt;
    }

    /// <summary>
    /// Structured-output schema. The API validates the response against this, so
    /// the host never has to parse a fenced code block out of prose.
    /// </summary>
    public static JObject ResponseSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["plan"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Two or three sentences: the geometry, and any ambiguity resolved.",
            },
            ["code"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "The C# to run. No markdown fences, no commentary.",
            },
            ["tools_used"] = new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "string" },
                ["description"] = "Plugin method names the code calls.",
            },
        },
        ["required"] = new JArray("plan", "code", "tools_used"),
        ["additionalProperties"] = false,
    };
}
