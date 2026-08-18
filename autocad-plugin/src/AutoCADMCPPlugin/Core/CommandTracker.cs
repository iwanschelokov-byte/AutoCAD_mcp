using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Core
{
    /// <summary>
    /// Records AutoCAD command activity so that MCP callers can find out what
    /// actually happened after an asynchronous <c>execute_command</c>.
    ///
    /// <c>SendStringToExecute</c> is fire-and-forget: the plugin cannot report
    /// the outcome of a command in the same JSON-RPC response. Instead every
    /// command start / end / failure / cancellation is appended to a ring
    /// buffer here, each with a monotonically increasing sequence number.
    /// <c>execute_command</c> returns the sequence number that was current when
    /// the string was queued ("since"), and <c>read_command_line</c> returns
    /// everything newer than that — which is exactly the effect of that one call.
    /// </summary>
    public static class CommandTracker
    {
        private const int Capacity = 100;

        private static readonly LinkedList<Entry> _log = new LinkedList<Entry>();
        private static readonly HashSet<Document> _hooked = new HashSet<Document>();
        private static readonly object _lock = new object();
        private static bool _installed;
        private static long _seq;

        private class Entry
        {
            public long Seq;
            public string Command;
            public string Status;      // queued | started | ended | failed | cancelled
            public string Document;
            public string Detail;
            public DateTime TimeUtc;
        }

        /// <summary>Sequence number of the most recent entry (0 if empty).</summary>
        public static long CurrentSeq
        {
            get { lock (_lock) { return _seq; } }
        }

        /// <summary>
        /// Hook the document collection and every document that is already open.
        /// Safe to call more than once.
        /// </summary>
        public static void Install()
        {
            lock (_lock)
            {
                if (_installed) return;
                try
                {
                    DocumentCollection dm = Application.DocumentManager;
                    foreach (Document d in dm)
                        HookNoLock(d);
                    dm.DocumentCreated += OnDocumentCreated;
                    dm.DocumentDestroyed += OnDocumentDestroyed;
                    _installed = true;
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MCP] CommandTracker.Install failed: {ex.Message}");
                }
            }
        }

        public static void Uninstall()
        {
            lock (_lock)
            {
                if (!_installed) return;
                try
                {
                    DocumentCollection dm = Application.DocumentManager;
                    dm.DocumentCreated -= OnDocumentCreated;
                    dm.DocumentDestroyed -= OnDocumentDestroyed;
                    foreach (Document d in new List<Document>(_hooked))
                        UnhookNoLock(d);
                }
                catch { }
                _hooked.Clear();
                _installed = false;
            }
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            lock (_lock) { HookNoLock(e.Document); }
        }

        private static void OnDocumentDestroyed(object sender, DocumentDestroyedEventArgs e)
        {
            // The Document object is already gone here; just record the fact.
            Record(null, "document_closed", e.FileName, null);
        }

        private static void HookNoLock(Document d)
        {
            if (d == null || _hooked.Contains(d)) return;
            d.CommandWillStart += OnCommandWillStart;
            d.CommandEnded += OnCommandEnded;
            d.CommandFailed += OnCommandFailed;
            d.CommandCancelled += OnCommandCancelled;
            _hooked.Add(d);
        }

        private static void UnhookNoLock(Document d)
        {
            if (d == null) return;
            try
            {
                d.CommandWillStart -= OnCommandWillStart;
                d.CommandEnded -= OnCommandEnded;
                d.CommandFailed -= OnCommandFailed;
                d.CommandCancelled -= OnCommandCancelled;
            }
            catch { }
            _hooked.Remove(d);
        }

        private static void OnCommandWillStart(object sender, CommandEventArgs e)
        {
            Record(e.GlobalCommandName, "started", DocName(sender), null);
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            Record(e.GlobalCommandName, "ended", DocName(sender), null);
        }

        private static void OnCommandFailed(object sender, CommandEventArgs e)
        {
            Record(e.GlobalCommandName, "failed", DocName(sender), LastPrompt());
        }

        private static void OnCommandCancelled(object sender, CommandEventArgs e)
        {
            Record(e.GlobalCommandName, "cancelled", DocName(sender), LastPrompt());
        }

        private static string DocName(object sender)
        {
            try { return (sender as Document)?.Name; }
            catch { return null; }
        }

        /// <summary>
        /// Last line echoed to the AutoCAD command line. This is where "Unknown
        /// command", "requires numeric distance" and similar messages show up —
        /// they never raise CommandFailed because no command ever started.
        /// </summary>
        public static string LastPrompt()
        {
            try { return Application.GetSystemVariable("LASTPROMPT") as string; }
            catch { return null; }
        }

        /// <summary>Append an entry and return its sequence number.</summary>
        public static long Record(string command, string status, string document, string detail)
        {
            lock (_lock)
            {
                long seq = ++_seq;
                _log.AddLast(new Entry
                {
                    Seq = seq,
                    Command = command,
                    Status = status,
                    Document = document,
                    Detail = detail,
                    TimeUtc = DateTime.UtcNow
                });
                while (_log.Count > Capacity)
                    _log.RemoveFirst();
                return seq;
            }
        }

        /// <summary>
        /// Entries newer than <paramref name="since"/> (pass 0 for "everything
        /// in the buffer"), newest last, capped at <paramref name="limit"/>.
        /// </summary>
        public static JArray GetEntries(long since, int limit)
        {
            if (limit <= 0) limit = 20;
            var wanted = new List<Entry>();
            lock (_lock)
            {
                foreach (Entry e in _log)
                    if (e.Seq > since) wanted.Add(e);
            }
            int skip = Math.Max(0, wanted.Count - limit);
            var arr = new JArray();
            for (int i = skip; i < wanted.Count; i++)
                arr.Add(ToJson(wanted[i]));
            return arr;
        }

        /// <summary>
        /// The newest entry in the buffer, or null if nothing has been recorded.
        /// Reading this takes only the tracker's own lock, so it is safe from the
        /// socket thread while AutoCAD's main thread is blocked - which is
        /// exactly when a caller most needs to know what the last command was.
        /// </summary>
        public static JObject LastEntry()
        {
            lock (_lock)
            {
                return _log.Last == null ? null : ToJson(_log.Last.Value);
            }
        }

        /// <summary>Most recent failed/cancelled entry newer than <paramref name="since"/>, or null.</summary>
        public static JObject LastProblem(long since)
        {
            lock (_lock)
            {
                for (LinkedListNode<Entry> n = _log.Last; n != null; n = n.Previous)
                {
                    if (n.Value.Seq <= since) break;
                    if (n.Value.Status == "failed" || n.Value.Status == "cancelled")
                        return ToJson(n.Value);
                }
            }
            return null;
        }

        private static JObject ToJson(Entry e)
        {
            var o = new JObject
            {
                ["seq"] = e.Seq,
                ["command"] = e.Command ?? "",
                ["status"] = e.Status,
                ["time"] = e.TimeUtc.ToString("HH:mm:ss.fff")
            };
            if (!string.IsNullOrEmpty(e.Document)) o["document"] = e.Document;
            if (!string.IsNullOrEmpty(e.Detail)) o["detail"] = e.Detail;
            return o;
        }
    }
}
