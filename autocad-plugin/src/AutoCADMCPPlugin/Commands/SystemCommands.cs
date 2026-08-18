using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using AutoCADMCPPlugin.Models;
using AutoCADMCPPlugin.Core;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using static AutoCADMCPPlugin.Commands.EntityHelper;

namespace AutoCADMCPPlugin.Commands
{
    /// <summary>
    /// Reads AutoCAD's CMDACTIVE / CMDNAMES pair and turns it into something a
    /// caller can act on.
    ///
    /// This matters because every MCP call is marshalled onto AutoCAD's main
    /// thread through Application.Idle. Idle still fires while a command sits at
    /// a prompt waiting for input, so the bridge answers - but any command the
    /// caller sends lands *inside* the waiting command and is eaten as a prompt
    /// response. A modal dialog is worse: it runs its own message loop, Idle
    /// never fires, and every call times out until a human closes the dialog.
    /// Reporting the state is what turns both cases from "the bridge went
    /// silent" into a diagnosis.
    /// </summary>
    public static class CommandState
    {
        // CMDACTIVE bit flags, per the AutoCAD system-variable reference.
        public const int Ordinary = 1;
        public const int Transparent = 2;
        public const int Script = 4;
        public const int Dialog = 8;
        public const int Dde = 16;
        public const int Lisp = 32;
        public const int Arx = 64;

        public static int ActiveFlags()
        {
            try
            {
                object v = Application.GetSystemVariable("CMDACTIVE");
                return v == null ? 0 : Convert.ToInt32(v);
            }
            catch { return 0; }
        }

        public static string Names()
        {
            try { return Application.GetSystemVariable("CMDNAMES") as string ?? ""; }
            catch { return ""; }
        }

        /// <summary>One-word summary: idle | command | dialog | script.</summary>
        public static string Summary(int flags)
        {
            if (flags == 0) return "idle";
            if ((flags & Dialog) != 0) return "dialog";
            if ((flags & Script) != 0) return "script";
            return "command";
        }

        /// <summary>Fill a JObject with the current command-line state.</summary>
        public static void Describe(JObject target)
        {
            int flags = ActiveFlags();
            string names = Names();

            target["command_active"] = flags != 0;
            target["command_active_flags"] = flags;
            target["command_names"] = names;
            target["command_state"] = Summary(flags);
            target["dialog_active"] = (flags & Dialog) != 0;
            target["script_active"] = (flags & Script) != 0;

            if (flags != 0)
            {
                target["command_hint"] = (flags & Dialog) != 0
                    ? "A modal dialog is open. The bridge cannot close it - a human has to. " +
                      "Avoid commands that open dialogs; use the MCP tool instead (for example " +
                      "plot_to_pdf rather than EXPORTPDF)."
                    : $"Command '{names}' is waiting for input. Anything sent now is consumed as a " +
                      "prompt response. Call cancel_command first, then retry.";
            }
        }
    }

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

            CommandState.Describe(data);

            JObject last = CommandTracker.LastEntry();
            if (last != null) data["last_command"] = last;

            return CommandResult.Ok(data);
        }
    }

    /// <summary>
    /// Send ESC ESC to the active document to abort a command that is sitting at
    /// a prompt.
    ///
    /// Limits worth knowing before relying on this:
    ///  * It cannot close a modal dialog. When CMDACTIVE reports flag 8 the
    ///    plugin's own thread never runs, so this command would not even be
    ///    reached; if it is reached, it refuses and says so.
    ///  * Like every other command dispatch, the cancel is queued
    ///    (SendStringToExecute is fire-and-forget), so the state in this reply is
    ///    the state *before* the cancel. Re-read system_status to confirm.
    ///  * A few commands - MATCHPROP is the usual offender - re-arm themselves
    ///    after a single ESC. Pass repeat=2 or 3 for those.
    /// </summary>
    public class CancelCommandCommand : AcadCommand
    {
        public override string MethodName => "cancel_command";

        // Sending ESC changes nothing in the drawing, and read-only mode is
        // exactly when a caller still needs to unstick a prompt: classified as a
        // write it would be refused by the safety gate and the bridge would stay
        // wedged with no way out.
        public override bool IsWrite => false;

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            int flags = CommandState.ActiveFlags();
            string names = CommandState.Names();

            if ((flags & CommandState.Dialog) != 0)
                return CommandResult.Fail(
                    "A modal dialog is open. ESC cannot reach it from the plugin - close the dialog " +
                    "in AutoCAD by hand. (CMDACTIVE=" + flags + ", CMDNAMES='" + names + "')");

            if (flags == 0)
            {
                var idle = new JObject
                {
                    ["cancelled"] = false,
                    ["was_active"] = false,
                    ["message"] = "No command was active; nothing to cancel."
                };
                CommandState.Describe(idle);
                return CommandResult.Ok(idle);
            }

            int repeat = parameters?["repeat"]?.Value<int>() ?? 1;
            if (repeat < 1) repeat = 1;
            if (repeat > 5) repeat = 5;

            long since = CommandTracker.Record(names, "cancel_requested", doc.Name, "ESC x" + (repeat * 2));

            using (LockDoc())
            {
                for (int i = 0; i < repeat; i++)
                    doc.SendStringToExecute("\x03\x03", true, false, false);
            }

            var data = new JObject
            {
                ["cancelled"] = true,
                ["was_active"] = true,
                ["cancelled_command"] = names,
                ["repeat"] = repeat,
                ["since"] = since,
                ["message"] = $"ESC sent {repeat}x to abort '{names}'. The cancel is queued - " +
                              "call system_status again to confirm the command line is idle."
            };
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
