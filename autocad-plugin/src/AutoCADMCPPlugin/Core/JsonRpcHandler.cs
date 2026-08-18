using System;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AutoCADMCPPlugin.Models;
using Autodesk.AutoCAD.ApplicationServices;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Core
{
    /// <summary>
    /// Handles JSON-RPC 2.0 message parsing and routing.
    /// Enforces the safety posture (read-only mode, destructive confirmation),
    /// dispatches to the CommandRegistry, and records every call to the audit log.
    /// </summary>
    public static class JsonRpcHandler
    {
        // JSON-RPC 2.0 error codes
        private const int ParseError = -32700;
        private const int InvalidRequest = -32600;
        private const int MethodNotFound = -32601;
        private const int InvalidParams = -32602;
        private const int InternalError = -32603;

        /// <summary>
        /// Process a raw JSON-RPC request string and return the response JSON.
        /// </summary>
        public static string ProcessRequest(string requestJson)
        {
            JObject request;
            object id = null;

            try
            {
                request = JObject.Parse(requestJson);
            }
            catch (JsonReaderException)
            {
                return CreateErrorResponse(null, ParseError, "Parse error: Invalid JSON", ErrorCode.InvalidParam);
            }

            // Extract request ID (can be string, number, or null)
            id = request["id"]?.ToObject<object>();

            // Validate JSON-RPC 2.0 structure
            string jsonrpc = request["jsonrpc"]?.ToString();
            if (jsonrpc != "2.0")
            {
                return CreateErrorResponse(id, InvalidRequest, "Invalid Request: jsonrpc must be '2.0'", ErrorCode.InvalidParam);
            }

            string method = request["method"]?.ToString();
            if (string.IsNullOrEmpty(method))
            {
                return CreateErrorResponse(id, InvalidRequest, "Invalid Request: method is required", ErrorCode.InvalidParam);
            }

            JObject parameters = request["params"] as JObject ?? new JObject();

            // Look up command in registry
            var command = CommandRegistry.GetCommand(method);
            if (command == null)
            {
                return CreateErrorResponse(id, MethodNotFound, $"Method not found: {method}", ErrorCode.NotFound);
            }

            Settings.EnsureLoaded();

            // ---- Safety gate: read-only mode ---------------------------------
            if (Settings.ReadOnly && command.IsWrite)
            {
                var msg = $"Server is in read-only mode; '{method}' would modify the drawing. " +
                          "Disable with set_server_options({\"read_only\": false}).";
                LogCall(method, false, ErrorCode.ReadOnly, 0, "", command.IsWrite);
                return CreateErrorResponse(id, InternalError, msg, ErrorCode.ReadOnly);
            }

            // ---- Safety gate: destructive confirmation -----------------------
            if (Settings.ConfirmDestructive && command.IsDestructive)
            {
                // Accept both spellings: "__confirm" is the wire form, but MCP
                // servers cannot expose a parameter starting with an underscore,
                // so tool wrappers surface it as "confirm".
                bool confirmed = (parameters["__confirm"] ?? parameters["confirm"])
                                     ?.Value<bool>() ?? false;
                if (!confirmed)
                {
                    var msg = $"'{method}' is destructive and requires confirmation. " +
                              "Re-send the same call with \"__confirm\": true if this is intended.";
                    LogCall(method, false, ErrorCode.NeedsConfirm, 0, "", command.IsWrite);
                    return CreateErrorResponse(id, InternalError, msg, ErrorCode.NeedsConfirm);
                }
            }

            var sw = Stopwatch.StartNew();
            string drawing = "";

            try
            {
                CommandResult result;

                if (command.RunDirect)
                {
                    // Introspection/settings commands never touch the AutoCAD API,
                    // so they answer immediately even while AutoCAD is modal/busy.
                    result = command.Execute(parameters);
                }
                else
                {
                    result = IdleActionRunner.RunOnMainThread(() =>
                    {
                        drawing = SafeDocumentName();
                        return command.Execute(parameters);
                    });
                }

                sw.Stop();
                LogCall(method, result.Success, result.Code, sw.ElapsedMilliseconds, drawing, command.IsWrite);

                if (result.Success)
                {
                    return CreateSuccessResponse(id, result.Data);
                }

                int rpcCode = result.Code == ErrorCode.InvalidParam ? InvalidParams : InternalError;
                return CreateErrorResponse(id, rpcCode, result.Error, result.Code);
            }
            catch (TimeoutException)
            {
                sw.Stop();
                LogCall(method, false, ErrorCode.Timeout, sw.ElapsedMilliseconds, drawing, command.IsWrite);
                return CreateErrorResponse(id, InternalError, DescribeTimeout(method), ErrorCode.Timeout);
            }
            catch (ArgumentException ex)
            {
                // Parameter parsing helpers throw ArgumentException for bad input.
                sw.Stop();
                LogCall(method, false, ErrorCode.InvalidParam, sw.ElapsedMilliseconds, drawing, command.IsWrite);
                return CreateErrorResponse(id, InvalidParams, ex.Message, ErrorCode.InvalidParam);
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogCall(method, false, ErrorCode.Internal, sw.ElapsedMilliseconds, drawing, command.IsWrite);
                return CreateErrorResponse(id, InternalError, $"Internal error: {ex.Message}", ErrorCode.Internal);
            }
        }

        /// <summary>
        /// Read the active document name. Only safe on AutoCAD's main thread.
        /// </summary>
        private static string SafeDocumentName()
        {
            try
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                return doc?.Name ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static void LogCall(string method, bool success, ErrorCode code, long ms, string drawing, bool isWrite)
        {
            ActivityLogger.Log(method, success, code, ms, drawing, isWrite);
        }

        /// <summary>
        /// Turn a bare timeout into a diagnosis.
        ///
        /// Everything here is read from managed state that does not need
        /// AutoCAD's main thread - the tracker's own lock and a tick counter -
        /// because at this moment that thread is precisely what is unavailable.
        /// The age of the last idle tick is the useful signal: a command waiting
        /// at a prompt still lets Idle fire, a modal dialog does not.
        /// </summary>
        private static string DescribeTimeout(string method)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Timeout: AutoCAD did not process '").Append(method)
              .Append("' within the allowed time.");

            double idleAge = IdleActionRunner.SecondsSinceIdle;
            if (idleAge >= 0)
            {
                sb.Append(" AutoCAD's idle loop last ran ")
                  .Append(idleAge.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" s ago.");
                if (idleAge > 2.0)
                    sb.Append(" That is the signature of a MODAL DIALOG: it runs its own message loop, " +
                              "so the plugin never gets the main thread back. No MCP call can close it - " +
                              "someone has to dismiss the dialog in AutoCAD by hand.");
                else
                    sb.Append(" The idle loop is alive, so this call itself is slow or a command is " +
                              "holding the document lock rather than a dialog blocking everything.");
            }

            int pending = IdleActionRunner.PendingCount;
            if (pending > 0)
                sb.Append(" ").Append(pending).Append(pending == 1
                    ? " call is still queued behind it."
                    : " calls are still queued behind it.");

            try
            {
                JObject last = CommandTracker.LastEntry();
                if (last != null)
                    sb.Append(" Last command seen: ").Append(last["command"])
                      .Append(" (").Append(last["status"]).Append(", ").Append(last["time"]).Append(").");
            }
            catch { }

            sb.Append(" Once AutoCAD responds again, call system_status - it reports CMDACTIVE / CMDNAMES - " +
                      "and cancel_command to abort a command that is waiting at a prompt.");
            return sb.ToString();
        }

        private static string CreateSuccessResponse(object id, JToken result)
        {
            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["result"] = result ?? JValue.CreateNull(),
                ["id"] = id != null ? JToken.FromObject(id) : JValue.CreateNull()
            };
            return response.ToString(Formatting.None);
        }

        private static string CreateErrorResponse(object id, int code, string message, ErrorCode errorCode)
        {
            var error = new JObject
            {
                ["code"] = code,
                ["message"] = message
            };

            if (errorCode != ErrorCode.None)
            {
                error["data"] = new JObject { ["errorCode"] = errorCode.ToString() };
            }

            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["error"] = error,
                ["id"] = id != null ? JToken.FromObject(id) : JValue.CreateNull()
            };
            return response.ToString(Formatting.None);
        }
    }
}
