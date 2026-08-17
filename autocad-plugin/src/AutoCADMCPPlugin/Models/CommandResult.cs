using Newtonsoft.Json.Linq;

namespace AutoCADMCPPlugin.Models
{
    /// <summary>
    /// Unified result type for all command executions.
    /// Wraps success/failure state with optional JSON data and a typed error code.
    /// </summary>
    public class CommandResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public JToken Data { get; set; }

        /// <summary>
        /// Typed failure category, surfaced to clients as error.data.errorCode.
        /// ErrorCode.None on success.
        /// </summary>
        public ErrorCode Code { get; set; } = ErrorCode.None;

        public static CommandResult Ok(JToken data = null)
        {
            return new CommandResult
            {
                Success = true,
                Data = data ?? new JObject { ["success"] = true }
            };
        }

        public static CommandResult Ok(string message)
        {
            return new CommandResult
            {
                Success = true,
                Data = new JObject
                {
                    ["success"] = true,
                    ["message"] = message
                }
            };
        }

        /// <summary>
        /// Generic failure. Prefer the typed overload where the category is known.
        /// </summary>
        public static CommandResult Fail(string error)
        {
            return new CommandResult
            {
                Success = false,
                Error = error,
                Code = ErrorCode.Internal
            };
        }

        /// <summary>
        /// Typed failure — lets clients branch on the failure category.
        /// </summary>
        public static CommandResult Fail(ErrorCode code, string error)
        {
            return new CommandResult
            {
                Success = false,
                Error = error,
                Code = code
            };
        }

        // ---- Common typed shorthands ----------------------------------------

        /// <summary>No drawing is open in AutoCAD.</summary>
        public static CommandResult NoDoc()
        {
            return Fail(ErrorCode.NoDocument, "No active document. Open a drawing in AutoCAD first.");
        }

        /// <summary>A required or malformed parameter.</summary>
        public static CommandResult BadParam(string message)
        {
            return Fail(ErrorCode.InvalidParam, message);
        }

        /// <summary>A referenced object could not be found.</summary>
        public static CommandResult NotFound(string message)
        {
            return Fail(ErrorCode.NotFound, message);
        }

        /// <summary>The operation is not supported here.</summary>
        public static CommandResult Unsupported(string message)
        {
            return Fail(ErrorCode.Unsupported, message);
        }
    }
}
