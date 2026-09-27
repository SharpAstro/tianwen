using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.DAL;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Quitting while this computer's node holds the rig (decision 1 of docs/plans/hardware-in-the-server.md, #936): only the
/// last client asks; with a run going on it is left running unless the user stops the rig, which ends the run through its
/// own ending and only then warms up and disconnects every device; with devices connected and no run they are warmed up
/// and disconnected by the node after the window has gone, unless the user leaves them connected. Through the GUI's real
/// signal handler and <see cref="AppQuit"/> over a real node on its socket.
/// </summary>
[Collection("NodeProcesses")]
public class QuitThroughTheNodeTests(ITestOutputHelper output)
{
    private static bool AnyConnected(GuiNodeHarness h) => h.Hub.ConnectedDevices.Count > 0;

    private static async Task<QuitDialog> AskedAsync(GuiNodeHarness h, AppQuit quit)
    {
        quit.Request();
        await h.UntilAsync(() => h.Contexts.Local.LiveSession.QuitDialog is not null || h.AppState.ShuttingDown, TestContext.Current.CancellationToken);
        h.AppState.ShuttingDown.ShouldBeFalse("premise: the quit asked rather than going ahead");
        h.AppState.ActiveTab.ShouldBe(GuiTab.LiveSession, "the question is drawn on the Live Session tab");
        return h.Contexts.Local.LiveSession.QuitDialog.ShouldNotBeNull();
    }

    private static async Task StartedCaptureAsync(GuiNodeHarness h)
    {
        var ct = TestContext.Current.CancellationToken;
        var started = await h.Local.Client.StartPlanetaryAsync(new PlanetaryRequestDto(), ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        await h.Local.RefreshDevicesNowAsync(ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task WithNothingConnectedAQuitGoesWithoutAsking()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        foreach (var uri in new[] { h.MountUri, h.CameraUri, h.FocuserUri })
        {
            await h.Hub.DisconnectAsync(uri, force: true, ct);
        }
        await h.Local.RefreshDevicesNowAsync(ct);
        // The host's own background work, which runs until the host cancels it: the quit must, or it never completes.
        var background = h.HostBackground;
        h.Tracker.Run(() => Task.Delay(Timeout.Infinite, background), "A stand-in for the host's background work");
        var quit = h.Quit();

        quit.Request();

        await h.UntilAsync(() => quit.IsComplete, ct);
        h.Contexts.Local.LiveSession.QuitDialog.ShouldBeNull("there was nothing to ask about");
        background.IsCancellationRequested.ShouldBeTrue("a quit cancels the host's background work");
    }

    [Fact(Timeout = 60_000)]
    public async Task WithDevicesConnectedItAsksAndTheNodeWarmsThemUpAndDisconnectsThemAfterTheWindowHasGone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var quit = h.Quit();

        var asked = await AskedAsync(h, quit);
        (asked.Default, asked.Other).ShouldBe((QuitAction.WarmUpAndDisconnect, QuitAction.LeaveConnected));
        asked.Message.ShouldStartWith("3 devices are connected");

        quit.Answer(asked.Default);

        await h.UntilAsync(() => quit.IsComplete, ct);
        // The node finishes what it was asked, whatever became of the window.
        await h.UntilAsync(() => !AnyConnected(h), ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task LeavingThemConnectedQuitsAndTouchesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var quit = h.Quit();

        quit.Answer((await AskedAsync(h, quit)).Other);

        await h.UntilAsync(() => quit.IsComplete, ct);
        h.Hub.ConnectedDevices.Count.ShouldBe(3, "left connected on the node");
    }

    [Fact(Timeout = 60_000)]
    public async Task WithARemoteRigOnScreenTheQuestionComesOnScreenOnThisComputersView()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct, remoteOnScreen: true);
        var quit = h.Quit();

        await AskedAsync(h, quit);

        h.Contexts.Active.ShouldBeSameAs(h.Contexts.Local, "the Live Session tab draws the active view, and the question is this computer's");
    }

    [Fact(Timeout = 60_000)]
    public async Task EscapeStaysAndTheNextQuitAsksAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var quit = h.Quit();

        quit.Answer(null);
        (await AskedAsync(h, quit)).ShouldNotBeNull();
        quit.Answer(null);

        h.Contexts.Local.LiveSession.QuitDialog.ShouldBeNull();
        h.AppState.ShuttingDown.ShouldBeFalse("staying is staying");
        (await AskedAsync(h, quit)).Default.ShouldBe(QuitAction.WarmUpAndDisconnect, "a later quit asks again");
    }

    [Fact(Timeout = 60_000)]
    public async Task WithARunGoingOnItAsksAndLeavingTheRigRunningLeavesTheRunToTheNode()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        await StartedCaptureAsync(h);
        var quit = h.Quit();

        var asked = await AskedAsync(h, quit);
        (asked.Default, asked.Other).ShouldBe((QuitAction.LeaveTheRigRunning, QuitAction.StopTheRig));
        asked.Message.ShouldStartWith("A planetary capture is running");
        asked.Message.ShouldContain("on this computer. Leaving it running closes only this window");

        quit.Answer(asked.Default);

        await h.UntilAsync(() => quit.IsComplete, ct);
        (await h.Local.Client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull().Running.ShouldBeTrue("the run goes on without the window");
        (await h.Local.Client.StopPlanetaryAsync(ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 90_000)]
    public async Task StoppingTheRigDisconnectsNothingUntilTheSessionHasFinalised()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var session = await RunsThroughTheNodeTests.StartedSessionAsync(h, setpointC: -10);
        var quit = h.Quit();

        var asked = await AskedAsync(h, quit);
        asked.Default.ShouldBe(QuitAction.LeaveTheRigRunning);
        quit.Answer(QuitAction.StopTheRig);

        await session.Cancelled.Task.WaitAsync(ct);
        await h.UntilAsync(() => quit.Progress == RigShutdown.Ending(NodeRunKind.Session), ct);
        h.Hub.ConnectedDevices.Count.ShouldBe(3, "the session's Finalise parks and warms first; nothing is pulled from under it");
        quit.IsComplete.ShouldBeFalse();

        session.Finalise.TrySetResult();

        await h.UntilAsync(() => quit.IsComplete, ct);
        AnyConnected(h).ShouldBeFalse("once the run ended, every device was warmed up and disconnected");
        (await h.Local.Client.GetNodeAsync(ct)).Value.ShouldNotBeNull().Run.ShouldBeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task AQuitWhileTheRigStopsIsRefusedSayingWhatItWaitsFor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var session = await RunsThroughTheNodeTests.StartedSessionAsync(h, setpointC: -10);
        var quit = h.Quit();
        quit.Answer((await AskedAsync(h, quit)).Other);
        await session.Cancelled.Task.WaitAsync(ct);
        await h.UntilAsync(() => quit.Progress is not null, ct);

        quit.Request();

        h.AppState.Notifications.ShouldContain(static n => n.Severity == NotificationSeverity.Warning
            && n.Message == "Quitting… Finalising the session: park, warm-up, covers, please wait");
        h.Contexts.Local.LiveSession.QuitDialog.ShouldBeNull("it does not ask again");
        session.Finalise.TrySetResult();
        await h.UntilAsync(() => quit.IsComplete, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task AClientThatIsNotTheLastDetachesWithoutAsking()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        await StartedCaptureAsync(h);
        // Another window on the same node: the laptop watching over RDP, say.
        await using var other = await NodeWindow.OpenAsync(h.Node, output, ct);
        await h.UntilAsync(() => h.Local.Mirror.IsEventStreamConnected, ct);
        var quit = h.Quit();

        quit.Request();

        await h.UntilAsync(() => quit.IsComplete, ct);
        h.Contexts.Local.LiveSession.QuitDialog.ShouldBeNull("only the last client asks");
        (await h.Local.Client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull().Running.ShouldBeTrue();
        h.Hub.ConnectedDevices.Count.ShouldBe(3, "closing a second window never warms a rig the first is watching");
        (await h.Local.Client.StopPlanetaryAsync(ct)).IsSuccess.ShouldBeTrue();
    }
}
