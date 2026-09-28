using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Quitting the app, the one rule for the GUI and the TUI (decision 1 of docs/plans/hardware-in-the-server.md, #936). The
/// rig is this computer's node's, so a window that closes stops nothing by itself: only the LAST client attached to the node
/// asks, and a client that is not the last one detaches without asking, so closing a second window never warms a rig the
/// first is watching. With a run going on the question is whether to leave it running (the default) or stop the rig (the
/// node's abort, its Finalise, then every device warmed up and disconnected, with progress). With devices connected and no
/// run it is whether to warm them up and disconnect them (the default, which the node finishes after the window has gone)
/// or leave them connected. With neither it quits at once.
/// </summary>
/// <remarks>
/// <para>The host drives it from its own loop: <see cref="Request"/> for a quit (a key, the window's close button),
/// <see cref="Answer"/> for the dialog's choice, and it stops looping on <see cref="IsComplete"/>. The question is the
/// local view's <see cref="LiveSessionState.QuitDialog"/>, drawn by each host's Live Session tab its own way (the GUI's
/// card, the TUI's status line).</para>
/// <para>The TUI had no quit rule before P0c (#788): Q drained a tracker nothing had cancelled, and hung for ever on the
/// mount-limit watcher. Every quit here cancels the host's own background work first, whatever it then does with the rig.</para>
/// </remarks>
/// <param name="hostBackground">The host's own background work, which is not the rig's: the planner, the weather fetch,
/// the watching of the node's runs. Cancelled as the quit goes ahead.</param>
/// <param name="beforeQuit">Anything the host records as it goes (the GUI's remote-rig last-seen).</param>
public sealed class AppQuit(
    GuiAppState appState,
    ViewContexts contexts,
    RigShutdown rig,
    BackgroundTaskTracker tracker,
    CancellationTokenSource hostBackground,
    ITimeProvider timeProvider,
    ILogger logger,
    Func<Task>? beforeQuit = null)
{
    private string? _progress;
    private int _deciding;

    /// <summary>What the stop is doing now ("Finalising the session: ...", "Warming ..."), or null.</summary>
    public string? Progress => Volatile.Read(ref _progress);

    /// <summary>
    /// The quit has finished: whatever it asked of the node has been asked (and, for "Stop the rig", done), and the host's
    /// background work has drained, so the host may leave its loop. Reads the tracker's pending set, which drops finished
    /// work only in <see cref="BackgroundTaskTracker.ProcessCompletions"/>, so it holds for a host that processes completions
    /// every iteration (both do).
    /// </summary>
    public bool IsComplete => appState.ShuttingDown && !tracker.HasPending;

    /// <summary>
    /// A quit: Q, Ctrl+C, the window's close button. Refused while the quit is already going ahead; ignored while the
    /// question is on screen or being worked out; otherwise asks the node what is going on and asks the user only as
    /// decision 1 says.
    /// </summary>
    public void Request()
    {
        if (appState.ShuttingDown)
        {
            Notify(NotificationSeverity.Warning, Volatile.Read(ref _progress) is { } stopping
                ? $"Quitting… {stopping}, please wait"
                : "Quitting… please wait");
            return;
        }
        if (contexts.Local.LiveSession.QuitDialog is not null)
        {
            // Asked already: put the question back on screen, where a switch of view may have taken it from.
            ShowQuestion();
            return;
        }
        if (Interlocked.CompareExchange(ref _deciding, 1, 0) != 0)
        {
            return;
        }

        tracker.Run(DecideAsync, "Deciding how to quit");
    }

    // What the node holds decides the question: the node is asked, never this process's picture of it.
    private async Task DecideAsync()
    {
        try
        {
            if (await AskAsync().ConfigureAwait(false) is { } dialog)
            {
                contexts.Local.LiveSession.QuitDialog = dialog;
                ShowQuestion();
            }
            else
            {
                GoAhead();
            }
        }
        finally
        {
            Volatile.Write(ref _deciding, 0);
        }
    }

    private async Task<QuitDialog?> AskAsync()
    {
        if (appState.LocalNode is not { } node)
        {
            // No node: nothing of the rig is this window's to ask about.
            return null;
        }

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider.System);
        var answer = await node.Client.GetNodeAsync(budget.Token).ConfigureAwait(false);
        if (answer.Value is not { } now)
        {
            // A node that does not answer cannot be stopped from here either; the window goes.
            logger.LogWarning("Quit: this computer's node did not answer ({Error}); quitting without asking", answer.Error);
            return null;
        }

        // Only the last client asks. The count includes this one while its event stream is attached.
        var others = now.ClientsAttached - (node.Mirror.IsEventStreamConnected ? 1 : 0);
        if (others > 0)
        {
            logger.LogInformation("Quit: {Others} other client(s) attached to this computer's node; detaching without asking", others);
            return null;
        }
        if (now.Run is { } run)
        {
            return QuitDialog.RunGoingOn(run);
        }

        var connected = node.Devices.Values.Count(static d => d.Connected);
        var aCameraNeedsWarming = node.Devices.Values.Any(static d => d.Connected && d.Camera is { } camera && camera.ToReading().NeedsWarmUp);
        return connected > 0 ? QuitDialog.DevicesConnected(connected, aCameraNeedsWarming) : null;
    }

    // The question is drawn by the Live Session tab, which renders the ACTIVE context: with a remote rig on screen it would
    // be set where no frame draws it, so this computer's goes on screen.
    private void ShowQuestion()
    {
        contexts.Activate(contexts.Local);
        appState.ActiveTab = GuiTab.LiveSession;
        contexts.Local.LiveSession.NeedsRedraw = true;
        appState.NeedsRedraw = true;
    }

    /// <summary>The dialog's choice, or null to stay (Escape).</summary>
    public void Answer(QuitAction? action)
    {
        var local = contexts.Local.LiveSession;
        if (local.QuitDialog is null)
        {
            return;
        }
        local.QuitDialog = null;
        local.NeedsRedraw = true;
        appState.NeedsRedraw = true;

        switch (action)
        {
            case null:
                return;

            case QuitAction.StopTheRig when appState.LocalNode is { } node:
                GoAhead(ct => rig.StopAsync(node.Client, p => Volatile.Write(ref _progress, p), ct), "Stopping the rig");
                return;

            case QuitAction.WarmUpAndDisconnect or QuitAction.Disconnect when appState.LocalNode is { } node:
                // Taken by the node, which finishes the warm-ups after this window has gone (warming only a camera that needs it).
                GoAhead(ct => rig.WarmUpAndDisconnectAsync(node.Client, p => Volatile.Write(ref _progress, p), untilDone: false, ct),
                    action is QuitAction.Disconnect ? "Disconnecting" : "Warming up and disconnecting");
                return;

            default:
                GoAhead();
                return;
        }
    }

    private void GoAhead(Func<CancellationToken, Task>? withTheRig = null, string? what = null)
    {
        hostBackground.Cancel();

        if (beforeQuit is { } before)
        {
            tracker.Run(before, "Before quitting");
        }
        if (withTheRig is { } work)
        {
            // Not the host's background token, which the quit has just cancelled: a stop the user asked for runs to its end.
            tracker.Run(() => work(CancellationToken.None), what ?? "Stopping the rig");
        }

        appState.ShuttingDown = true;
        appState.ShutdownComplete = false;
        Notify(NotificationSeverity.Info, "Shutting down…");
    }

    private void Notify(NotificationSeverity severity, string message)
    {
        appState.AppendNotification(timeProvider.GetUtcNow(), severity, message);
        appState.NeedsRedraw = true;
    }
}
