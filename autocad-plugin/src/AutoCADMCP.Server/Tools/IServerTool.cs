using Newtonsoft.Json.Linq;

namespace AutoCADMCP.Server.Tools;

/// <summary>
/// A tool the server implements itself rather than proxying to the plugin.
///
/// Most tools are a straight pass-through: the plugin does the work and the
/// server forwards the JSON. A few need work the plugin deliberately cannot do —
/// reading a spreadsheet, rewriting a PDF — because the plugin is loaded into
/// acad.exe and every extra assembly there risks shadowing one AutoCAD already
/// loaded. Those live here, in a separate process where a dependency is free.
///
/// A server tool may still call the plugin; several are wrappers that add a
/// post-processing step around a plugin command.
/// </summary>
public interface IServerTool
{
    /// <summary>Tool name, matching the entry in tools.json.</summary>
    string Name { get; }

    Task<JObject> ExecuteAsync(JObject arguments, PluginClient plugin, CancellationToken ct);
}

public static class ServerTools
{
    /// <summary>Every locally-implemented tool, keyed by name.</summary>
    public static IReadOnlyDictionary<string, IServerTool> All { get; } =
        new IServerTool[]
        {
            new ExcelTableTool(),
            new PlotToPdfTool(),
        }.ToDictionary(t => t.Name, StringComparer.Ordinal);
}
