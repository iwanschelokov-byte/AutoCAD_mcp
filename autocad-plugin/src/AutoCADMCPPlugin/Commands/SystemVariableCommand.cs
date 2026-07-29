using System;
using System.Text;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using static AutoCADMCPPlugin.Commands.EntityHelper;

namespace AutoCADMCPPlugin.Commands
{
    /// <summary>
    /// Set an AutoCAD system variable (e.g., DIMTXT, DIMASZ, LTSCALE).
    /// </summary>
    public class SetSystemVariableCommand : ICommand
    {
        public string MethodName => "set_system_variable";

        public CommandResult Execute(JObject parameters)
        {
            string name = parameters["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                return CommandResult.Fail("Parameter 'name' is required");

            JToken valueToken = parameters["value"];
            if (valueToken == null)
                return CommandResult.Fail("Parameter 'value' is required");

            try
            {
                using (LockDoc())
                {
                    object value;
                    // Determine type from JSON token
                    switch (valueToken.Type)
                    {
                        case JTokenType.Integer:
                            value = valueToken.Value<int>();
                            break;
                        case JTokenType.Float:
                            value = valueToken.Value<double>();
                            break;
                        case JTokenType.String:
                            value = valueToken.Value<string>();
                            break;
                        case JTokenType.Boolean:
                            value = valueToken.Value<bool>() ? 1 : 0;
                            break;
                        default:
                            value = valueToken.ToString();
                            break;
                    }

                    Application.SetSystemVariable(name.ToUpper(), value);

                    return CommandResult.Ok(new JObject
                    {
                        ["variable"] = name.ToUpper(),
                        ["value"] = JToken.FromObject(value),
                        ["message"] = $"System variable {name.ToUpper()} set"
                    });
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to set {name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Get an AutoCAD system variable value.
    /// </summary>
    public class GetSystemVariableCommand : ICommand
    {
        public string MethodName => "get_system_variable";

        public CommandResult Execute(JObject parameters)
        {
            string name = parameters["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                return CommandResult.Fail("Parameter 'name' is required");

            try
            {
                object value = Application.GetSystemVariable(name.ToUpper());
                return CommandResult.Ok(new JObject
                {
                    ["variable"] = name.ToUpper(),
                    ["value"] = value != null ? JToken.FromObject(value) : JValue.CreateNull()
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to get {name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Execute an AutoCAD command, optionally with all of its interactive inputs.
    ///
    /// The "command" string and any items in the optional "inputs" array are
    /// joined into ONE space-separated string and sent via
    /// Document.SendStringToExecute. Passing the whole command plus every prompt
    /// response in a single call is what makes interactive / multi-step commands
    /// reliable.
    ///
    /// Splitting an interactive command across several execute_command calls is
    /// NOT supported: each call is queued independently, and if the active
    /// document changes in between, AutoCAD injects two cancels ("\x03\x03",
    /// seen as ESC ESC in the command-line echo) that abort a command still
    /// waiting for input.
    ///
    /// SendStringToExecute is asynchronous (fire-and-forget): it returns once the
    /// string is queued, before the command finishes. It is the supported way to
    /// drive commands from a modeless / Application.Idle context — the
    /// synchronous Editor.Command() throws eInvalidInput when called from there.
    ///
    /// Example (draw a circle): { "command": "_.CIRCLE", "inputs": ["100,100", "40"] }
    /// or simply { "command": "_.CIRCLE 100,100 40" }.
    /// </summary>
    public class ExecuteCommandCommand : ICommand
    {
        public string MethodName => "execute_command";

        public CommandResult Execute(JObject parameters)
        {
            string command = parameters["command"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(command))
                return CommandResult.Fail("Parameter 'command' is required and cannot be empty/whitespace");

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return CommandResult.Fail("No active document");

            // Append optional prompt responses so the entire interactive command
            // is sent as one queued string.
            var sb = new StringBuilder(command);
            if (parameters["inputs"] is JArray inputs)
            {
                foreach (JToken item in inputs)
                {
                    string s = item?.ToString();
                    if (!string.IsNullOrEmpty(s))
                        sb.Append(' ').Append(s);
                }
            }

            // Trailing space is the final <Enter> that submits the command.
            string full = sb.ToString().TrimEnd() + " ";

            // Mark the position in the command log *before* queueing, so that
            // read_command_line(since) returns exactly what this call caused.
            long since = Core.CommandTracker.CurrentSeq;

            using (LockDoc())
            {
                doc.SendStringToExecute(full, true, false, false);
            }

            return CommandResult.Ok(new JObject
            {
                ["command"] = command,
                ["sent"] = full,
                ["since"] = since,
                ["message"] = "Command sent to AutoCAD. It runs asynchronously — " +
                              "call read_command_line with this 'since' value to see what happened."
            });
        }
    }
}
