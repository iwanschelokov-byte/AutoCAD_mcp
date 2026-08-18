using System;
using System.Collections.Concurrent;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Core
{
    /// <summary>
    /// Marshals work from background threads (socket handlers) to AutoCAD's main UI thread.
    /// Uses Application.Idle event to dequeue and execute pending actions.
    /// Similar to Revit's ExternalEvent pattern.
    /// </summary>
    public static class IdleActionRunner
    {
        private static readonly ConcurrentQueue<ActionItem> _queue = new ConcurrentQueue<ActionItem>();
        private static volatile bool _hooked;
        private static readonly object _hookLock = new object();

        /// <summary>
        /// Default timeout for commands (30 seconds).
        /// Modal dialogs or heavy operations may need more.
        /// </summary>
        public static int TimeoutMs { get; set; } = 30000;

        private static long _lastIdleTicks;

        /// <summary>
        /// Seconds since AutoCAD's idle loop last ran, or -1 if it has never run.
        /// A modal dialog runs its own message loop, so this number growing past
        /// the timeout is the signature of a dialog blocking the bridge.
        /// </summary>
        public static double SecondsSinceIdle
        {
            get
            {
                long ticks = Interlocked.Read(ref _lastIdleTicks);
                if (ticks == 0) return -1;
                return (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerSecond;
            }
        }

        /// <summary>Actions queued but not yet picked up by the main thread.</summary>
        public static int PendingCount => _queue.Count;

        /// <summary>
        /// Execute a function on AutoCAD's main thread and return the result.
        /// Blocks the calling thread until execution completes or times out.
        /// </summary>
        public static Models.CommandResult RunOnMainThread(Func<Models.CommandResult> action)
        {
            EnsureHooked();

            var item = new ActionItem(action);
            _queue.Enqueue(item);

            // Wait for the main thread to pick it up and execute
            if (!item.CompletionEvent.Wait(TimeoutMs))
            {
                // Abandon it. Without this the action stays in the queue and
                // fires whenever AutoCAD becomes idle again - minutes later, in
                // a document that may no longer be the one the caller meant.
                item.Abandoned = true;
                throw new TimeoutException(
                    $"AutoCAD main thread did not execute the command within {TimeoutMs}ms.");
            }

            if (item.Exception != null)
            {
                return Models.CommandResult.Fail($"Execution error: {item.Exception.Message}");
            }

            return item.Result;
        }

        private static void EnsureHooked()
        {
            if (_hooked) return;
            lock (_hookLock)
            {
                if (_hooked) return;
                Application.Idle += OnIdle;
                _hooked = true;
            }
        }

        /// <summary>
        /// Fires on AutoCAD's main (UI) thread whenever AutoCAD is idle.
        /// Dequeues and executes all pending actions.
        /// </summary>
        private static void OnIdle(object sender, EventArgs e)
        {
            Interlocked.Exchange(ref _lastIdleTicks, DateTime.UtcNow.Ticks);

            while (_queue.TryDequeue(out ActionItem item))
            {
                // The caller has already given up and been told so. Running the
                // action now would change the drawing behind its back.
                if (item.Abandoned) continue;

                try
                {
                    item.Result = item.Action();
                }
                catch (Exception ex)
                {
                    item.Exception = ex;
                    item.Result = Models.CommandResult.Fail($"Exception: {ex.Message}");
                }
                finally
                {
                    item.CompletionEvent.Set();
                }
            }
        }

        /// <summary>
        /// Unhook from Idle event. Called during plugin termination.
        /// </summary>
        public static void Unhook()
        {
            lock (_hookLock)
            {
                if (!_hooked) return;
                Application.Idle -= OnIdle;
                _hooked = false;
            }

            // Signal any waiting threads so they don't hang
            while (_queue.TryDequeue(out ActionItem item))
            {
                item.Exception = new OperationCanceledException("Plugin shutting down");
                item.CompletionEvent.Set();
            }
        }

        private class ActionItem
        {
            public Func<Models.CommandResult> Action { get; }
            public ManualResetEventSlim CompletionEvent { get; } = new ManualResetEventSlim(false);
            public Models.CommandResult Result { get; set; }
            public Exception Exception { get; set; }
            public volatile bool Abandoned;

            public ActionItem(Func<Models.CommandResult> action)
            {
                Action = action;
            }
        }
    }
}
