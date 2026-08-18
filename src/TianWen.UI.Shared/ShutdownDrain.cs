using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.UI.Shared;

/// <summary>
/// Draining background work at shutdown without letting the window stop responding.
/// </summary>
/// <remarks>
/// <para><b>The window and the Vulkan context can only be destroyed on the thread that created
/// them</b> -- the one that ran the event loop. That makes the shape of a host's shutdown
/// load-bearing, and both of the obvious spellings are wrong:</para>
///
/// <list type="bullet">
/// <item><description><c>await drain;</c> then dispose. The await resumes the rest of the method
/// on whichever thread completed the awaited task, which for anything non-trivial is a thread-pool
/// thread. Destroying the window from there WEDGES the process: the destroy needs the owning
/// thread to pump messages, and the owning thread is the one blocked inside async <c>Main</c>'s
/// entry point waiting for that very task. Observed as a window stuck "Not Responding" at 0% CPU
/// after a long AI enhance, needing Task Manager to clear.</description></item>
/// <item><description><c>drain.Wait();</c> then dispose. This fixes the thread affinity and
/// reintroduces the symptom by another route: the loop is not pumping, so the window is
/// unresponsive for however long the drain takes. A shutdown that cannot be cancelled quickly --
/// a camera warming up, a running AI pass -- must still leave a live window behind it.</description></item>
/// </list>
///
/// <para>So keep the loop running while the drain proceeds. Events keep dispatching, the window
/// stays alive, and the teardown after this returns still happens on the loop's own thread.</para>
/// </remarks>
public static class ShutdownDrain
{
    /// <summary>How long to keep pumping before abandoning the background work. Bounded so a task
    /// that will not cancel costs a slow exit rather than a process that has to be killed.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Pumps <paramref name="loop"/> until <paramref name="drain"/> completes or the timeout
    /// elapses. Must be called on the loop's own thread, immediately after <c>Run</c> returns and
    /// before any window or renderer is disposed.
    /// </summary>
    /// <param name="loop">The event loop that has just stopped.</param>
    /// <param name="drain">The shutdown task to wait for.</param>
    /// <param name="logger">Logs when the timeout is hit; the abandoned work is worth knowing about.</param>
    /// <param name="timeout">Overrides <see cref="DefaultTimeout"/>.</param>
    /// <returns>True if the drain finished, false if it was abandoned at the timeout.</returns>
    public static bool PumpUntilComplete(SdlEventLoop loop, Task drain, ILogger? logger = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(drain);

        if (drain.IsCompleted)
        {
            return true;
        }

        var budget = timeout ?? DefaultTimeout;

        // Nothing of the app's own state is safe to draw from here: the shutdown is disposing the
        // documents and sources the render callback reads. Paint the background only.
        loop.OnRender = null;
        loop.CheckNeedsRedraw = () =>
        {
            // Polled once per loop iteration, ON this thread, so the loop stops itself rather than
            // having another thread reach in and write its running flag.
            if (drain.IsCompleted)
            {
                loop.Stop();
            }
            return false;
        };

        // A second close request during the drain means the user has waited long enough.
        loop.OnQuit = () =>
        {
            loop.Stop();
            return true;
        };

        using var deadline = new CancellationTokenSource(budget);
        loop.Run(deadline.Token);

        if (drain.IsCompleted)
        {
            // Surface a drain that failed rather than swallowing it -- the host is about to exit,
            // so this is the last chance to say anything about it.
            if (drain.IsFaulted)
            {
                logger?.LogWarning(drain.Exception?.GetBaseException(), "Shutdown: background work faulted while draining.");
            }
            return true;
        }

        logger?.LogWarning("Shutdown: background work did not finish within {Seconds}s; abandoning it.",
            budget.TotalSeconds);
        return false;
    }
}
