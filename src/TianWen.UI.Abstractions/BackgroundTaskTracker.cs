using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Tracks background tasks submitted from UI callbacks. Checks for completions
    /// each frame and logs errors. Shared between GUI and TUI.
    /// </summary>
    public class BackgroundTaskTracker
    {
        private readonly List<(Task Task, string Description)> _pending = [];

        /// <summary>
        /// Submits an async operation to run in the background.
        /// </summary>
        public void Run(Func<Task> work, string description)
        {
            _pending.Add((Task.Run(work), description));
        }

        /// <summary>
        /// Checks for completed tasks, logs errors, and removes them from the pending list.
        /// Call once per frame from the render loop. Returns true if any task completed
        /// (caller should trigger a redraw).
        /// </summary>
        public bool ProcessCompletions(ILogger logger)
        {
            var anyCompleted = false;
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].Task.IsCompleted)
                {
                    if (_pending[i].Task.IsFaulted)
                    {
                        logger.LogError(_pending[i].Task.Exception,
                            "Background operation failed: {Description}", _pending[i].Description);
                    }
                    _pending.RemoveAt(i);
                    anyCompleted = true;
                }
            }
            return anyCompleted;
        }

        /// <summary>Whether any tasks are still pending.</summary>
        public bool HasPending => _pending.Count > 0;

        /// <summary>Number of pending tasks.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>
        /// Awaits all pending tasks (swallowing exceptions). Call at shutdown.
        /// </summary>
        public async Task DrainAsync()
        {
            foreach (var (task, _) in _pending)
            {
                try { await task; } catch { /* already logged by ProcessCompletions */ }
            }
            _pending.Clear();
        }
    }
}
