using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoCADMCPPlugin.Core
{
    /// <summary>
    /// Append-only JSONL activity log — one JSON object per command execution.
    ///
    /// Every record carries user / machine / drawing from day one so the log can
    /// later be aggregated firm-wide without a schema change ("local-first,
    /// central-ready", matching the Revit MCP's audit design).
    /// </summary>
    public static class ActivityLogger
    {
        private static readonly object _writeLock = new object();

        private static string LogPath
        {
            get { return Path.Combine(Settings.DataDir, "activity.jsonl"); }
        }

        /// <summary>
        /// Record one command execution. Never throws — logging failures must not
        /// break the command pipeline.
        /// </summary>
        public static void Log(
            string method,
            bool success,
            Models.ErrorCode code,
            long durationMs,
            string drawing,
            bool isWrite)
        {
            try
            {
                Settings.EnsureLoaded();
                if (!Settings.AuditLog) return;

                var record = new JObject
                {
                    ["ts"] = DateTime.UtcNow.ToString("o"),
                    ["method"] = method ?? "",
                    ["success"] = success,
                    ["error_code"] = code == Models.ErrorCode.None ? null : code.ToString(),
                    ["duration_ms"] = durationMs,
                    ["is_write"] = isWrite,
                    ["drawing"] = drawing ?? "",
                    ["user"] = SafeUser(),
                    ["machine"] = SafeMachine()
                };

                lock (_writeLock)
                {
                    Directory.CreateDirectory(Settings.DataDir);
                    File.AppendAllText(LogPath, record.ToString(Formatting.None) + Environment.NewLine);
                }
            }
            catch
            {
                // Audit logging is best-effort by design.
            }
        }

        private static string SafeUser()
        {
            try { return Environment.UserName; } catch { return ""; }
        }

        private static string SafeMachine()
        {
            try { return Environment.MachineName; } catch { return ""; }
        }
    }
}
