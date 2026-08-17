using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoCADMCPPlugin.Core
{
    /// <summary>
    /// Persisted plugin settings (safety posture + transport ports).
    /// Stored as JSON at %APPDATA%\AutoCADMCP\settings.json so the posture
    /// survives AutoCAD restarts.
    /// </summary>
    public static class Settings
    {
        private static readonly object _lock = new object();
        private static bool _loaded;

        /// <summary>
        /// When true, all write commands are refused with ErrorCode.ReadOnly.
        /// Lets a user hand the AI a drawing to inspect with no risk of edits.
        /// </summary>
        public static bool ReadOnly { get; set; }

        /// <summary>
        /// When true, destructive commands require "__confirm": true in their
        /// parameters. Defaults to ON — matching the Revit MCP's safety posture.
        /// </summary>
        public static bool ConfirmDestructive { get; set; } = true;

        /// <summary>
        /// When true, every command execution is appended to the JSONL activity log.
        /// </summary>
        public static bool AuditLog { get; set; } = true;

        /// <summary>Directory holding settings + activity log.</summary>
        public static string DataDir
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(root, "AutoCADMCP");
            }
        }

        private static string SettingsPath
        {
            get { return Path.Combine(DataDir, "settings.json"); }
        }

        /// <summary>Load settings from disk. Safe to call repeatedly; only reads once.</summary>
        public static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    if (!File.Exists(SettingsPath)) return;
                    var json = JObject.Parse(File.ReadAllText(SettingsPath));
                    ReadOnly = json["readOnly"]?.Value<bool>() ?? ReadOnly;
                    ConfirmDestructive = json["confirmDestructive"]?.Value<bool>() ?? ConfirmDestructive;
                    AuditLog = json["auditLog"]?.Value<bool>() ?? AuditLog;
                }
                catch
                {
                    // Corrupt settings file must never stop the plugin loading.
                }
            }
        }

        /// <summary>Persist current settings to disk.</summary>
        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    var json = new JObject
                    {
                        ["readOnly"] = ReadOnly,
                        ["confirmDestructive"] = ConfirmDestructive,
                        ["auditLog"] = AuditLog
                    };
                    File.WriteAllText(SettingsPath, json.ToString(Formatting.Indented));
                }
                catch
                {
                    // Non-fatal: settings just won't persist across restarts.
                }
            }
        }

        public static JObject ToJson()
        {
            EnsureLoaded();
            return new JObject
            {
                ["read_only"] = ReadOnly,
                ["confirm_destructive"] = ConfirmDestructive,
                ["audit_log"] = AuditLog,
                ["data_dir"] = DataDir
            };
        }
    }
}
