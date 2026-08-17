using System.Linq;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using AutoCADMCPPlugin.Models;
using AutoCADMCPPlugin.Core;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    public class SystemStatusCommand : ICommand
    {
        public string MethodName => "system_status";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            var data = new JObject
            {
                ["plugin"] = Plugin.PluginName,
                ["version"] = Plugin.Version,
                ["build"] = Plugin.Build,
                ["autocad_running"] = true,
                ["document_open"] = doc != null,
                ["document_name"] = doc?.Name ?? "none"
            };

            try
            {
                var acadApp = Application.Version;
                data["autocad_version"] = acadApp.ToString();
            }
            catch { }

            return CommandResult.Ok(data);
        }
    }

    public class ListMethodsCommand : ICommand
    {
        public string MethodName => "list_methods";

        public CommandResult Execute(JObject parameters)
        {
            var methods = CommandRegistry.GetAllMethods().OrderBy(m => m).ToList();
            var data = new JObject
            {
                ["methods"] = new JArray(methods),
                ["count"] = methods.Count
            };
            return CommandResult.Ok(data);
        }
    }
}
