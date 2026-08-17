using System;

namespace AutoCADMCPPlugin.Core
{
    /// <summary>
    /// Derives read/write/destructive classification from a command's method name
    /// so that ~150 command classes do not each need explicit annotation.
    ///
    /// Individual commands can still override the inferred value by overriding
    /// IsWrite / IsDestructive on their base class.
    /// </summary>
    public static class CommandClassifier
    {
        // Prefixes that only ever read state.
        private static readonly string[] ReadPrefixes =
        {
            "list_", "get_", "measure_", "search_", "find_", "select_",
            "system_", "drawing_info", "zoom_", "capture_", "read_", "count_",
            "audit_", "preview_", "export_"
        };

        // Names that destroy existing data badly enough to warrant confirmation.
        // Deliberately tight: routine, easily-undone edits (explode, join, offset)
        // are NOT listed, so confirmation stays meaningful rather than reflexive.
        private static readonly string[] DestructivePrefixes =
        {
            "erase_", "delete_", "purge_", "bulk_erase", "detach_",
            "overkill", "ungroup", "redefine_block"
        };

        /// <summary>
        /// True when the command may modify the drawing database.
        /// </summary>
        public static bool IsWrite(string methodName)
        {
            if (string.IsNullOrEmpty(methodName)) return true; // fail safe: treat unknown as write

            foreach (var p in ReadPrefixes)
            {
                if (methodName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// True when the command destroys existing data and should require confirmation.
        /// </summary>
        public static bool IsDestructive(string methodName)
        {
            if (string.IsNullOrEmpty(methodName)) return false;

            foreach (var p in DestructivePrefixes)
            {
                if (methodName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
