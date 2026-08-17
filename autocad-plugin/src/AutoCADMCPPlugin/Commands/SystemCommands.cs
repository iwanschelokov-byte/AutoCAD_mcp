using System.Linq;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using AutoCADMCPPlugin.Models;
using AutoCADMCPPlugin.Core;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    public class SystemStatusCommand : AcadCommand
    {
        public override string MethodName => "system_status";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            var data = new JObject
            {
                ["plugin"] = Plugin.PluginName,
                ["version"] = Plugin.Version,
                ["build"] = Plugin.Build,
                ["autocad_running"] = true,
                ["document_open"] = doc != null,
                ["document_name"] = doc?.Name ?? "none",
                ["target_framework"] = AcadCompat.TargetFramework
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

    /// <summary>
    /// Runs directly on the socket thread so tool discovery never blocks on
    /// AutoCAD being busy or showing a modal dialog.
    /// </summary>
    public class ListMethodsCommand : DirectCommand
    {
        public override string MethodName => "list_methods";

        public override CommandResult Execute(JObject parameters)
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

    /// <summary>
    /// Full introspection: what this build is, what it supports, and the current
    /// safety posture. Direct — answers even while AutoCAD is modal.
    /// </summary>
    public class GetCapabilitiesCommand : DirectCommand
    {
        public override string MethodName => "get_capabilities";

        public override CommandResult Execute(JObject parameters)
        {
            var methods = CommandRegistry.GetAllMethods().OrderBy(m => m).ToList();

            int writeCount = 0;
            int destructiveCount = 0;
            var destructive = new JArray();
            foreach (var m in methods)
            {
                var cmd = CommandRegistry.GetCommand(m);
                if (cmd == null) continue;
                if (cmd.IsWrite) writeCount++;
                if (cmd.IsDestructive) { destructiveCount++; destructive.Add(m); }
            }

            var data = new JObject
            {
                ["plugin"] = Plugin.PluginName,
                ["version"] = Plugin.Version,
                ["target_framework"] = AcadCompat.TargetFramework,
                ["supports"] = AcadCompat.SupportedAutoCadRange,
                ["tool_count"] = methods.Count,
                ["write_tool_count"] = writeCount,
                ["destructive_tool_count"] = destructiveCount,
                ["destructive_tools"] = destructive,
                ["options"] = Settings.ToJson()
            };
            return CommandResult.Ok(data);
        }
    }

    /// <summary>Read the current safety posture.</summary>
    public class GetServerOptionsCommand : DirectCommand
    {
        public override string MethodName => "get_server_options";

        public override CommandResult Execute(JObject parameters)
        {
            return CommandResult.Ok(Settings.ToJson());
        }
    }

    /// <summary>
    /// Change and persist the safety posture (read-only mode, destructive
    /// confirmation, audit logging).
    /// </summary>
    public class SetServerOptionsCommand : DirectCommand
    {
        public override string MethodName => "set_server_options";

        public override CommandResult Execute(JObject parameters)
        {
            Settings.EnsureLoaded();

            bool changed = false;

            var ro = parameters["read_only"];
            if (ro != null && ro.Type != JTokenType.Null)
            {
                Settings.ReadOnly = ro.Value<bool>();
                changed = true;
            }

            var cd = parameters["confirm_destructive"];
            if (cd != null && cd.Type != JTokenType.Null)
            {
                Settings.ConfirmDestructive = cd.Value<bool>();
                changed = true;
            }

            var al = parameters["audit_log"];
            if (al != null && al.Type != JTokenType.Null)
            {
                Settings.AuditLog = al.Value<bool>();
                changed = true;
            }

            if (!changed)
            {
                return CommandResult.BadParam(
                    "Provide at least one of: read_only, confirm_destructive, audit_log (booleans).");
            }

            Settings.Save();

            var data = Settings.ToJson();
            data["message"] = "Server options updated and saved.";
            return CommandResult.Ok(data);
        }
    }
}
