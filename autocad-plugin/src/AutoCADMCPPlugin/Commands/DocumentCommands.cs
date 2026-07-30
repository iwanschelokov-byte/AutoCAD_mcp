using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCPPlugin.Core;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    /// <summary>Shared document lookup helpers.</summary>
    internal static class DocumentHelper
    {
        /// <summary>Full, comparable form of a document path ("" when unknown).</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return path.Trim().TrimEnd('\\', '/'); }
        }

        /// <summary>
        /// The open document whose file is <paramref name="path"/>, or null.
        /// Matching is by normalized full path, then by file name only, so
        /// callers can pass either form.
        /// </summary>
        public static Document FindOpen(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            string wanted = Normalize(path);
            string wantedName = null;
            try { wantedName = Path.GetFileName(wanted); } catch { }

            Document byName = null;
            foreach (Document d in Application.DocumentManager)
            {
                string dn;
                try { dn = d.Name; } catch { continue; }
                if (string.IsNullOrEmpty(dn)) continue;

                if (Normalize(dn).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    return d;

                if (byName == null && !string.IsNullOrEmpty(wantedName))
                {
                    string fn = null;
                    try { fn = Path.GetFileName(dn); } catch { }
                    if (!string.IsNullOrEmpty(fn) && fn.Equals(wantedName, StringComparison.OrdinalIgnoreCase))
                        byName = d;
                }
            }
            return byName;
        }

        /// <summary>
        /// Whether <paramref name="doc"/> has edits that have not been written
        /// to disk, or null when that cannot be determined.
        ///
        /// DBMOD is the only thing AutoCAD offers here, and
        /// Application.GetSystemVariable reads it from the *active* document, so
        /// the answer is only trustworthy for that one. Returning null rather
        /// than guessing keeps callers from reporting "nothing was lost" about a
        /// drawing whose state we never actually looked at.
        /// </summary>
        public static bool? HasUnsavedChanges(Document doc)
        {
            try
            {
                if (doc == null) return null;
                if (!ReferenceEquals(doc, Application.DocumentManager.MdiActiveDocument)) return null;
                object v = Application.GetSystemVariable("DBMOD");
                if (v == null) return null;
                return Convert.ToInt32(v) != 0;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Close one drawing. Without this, an MCP session could open drawings but
    /// never let go of them: AutoCAD keeps the file locked, so the next
    /// drawing_open of the same file (or any external tool) fails.
    ///
    /// <c>save</c> defaults to false — the drawing is closed and any changes
    /// are discarded. Pass save=true to write the file before closing.
    /// </summary>
    public class DrawingCloseCommand : ICommand
    {
        public string MethodName => "drawing_close";

        public CommandResult Execute(JObject parameters)
        {
            string path = parameters?["path"]?.ToString();
            bool save = parameters?["save"]?.Value<bool>() ?? false;

            Document doc;
            if (!string.IsNullOrWhiteSpace(path))
            {
                doc = DocumentHelper.FindOpen(path);
                if (doc == null)
                    return CommandResult.Fail($"No open drawing matches '{path}'. Use drawing_list to see what is open.");
            }
            else
            {
                doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return CommandResult.Fail("No active document");
            }

            // Capture identity first: after the close the Document is disposed.
            string name, filename;
            try { name = doc.Name; } catch { name = null; }
            try { filename = doc.Database?.Filename; } catch { filename = null; }

            if (save && string.IsNullOrEmpty(filename))
                return CommandResult.Fail(
                    "Cannot save this drawing on close: it has never been written to disk. " +
                    "Call drawing_save with a 'path' first, or close with save=false.");

            // Ask before closing: afterwards the document is gone and DBMOD
            // belongs to whatever drawing AutoCAD made active next.
            bool? hadChanges = DocumentHelper.HasUnsavedChanges(doc);

            var result = new JObject
            {
                ["document"] = name,
                ["path"] = filename,
                ["saved"] = save
            };
            if (hadChanges.HasValue) result["had_unsaved_changes"] = hadChanges.Value;

            if (Application.DocumentManager.IsApplicationContext)
            {
                string err = CloseOne(doc, save);
                if (err != null) return CommandResult.Fail(err);
                result["closed"] = true;

                // "Closed without saving" reads like a warning, and it was
                // printed even when the drawing had nothing to save. Say which
                // of the two actually happened.
                if (save)
                {
                    result["status"] = "saved";
                    result["message"] = hadChanges == false
                        ? $"Drawing closed and written to disk (it had no pending changes): {name}"
                        : $"Drawing saved and closed: {name}";
                }
                else if (hadChanges == false)
                {
                    result["status"] = "closed_unchanged";
                    result["message"] = $"Drawing closed; there was nothing to save: {name}";
                }
                else if (hadChanges == true)
                {
                    result["status"] = "changes_discarded";
                    result["message"] = $"Drawing closed and its unsaved changes were discarded: {name}";
                }
                else
                {
                    result["status"] = "closed";
                    result["message"] =
                        $"Drawing closed without saving: {name} (it was not the active drawing, " +
                        "so whether it had unsaved changes could not be checked).";
                }
            }
            else
            {
                // Not in application context — closing here would deadlock.
                // Queue it and tell the caller it is asynchronous.
                Document target = doc;
                bool doSave = save;
                Application.DocumentManager.ExecuteInApplicationContext(
                    state => CloseOne(target, doSave), null);
                result["closed"] = false;
                result["queued"] = true;
                result["status"] = "queued";
                result["message"] = $"Close queued for {name}; verify with drawing_list.";
            }

            return CommandResult.Ok(result);
        }

        internal static string CloseOne(Document doc, bool save)
        {
            try
            {
                string filename = null;
                try { filename = doc.Database?.Filename; } catch { }

                if (save && !string.IsNullOrEmpty(filename))
                    doc.CloseAndSave(filename);
                else
                    doc.CloseAndDiscard();
                return null;
            }
            catch (System.Exception ex)
            {
                return $"Failed to close drawing: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Close every open drawing. <c>save</c> defaults to false.
    /// <c>keep</c> optionally names one drawing (path or file name) to leave open.
    /// </summary>
    public class CloseAllCommand : ICommand
    {
        public string MethodName => "close_all";

        public CommandResult Execute(JObject parameters)
        {
            bool save = parameters?["save"]?.Value<bool>() ?? false;
            string keep = parameters?["keep"]?.ToString();
            Document keepDoc = string.IsNullOrWhiteSpace(keep) ? null : DocumentHelper.FindOpen(keep);

            // Snapshot: the collection changes while we close.
            var docs = new List<Document>();
            foreach (Document d in Application.DocumentManager)
                if (!ReferenceEquals(d, keepDoc)) docs.Add(d);

            var closed = new JArray();
            var failed = new JArray();

            if (!Application.DocumentManager.IsApplicationContext)
            {
                List<Document> queued = docs;
                bool doSave = save;
                Application.DocumentManager.ExecuteInApplicationContext(state =>
                {
                    foreach (Document d in queued)
                        DrawingCloseCommand.CloseOne(d, doSave);
                }, null);

                return CommandResult.Ok(new JObject
                {
                    ["queued"] = true,
                    ["requested"] = docs.Count,
                    ["message"] = $"Close queued for {docs.Count} drawing(s); verify with drawing_list."
                });
            }

            foreach (Document d in docs)
            {
                string name;
                try { name = d.Name; } catch { name = "(unknown)"; }

                string err = DrawingCloseCommand.CloseOne(d, save);
                if (err == null) closed.Add(name);
                else failed.Add(new JObject { ["document"] = name, ["error"] = err });
            }

            var result = new JObject
            {
                ["closed"] = closed,
                ["closed_count"] = closed.Count,
                ["saved"] = save
            };
            if (failed.Count > 0) result["failed"] = failed;
            if (keepDoc != null)
            {
                try { result["kept"] = keepDoc.Name; } catch { }
            }
            result["message"] = $"Closed {closed.Count} drawing(s)" + (save ? " with save." : " without saving.");
            return CommandResult.Ok(result);
        }
    }

    /// <summary>List every open drawing, marking the active one.</summary>
    public class DrawingListCommand : ICommand
    {
        public string MethodName => "drawing_list";

        public CommandResult Execute(JObject parameters)
        {
            Document active = Application.DocumentManager.MdiActiveDocument;
            var docs = new JArray();

            foreach (Document d in Application.DocumentManager)
            {
                var o = new JObject();
                try { o["document"] = d.Name; } catch { o["document"] = "(unknown)"; }
                try { o["path"] = d.Database?.Filename; } catch { }
                o["active"] = ReferenceEquals(d, active);
                try { o["read_only"] = d.IsReadOnly; } catch { }
                // Only meaningful for the active drawing; see HasUnsavedChanges.
                bool? dirty = DocumentHelper.HasUnsavedChanges(d);
                if (dirty.HasValue) o["unsaved_changes"] = dirty.Value;
                docs.Add(o);
            }

            return CommandResult.Ok(new JObject
            {
                ["documents"] = docs,
                ["count"] = docs.Count,
                ["active"] = active?.Name
            });
        }
    }

    /// <summary>
    /// Read back what AutoCAD did after an asynchronous <c>execute_command</c>.
    ///
    /// Because commands are sent with SendStringToExecute, the JSON-RPC response
    /// for execute_command cannot carry the outcome — a misspelled command or a
    /// rejected input used to fail completely silently. execute_command now
    /// returns a "since" sequence number; pass it here to get exactly the
    /// command activity that call produced, plus the last command-line prompt.
    /// </summary>
    public class ReadCommandLineCommand : ICommand
    {
        public string MethodName => "read_command_line";

        public CommandResult Execute(JObject parameters)
        {
            long since = parameters?["since"]?.Value<long>() ?? 0;
            int limit = parameters?["limit"]?.Value<int>() ?? 20;

            var result = new JObject
            {
                ["since"] = since,
                ["current_seq"] = CommandTracker.CurrentSeq,
                ["history"] = CommandTracker.GetEntries(since, limit),
                ["last_prompt"] = CommandTracker.LastPrompt() ?? ""
            };

            JObject problem = CommandTracker.LastProblem(since);
            if (problem != null) result["last_error"] = problem;

            return CommandResult.Ok(result);
        }
    }
}
