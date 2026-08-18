using System;
using System.Collections.Generic;
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
    public class SetSystemVariableCommand : AcadCommand
    {
        public override string MethodName => "set_system_variable";

        public override CommandResult Execute(JObject parameters)
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
    public class GetSystemVariableCommand : AcadCommand
    {
        public override string MethodName => "get_system_variable";

        public override CommandResult Execute(JObject parameters)
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
    /// Commands that open a modal dialog, and what to use instead.
    ///
    /// A modal dialog is not merely inconvenient here - it runs its own message
    /// loop, so AutoCAD's Application.Idle stops firing and the whole MCP bridge
    /// goes silent until a human closes the dialog. FILEDIA 0 and CMDDIA 0 do
    /// not prevent this: they only suppress *file* dialogs for the commands that
    /// honour them, and EXPORTPDF is not one of them.
    ///
    /// Blocking these at the door is the only protection that works, because
    /// once the dialog is up the plugin can no longer act. Pass force=true to
    /// send one anyway.
    /// </summary>
    internal static class DialogCommands
    {
        private static readonly Dictionary<string, string> _advice =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "EXPORTPDF",  "opens the Save As dialog even with FILEDIA 0 - use the plot_to_pdf tool" },
            { "PLOT",       "opens the Plot dialog - use the plot_to_pdf tool" },
            { "-PLOT",      "works without a dialog, but explodes every TrueType glyph into line " +
                            "segments: the PDF ends up with outlined, unselectable text and ~25x the " +
                            "file size. Use the plot_to_pdf tool, which keeps the font embedded" },
            { "PUBLISH",    "opens the Publish dialog - use the plot_to_pdf tool per layout" },
            { "PAGESETUP",  "opens the Page Setup dialog - pass paper/scale/orientation to plot_to_pdf instead" },
            { "PLOTTERMANAGER", "opens Windows Explorer on the plotter folder - use the plot_devices tool" },
            { "STYLESMANAGER",  "opens Windows Explorer on the plot-style folder - use the plot_devices tool" },
            { "OPTIONS",    "opens the Options dialog - use set_system_variable" },
            { "UNITS",      "opens the Drawing Units dialog - use the set_units tool (or -UNITS)" },
            { "PURGE",      "opens the Purge dialog - use the purge_drawing tool (or -PURGE)" },
            { "INSERT",     "opens the Insert dialog - use the insert_block tool (or -INSERT)" },
            { "CLASSICINSERT", "opens the Insert dialog - use the insert_block tool" },
            { "STYLE",      "opens the Text Style dialog - use the create_text_style tool (or -STYLE)" },
            { "DIMSTYLE",   "opens the Dimension Style Manager - use the create_dimension_style tool (or -DIMSTYLE)" },
            { "MLEADERSTYLE", "opens the Multileader Style Manager - no dialog-free equivalent; set the DIM* variables" },
            { "TABLESTYLE", "opens the Table Style dialog - no dialog-free equivalent" },
            { "LAYERSTATE", "opens the Layer States Manager - use -LAYERSTATE" },
            { "DWGPROPS",   "opens the Drawing Properties dialog" },
            { "FIND",       "opens the Find and Replace dialog - use the search_text tool" },
            { "QUICKCALC",  "opens the QuickCalc palette dialog" },
            { "OPEN",       "opens the Select File dialog - use the drawing_open tool" },
            { "NEW",        "opens the Select Template dialog - use the drawing_new tool" },
            { "QNEW",       "may open the Select Template dialog - use the drawing_new tool" },
            { "SAVEAS",     "opens the Save As dialog - use drawing_save with a 'path' (it suppresses FILEDIA itself)" },
            { "RECOVER",    "opens the Select File dialog" },
            { "SCRIPT",     "opens the Select Script File dialog - use -SCRIPT" },
            { "APPLOAD",    "opens the Load Application dialog" },
            { "IMAGEATTACH", "opens the Select Image dialog - use -IMAGEATTACH" },
            { "PDFATTACH",  "opens the Select PDF dialog - use -PDFATTACH" },
            { "XATTACH",    "opens the Select Reference dialog - use -XATTACH" },
            { "ATTACH",     "opens the Select Reference dialog - use -ATTACH" },
            { "ETRANSMIT",  "opens the eTransmit dialog" },
            { "EXPORT",     "opens the Export Data dialog - use plot_to_pdf for PDF output" },
            { "IMPORT",     "opens the Import File dialog" },
            { "CUI",        "opens the Customize User Interface dialog" },
            { "OSNAP",      "opens the Drafting Settings dialog - use set_system_variable OSMODE" },
            { "DSETTINGS",  "opens the Drafting Settings dialog - use set_system_variable" },
            { "UCSMAN",     "opens the UCS dialog - use -UCS" },
            { "VIEW",       "opens the View Manager dialog - use -VIEW" },
            { "SHEETSET",   "opens the Sheet Set Manager palette" },
            { "3DCONFIG",   "opens the Graphics Performance dialog" },
            { "GRAPHICSCONFIG", "opens the Graphics Performance dialog" },
        };

        /// <summary>
        /// The bare command name: leading _ . ' modifiers removed, everything
        /// from the first space onward dropped. A leading '-' is KEPT, because
        /// the hyphenated form is usually the dialog-free one and must stay
        /// allowed - the single exception, -PLOT, is listed explicitly.
        /// </summary>
        public static string Normalize(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return "";
            string first = command.Trim().Split(new[] { ' ', '\t', '\n', '\r' }, 2)[0];
            return first.TrimStart('_', '.', '\'');
        }

        public static bool TryGetAdvice(string command, out string name, out string advice)
        {
            name = Normalize(command);
            return _advice.TryGetValue(name, out advice);
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
    public class ExecuteCommandCommand : AcadCommand
    {
        public override string MethodName => "execute_command";

        public override CommandResult Execute(JObject parameters)
        {
            string command = parameters["command"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(command))
                return CommandResult.Fail("Parameter 'command' is required and cannot be empty/whitespace");

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return CommandResult.Fail("No active document");

            bool force = parameters["force"]?.Value<bool>() ?? false;

            // (1) Refuse commands that open a modal dialog. Once one is up, the
            //     plugin cannot act at all - this check is the only moment at
            //     which the bridge can still protect itself.
            string blockedName, advice;
            if (!force && DialogCommands.TryGetAdvice(command, out blockedName, out advice))
            {
                return CommandResult.Fail(
                    $"Refused '{blockedName}': it {advice}. Most commands on this list open a modal " +
                    "dialog, and a modal dialog stops AutoCAD's idle loop, which silences this bridge " +
                    "until a human closes the dialog by hand. " +
                    "Pass force=true if you accept that risk and are sitting at the machine.");
            }

            // (2) Refuse to send into a command that is already waiting for
            //     input - the new string would be eaten as a prompt response.
            int flags = CommandState.ActiveFlags();
            if (!force && flags != 0)
            {
                string active = CommandState.Names();
                return CommandResult.Fail(
                    $"The command line is busy: '{active}' is active (CMDACTIVE={flags}). " +
                    "Anything sent now would be consumed as a response to its prompt, not run as a " +
                    "command. Call cancel_command to abort it, then retry. Pass force=true to send anyway.");
            }

            // (3) Prefix the command name with '_' so it resolves by its English
            //     name on a localised AutoCAD. Only '_' is added, never '_.',
            //     because the '.' modifier forces the built-in definition and
            //     would break commands defined by LISP or ObjectARX.
            bool autoPrefix = parameters["auto_prefix"]?.Value<bool>() ?? true;
            if (autoPrefix) command = AddUnderscore(command);

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
                ["forced"] = force,
                ["message"] = "Command sent to AutoCAD. It runs asynchronously — " +
                              "call read_command_line with this 'since' value to see what happened."
            });
        }

        /// <summary>
        /// Add the language-independence prefix to the command name if it does
        /// not already carry a modifier. "CIRCLE" -> "_CIRCLE";
        /// "-PURGE" -> "_-PURGE"; "_.CIRCLE" and "(command ...)" are left alone.
        /// </summary>
        private static string AddUnderscore(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return command;
            string trimmed = command.TrimStart();
            char c = trimmed[0];
            if (c == '_' || c == '.' || c == '\'' || c == '(' || c == '*') return command;
            if (char.IsLetter(c) || c == '-') return "_" + trimmed;
            return command;
        }
    }
}
