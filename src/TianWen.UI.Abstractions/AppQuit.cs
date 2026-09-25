using System;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Quitting the app, the one rule for the GUI and the TUI: a running session is aborted only once the user
/// confirms it, the host's own background work is cancelled, and the rig is stopped by
/// <see cref="RigShutdown"/> (every run through its own ending, then the cameras warmed and disconnected)
/// while the host keeps its loop going to show the progress. A quit asked for while that runs is refused,
/// since a warm-up must not be cut.
/// </summary>
/// <remarks>
/// <para>The rule lived in the GUI's <c>Program.cs</c> alone, and the TUI had none (P0c item 1 of
/// docs/plans/hardware-in-the-server.md, #788): Q broke its loop straight into a drain that cancelled
/// nothing, so it waited for ever on the mount-limit watcher, which loops until its token is cancelled. The
/// terminal froze with the rig untouched, a running session was awaited to its natural end instead of
/// aborted, and Ctrl+C, read as a key, could not get it out.</para>
/// <para>The host drives it from its own loop: <see cref="Request"/> for a quit (a key, the window's close
/// button), <see cref="Tick"/> once per iteration after its signals, and it stops looping on
/// <see cref="IsComplete"/>. P6 replaces the confirmation with the quit dialog for both hosts.</para>
/// </remarks>
/// <param name="hostBackground">The host's own background work, which is not the rig's: the planner, the
/// weather fetch, the mount-limit watcher, the planetary capture. Cancelled as the stop begins.</param>
/// <param name="beforeRigStop">Anything the host records as it goes (the GUI's remote-rig last-seen).</param>
public sealed class AppQuit(
    GuiAppState appState,
    ViewContexts contexts,
    RigShutdown rig,
    BackgroundTaskTracker tracker,
    CancellationTokenSource hostBackground,
    ITimeProvider timeProvider,
    Func<Task>? beforeRigStop = null)
{
    private string? _progress;

    /// <summary>What the stop is doing now ("Finalising the session: ...", "Warming ..."), or null.</summary>
    public string? Progress => Volatile.Read(ref _progress);

    /// <summary>
    /// The stop has finished: every run has ended, the cameras are warm and disconnected, and the host's
    /// background work has drained, so the host may leave its loop. Reads the tracker's pending set, which
    /// drops finished work only in <see cref="BackgroundTaskTracker.ProcessCompletions"/>, so it holds for a
    /// host that processes completions every iteration (both do).
    /// </summary>
    public bool IsComplete => appState.ShuttingDown && !tracker.HasPending;

    /// <summary>
    /// A quit: Q, Ctrl+C, the window's close button. Asks first while THIS computer's session runs, whichever
    /// rig is on screen (a session on a remote rig is that rig's, and closing a client that watches it is no
    /// reason to stop it); refuses while the rig is already stopping; otherwise stops the rig.
    /// </summary>
    public void Request()
    {
        if (appState.ShuttingDown)
        {
            Notify(NotificationSeverity.Warning, "Warming cameras\u2026 please wait");
            return;
        }

        var local = contexts.Local.LiveSession;
        if (local.Phase is SessionPhase.Finalising)
        {
            Notify(NotificationSeverity.Warning, "Warming cameras\u2026 please wait");
            return;
        }

        if (local.IsRunning && !local.ShowAbortConfirm)
        {
            // The confirmation is drawn by the Live Session tab, which renders the ACTIVE context: with a
            // remote rig on screen it was set where no frame drew it, so the local context goes on screen.
            contexts.Activate(contexts.Local);
            local.ShowAbortConfirm = true;
            appState.QuitRequested = true;
            appState.ActiveTab = GuiTab.LiveSession;
            appState.NeedsRedraw = true;
            return;
        }

        StopRig();
    }

    /// <summary>
    /// Once per loop iteration, after the host has processed its signals: a confirmation dismissed with the
    /// session still running withdraws the quit, and one confirmed (the session has ended) goes on to stop the
    /// rig.
    /// </summary>
    public void Tick()
    {
        if (!appState.QuitRequested)
        {
            return;
        }

        var local = contexts.Local.LiveSession;
        if (!local.ShowAbortConfirm && local.IsRunning && local.SessionCts is { IsCancellationRequested: false })
        {
            // Dismissed. It used to stay requested, and the host then quit by itself when the session ended.
            appState.QuitRequested = false;
            return;
        }

        if (!local.IsRunning && !appState.ShuttingDown)
        {
            appState.QuitRequested = false;
            Request();
        }
    }

    private void StopRig()
    {
        hostBackground.Cancel();

        if (beforeRigStop is { } before)
        {
            tracker.Run(before, "Before the rig stops");
        }

        // ONE tracked task: the runs aborted through their own endings, then the cameras. The host's loop
        // goes on meanwhile, so the progress shows and a second quit is answered rather than frozen out.
        tracker.Run(() => rig.StopAsync(RigShutdownMode.Quit, progress: p => Volatile.Write(ref _progress, p)),
            "Stopping the rig");

        appState.ShuttingDown = true;
        appState.ShutdownComplete = false;
        Notify(NotificationSeverity.Info, "Shutting down\u2026");
    }

    private void Notify(NotificationSeverity severity, string message)
    {
        appState.AppendNotification(timeProvider.GetUtcNow(), severity, message);
        appState.NeedsRedraw = true;
    }
}
