using System;
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
    /// Execute a raw AutoCAD command string via SendStringToExecute.
    /// Use for commands that have no dedicated MCP tool.
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

            using (LockDoc())
            {
                doc.SendStringToExecute(command + " ", true, false, false);
            }

            return CommandResult.Ok(new JObject
            {
                ["command"] = command,
                ["message"] = "Command sent to AutoCAD"
            });
        }
    }
}
