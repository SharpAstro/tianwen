using NSubstitute;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="RigShutdown"/>: the runs first, each through its own ending, and the cameras only once every
/// run has ENDED; a quit aborts the runs, a lost display lets the night finish (P0a of
/// docs/plans/hardware-in-the-server.md, #743). The runs here are fakes that end the way the real ones do:
/// cancelled or on their own, then their own Finalise, then the run's ended signal.
/// </summary>
public class RigShutdownTests(ITestOutputHelper output)
{
    private static readonly Uri CameraUri = new("Camera://FakeDevice/cam1");

    [Fact(Timeout = 30_000)]
    public async Task AQuitAbortsEveryRunAndStopsTheCamerasOnlyOnceTheyHaveAllEnded()
    {
        var ct = TestContext.Current.CancellationToken;
        var local = new LiveSessionState();
        var (hub, disconnects) = HubWithOneIdleCamera(local);
        var finalise = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = StartFakeSession(local, endsOnItsOwn: NeverEnds(), finalise.Task);
        var flats = StartFakeFlatRun(local);
        var polar = StartFakePolarRun(local);

        var stop = new RigShutdown(local, hub, Substitute.For<ITimeProvider>(), FakeExternal.CreateLogger(output))
            .StopAsync(RigShutdownMode.Quit, stopRig: ct);

        session.IsCancellationRequested.ShouldBeTrue("a quit aborts the session into its Finalise");
        flats.IsCancellationRequested.ShouldBeTrue("a quit aborts a flat run, which the old quit never did");
        polar.IsCancellationRequested.ShouldBeTrue("a quit stops polar alignment, whose refine loop held the old quit for ever");

        // The session's Finalise is still warming its cameras: the quit must not touch them yet. The old
        // quit started its own ramp on the same camera at this moment.
        stop.IsCompleted.ShouldBeFalse();
        disconnects.ShouldBeEmpty();

        finalise.SetResult();
        await stop.WaitAsync(ct);

        disconnects.ShouldHaveSingleItem().ShouldBe((CameraUri, RunStillGoing: false));
    }

    [Fact(Timeout = 30_000)]
    public async Task WithTheDisplayLostTheSessionFinishesOnItsOwnAndItsPromptIsAnsweredUnattended()
    {
        var ct = TestContext.Current.CancellationToken;
        var local = new LiveSessionState();
        var (hub, disconnects) = HubWithOneIdleCamera(local);
        var nightEnds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = StartFakeSession(local, nightEnds.Task, Task.CompletedTask);
        var polar = StartFakePolarRun(local);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        local.PendingPrompt = new SessionPromptEventArgs("Panel", "Switch the panel on", "Continue", "Cancel",
            answer, defaultIfUnanswerable: true);

        var stop = new RigShutdown(local, hub, Substitute.For<ITimeProvider>(), FakeExternal.CreateLogger(output))
            .StopAsync(RigShutdownMode.DisplayLost, stopRig: ct);

        (await answer.Task.WaitAsync(ct)).ShouldBeTrue("nobody can see the prompt, so it gets its unattended answer");
        local.PendingPrompt.ShouldBeNull();
        local.AnswerPromptsUnattended.ShouldBeTrue("a prompt raised from now on is answered the same way");
        polar.IsCancellationRequested.ShouldBeTrue("polar alignment is interactive and meaningless unseen");
        session.IsCancellationRequested.ShouldBeFalse("the night goes on without a display");
        stop.IsCompleted.ShouldBeFalse();
        disconnects.ShouldBeEmpty();

        nightEnds.SetResult();
        await stop.WaitAsync(ct);

        session.IsCancellationRequested.ShouldBeFalse("nobody aborted the session: it ended");
        disconnects.ShouldHaveSingleItem().ShouldBe((CameraUri, RunStillGoing: false));
    }

    [Fact(Timeout = 30_000)]
    public async Task ClosingTheWindowWithTheDisplayLostStopsTheRig()
    {
        var ct = TestContext.Current.CancellationToken;
        var local = new LiveSessionState();
        var (hub, disconnects) = HubWithOneIdleCamera(local);
        var session = StartFakeSession(local, endsOnItsOwn: NeverEnds(), Task.CompletedTask);
        using var stopRig = new CancellationTokenSource();

        var stop = new RigShutdown(local, hub, Substitute.For<ITimeProvider>(), FakeExternal.CreateLogger(output))
            .StopAsync(RigShutdownMode.DisplayLost, stopRig.Token);

        session.IsCancellationRequested.ShouldBeFalse();
        await stopRig.CancelAsync();
        session.IsCancellationRequested.ShouldBeTrue("closing the headless window means stop the rig");

        await stop.WaitAsync(ct);
        disconnects.ShouldHaveSingleItem().ShouldBe((CameraUri, RunStillGoing: false));
    }

    /// <summary>
    /// The headless window's title is the latest progress, and says that closing stops the rig only while
    /// a run goes on. A "goes on" reported but not yet shown when the window was closed used to overwrite
    /// "stopping" as soon as the loop got round to it, so the stop now reports itself before
    /// <see cref="CancellationTokenSource.Cancel()"/> returns, on the thread that asked.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AStopRequestedWhileTheSessionGoesOnIsTheLatestProgressBeforeCancelReturns()
    {
        var ct = TestContext.Current.CancellationToken;
        var local = new LiveSessionState();
        var (hub, _) = HubWithOneIdleCamera(local);
        var finalise = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StartFakeSession(local, endsOnItsOwn: NeverEnds(), finalise.Task);
        using var stopRig = new CancellationTokenSource();
        string? latest = null;

        var stop = new RigShutdown(local, hub, Substitute.For<ITimeProvider>(), FakeExternal.CreateLogger(output))
            .StopAsync(RigShutdownMode.DisplayLost, stopRig.Token, p => Volatile.Write(ref latest, p));

        Volatile.Read(ref latest).ShouldBe(RigShutdown.SessionGoesOn);
        stopRig.Cancel(); // what the headless window's OnQuit does, on the loop thread
        Volatile.Read(ref latest).ShouldBe(RigShutdown.StoppingTheRig);

        finalise.SetResult();
        await stop.WaitAsync(ct);
    }

    [Fact(Timeout = 30_000)]
    public async Task AStopRequestedBeforeTheWaitNeverReportsThatTheSessionGoesOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var local = new LiveSessionState();
        var (hub, _) = HubWithOneIdleCamera(local);
        StartFakeSession(local, endsOnItsOwn: NeverEnds(), Task.CompletedTask);
        using var stopRig = new CancellationTokenSource();
        stopRig.Cancel();
        var reported = new ConcurrentQueue<string>();

        await new RigShutdown(local, hub, Substitute.For<ITimeProvider>(), FakeExternal.CreateLogger(output))
            .StopAsync(RigShutdownMode.DisplayLost, stopRig.Token, reported.Enqueue).WaitAsync(ct);

        reported.ShouldNotContain(RigShutdown.SessionGoesOn);
        reported.ShouldContain(RigShutdown.StoppingTheRig);
    }

    [Fact(Timeout = 30_000)]
    public async Task TwoStopsAtOnceStopEachCameraOnce()
    {
        // A display that dies while a quit is already stopping the rig joins that stop.
        var ct = TestContext.Current.CancellationToken;
        var local = new LiveSessionState();
        var (hub, disconnects) = HubWithOneIdleCamera(local);
        var shutdown = new RigShutdown(local, hub, Substitute.For<ITimeProvider>(), FakeExternal.CreateLogger(output));

        await Task.WhenAll(
            shutdown.StopAsync(RigShutdownMode.Quit, stopRig: ct),
            shutdown.StopAsync(RigShutdownMode.DisplayLost, stopRig: ct)).WaitAsync(ct);

        disconnects.ShouldHaveSingleItem();
    }

    private static Task NeverEnds() => new TaskCompletionSource().Task;

    // A session run as SessionBootstrapper runs one: it ends when cancelled or on its own, then its
    // Finalise runs, then IsRunning drops and the run's ended signal completes, last of all.
    private static CancellationTokenSource StartFakeSession(LiveSessionState local, Task endsOnItsOwn, Task finalise)
    {
        var ended = local.BeginSession();
        var cts = new CancellationTokenSource();
        local.SessionCts = cts;
        local.IsRunning = true;
        _ = Task.Run(async () =>
        {
            try { await endsOnItsOwn.WaitAsync(cts.Token); }
            catch (OperationCanceledException) { }
            await finalise;
            local.IsRunning = false;
            ended.TrySetResult();
        });
        return cts;
    }

    private static CancellationTokenSource StartFakeFlatRun(LiveSessionState local)
    {
        var ended = local.BeginFlatRun();
        var cts = new CancellationTokenSource();
        local.FlatsCts = cts;
        _ = Task.Run(async () =>
        {
            try { await NeverEnds().WaitAsync(cts.Token); }
            catch (OperationCanceledException) { }
            local.FlatsCts = null;
            ended.TrySetResult();
        });
        return cts;
    }

    private static CancellationTokenSource StartFakePolarRun(LiveSessionState local)
    {
        var ended = local.BeginPolarRun();
        var cts = new CancellationTokenSource();
        local.PolarAlignmentCts = cts;
        _ = Task.Run(async () =>
        {
            try { await NeverEnds().WaitAsync(cts.Token); }
            catch (OperationCanceledException) { }
            local.PolarAlignmentCts = null;
            ended.TrySetResult();
        });
        return cts;
    }

    // One connected camera with its cooler off (so it is disconnected, not warmed), which records each
    // disconnect together with whether a run was still going at that moment.
    private static (IDeviceHub Hub, ConcurrentQueue<(Uri Uri, bool RunStillGoing)> Disconnects) HubWithOneIdleCamera(LiveSessionState local)
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.CanGetCoolerOn.Returns(false);
        camera.GetCameraStateAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(CameraState.Idle));

        var disconnects = new ConcurrentQueue<(Uri, bool)>();
        var hub = Substitute.For<IDeviceHub>();
        hub.ConnectedDevices.Returns(new List<(Uri DeviceUri, IDeviceDriver Driver)> { (CameraUri, camera) });
        hub.TryGetConnectedDriver<ICameraDriver>(CameraUri, out Arg.Any<ICameraDriver?>())
            .Returns(call => { call[1] = camera; return true; });
        hub.DisconnectAsync(CameraUri, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                disconnects.Enqueue((CameraUri, local.IsRunning || local.FlatsCts is not null || local.PolarAlignmentCts is not null));
                return ValueTask.CompletedTask;
            });
        return (hub, disconnects);
    }
}
