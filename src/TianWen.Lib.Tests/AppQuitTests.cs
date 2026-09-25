using System;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The quit rule both hosts use (<see cref="AppQuit"/>; P0c item 1 of docs/plans/hardware-in-the-server.md, #788).
/// The TUI had none: Q drained a tracker it never cancelled, so it waited for ever on the mount-limit watcher,
/// which runs until its token is cancelled (seen live: the process still running minutes after Q, its screen
/// gone). Each host here carries a stand-in with that shape.
/// </summary>
public class AppQuitTests
{
    [Fact(Timeout = 30_000)]
    public async Task AQuitWithNothingRunningCancelsTheHostsBackgroundWorkAndCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new Host();

        host.Quit.Request();

        host.Background.IsCancellationRequested.ShouldBeTrue("the watcher is the host's, and nothing else ends it");
        host.AppState.ShuttingDown.ShouldBeTrue();
        await host.RunUntilCompleteAsync(ct);
        host.WatcherEnded.ShouldBeTrue();
    }

    [Fact]
    public void AQuitWhileThisComputersSessionRunsAsksFirstWithItsContextOnScreen()
    {
        var host = new Host();
        host.StartSession();
        host.Contexts.Activate(host.Contexts.GetOrAddRemote("observatory-node", "Observatory"));

        host.Quit.Request();

        host.Local.ShowAbortConfirm.ShouldBeTrue();
        host.AppState.QuitRequested.ShouldBeTrue();
        host.AppState.ActiveTab.ShouldBe(GuiTab.LiveSession);
        host.Contexts.Active.IsLocal.ShouldBeTrue("the confirmation is drawn where the Live Session tab renders");
        host.AppState.ShuttingDown.ShouldBeFalse("nothing stops before the user confirms");
        host.Background.IsCancellationRequested.ShouldBeFalse();
        host.EndSession();
    }

    [Fact]
    public void ADismissedConfirmationWithdrawsTheQuit()
    {
        var host = new Host();
        host.StartSession();
        host.Quit.Request();

        host.Local.ShowAbortConfirm = false; // Esc
        host.Quit.Tick();

        host.AppState.QuitRequested.ShouldBeFalse("it used to stay requested, and the host quit by itself at the session's end");
        host.AppState.ShuttingDown.ShouldBeFalse();
        host.EndSession();
        host.Quit.Tick();
        host.AppState.ShuttingDown.ShouldBeFalse("the session ending on its own is no quit");
    }

    [Fact(Timeout = 30_000)]
    public async Task AConfirmedAbortStopsTheRigOnceTheSessionHasEnded()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new Host();
        host.StartSession();
        host.Quit.Request();

        // Enter: the confirmation goes and the session is cancelled, but it runs on through its Finalise.
        host.Local.ShowAbortConfirm = false;
        await host.Local.SessionCts!.CancelAsync();
        host.Quit.Tick();
        host.AppState.QuitRequested.ShouldBeTrue("confirmed, so the quit waits for the session to end");
        host.AppState.ShuttingDown.ShouldBeFalse();

        host.EndSession();
        host.Quit.Tick();

        host.AppState.ShuttingDown.ShouldBeTrue();
        await host.RunUntilCompleteAsync(ct);
    }

    [Fact(Timeout = 30_000)]
    public async Task ASecondQuitWhileTheRigStopsIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new Host();
        var warming = new TaskCompletionSource();
        host.Tracker.Run(() => warming.Task, "Warming the camera");

        host.Quit.Request();
        host.Quit.Request();

        host.AppState.Notifications[0].Message.ShouldBe("Warming cameras\u2026 please wait");
        host.Quit.IsComplete.ShouldBeFalse("a warm-up must not be cut");
        warming.SetResult();
        await host.RunUntilCompleteAsync(ct);
    }

    /// <summary>What a host owns: its state, its tracker, its own background work, and the rig's stop.</summary>
    private sealed class Host
    {
        private int _watcherEnded;
        private TaskCompletionSource? _sessionEnded;

        public Host()
        {
            var timeProvider = new SystemTimeProvider();
            Quit = new AppQuit(AppState, Contexts, new RigShutdown(Local, hub: null, timeProvider, NullLogger.Instance),
                Tracker, Background, timeProvider);

            // Shaped like the mount-limit watcher: it runs until its token is cancelled, and only the host can.
            Tracker.Run(async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, Background.Token);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Exchange(ref _watcherEnded, 1);
                }
            }, "Mount limit watcher");
        }

        public GuiAppState AppState { get; } = new GuiAppState();
        public ViewContexts Contexts { get; } = new ViewContexts();
        public BackgroundTaskTracker Tracker { get; } = new BackgroundTaskTracker();
        public CancellationTokenSource Background { get; } = new CancellationTokenSource();
        public AppQuit Quit { get; }
        public LiveSessionState Local => Contexts.Local.LiveSession;
        public bool WatcherEnded => Volatile.Read(ref _watcherEnded) == 1;

        public void StartSession()
        {
            _sessionEnded = Local.BeginSession();
            Local.SessionCts = new CancellationTokenSource();
            Local.IsRunning = true;
        }

        public void EndSession()
        {
            Local.IsRunning = false;
            _sessionEnded?.TrySetResult();
        }

        /// <summary>The host's loop, as far as a quit is concerned.</summary>
        public async Task RunUntilCompleteAsync(CancellationToken ct)
        {
            while (!Quit.IsComplete)
            {
                Tracker.ProcessCompletions(NullLogger.Instance);
                Quit.Tick();
                await Task.Delay(10, ct);
            }
        }
    }
}
