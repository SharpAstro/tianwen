using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing.PolarAlignment;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Polar alignment as the node's run (P5 part 4 of docs/plans/hardware-in-the-server.md, #934): it claims the mount and the
/// camera for as long as it runs, its frames are the OTA's, a stop is Done and Cancel alike and gives the rig back once
/// the mount is restored, and it ends by itself once no client has been present for the detach grace, while a client
/// back within the grace keeps it. The solver is a stand-in: what is under test is the node's run, not the routine's
/// geometry, which PolarAlignmentSessionTests covers.
/// </summary>
[Collection("Hosting")]
public class NodePolarAlignmentTests(ITestOutputHelper outputHelper)
{
    private static readonly FakeDevice Camera = new FakeDevice(DeviceType.Camera, 1);
    private static readonly FakeDevice Mount = new FakeDevice(DeviceType.Mount, 1);

    /// <summary>Two short rungs and one solve per pose: a Phase A that cannot solve fails in about a second.</summary>
    private static readonly PolarAlignmentRequestDto Quick = new PolarAlignmentRequestDto
    {
        Configuration = new PolarAlignmentConfigDto { ExposureRampSeconds = [0.05, 0.1], ReferenceFrameAverages = 1 },
    };

    /// <summary>
    /// Eight 1 s rungs: over a solver that never answers, each rung waits out its budget (five exposures and 5 s) before
    /// the next, so the run stays in Phase A for over a minute, however long a test needs it going on.
    /// </summary>
    private static readonly PolarAlignmentRequestDto Holding = new PolarAlignmentRequestDto
    {
        Configuration = new PolarAlignmentConfigDto { ExposureRampSeconds = [.. Enumerable.Repeat(1.0, 8)], ReferenceFrameAverages = 1 },
    };

    private Task<NodeHarness> PolarNodeAsync(StandInSolver solver, TimeSpan grace, CancellationToken ct, Action<IServiceCollection>? configure = null) =>
        NodeHarness.StartAsync(outputHelper, ct, services =>
        {
            services.AddSingleton<IPlateSolverFactory>(solver);
            services.AddSingleton(new NodeRunWatchOptions(grace, TimeSpan.FromMilliseconds(100)));
            configure?.Invoke(services);
        });

    private static TianWenNodeClient ClientOf(NodeHarness node) => new TianWenNodeClient(node.Client);

    private static Task<PolarStateDto> UntilEndedAsync(TianWenNodeClient client, CancellationToken ct) =>
        UntilAsync<PolarStateDto>("the polar alignment to end", async token =>
        {
            var state = (await client.GetPolarAlignmentAsync(token)).Value;
            return (state is { Running: false } ? state : null, state is null ? "none" : $"{state.Phase}: {state.StatusMessage}");
        }, ct);

    private static Task<PolarStateDto> UntilProbingAsync(TianWenNodeClient client, StandInSolver solver, CancellationToken ct) =>
        UntilAsync<PolarStateDto>("the first rung to be solving", async token =>
        {
            var state = (await client.GetPolarAlignmentAsync(token)).Value;
            return (state is { Phase: PolarAlignmentPhase.ProbingExposure } && solver.Solves > 0 ? state : null,
                state is null ? "none" : $"{state.Phase} after {solver.Solves} solves: {state.StatusMessage}");
        }, ct);

    private static Task<NodeResult<CameraSettingsDto>> CommandTheCameraAsync(TianWenNodeClient client, CancellationToken ct) =>
        client.SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = Camera.DeviceUri.ToString(), Bin = 1 }, ct);

    /// <summary>Opens a window on <paramref name="node"/> and waits until the node counts it as present.</summary>
    private Task<NodeWindow> WatchingAsync(NodeHarness node, CancellationToken ct) => NodeWindow.OpenAsync(node, outputHelper, ct);

    [Fact(Timeout = 60_000)]
    public async Task APhaseAThatCannotSolveEndsTheRunAndGivesTheRigBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await PolarNodeAsync(StandInSolver.Answering(null), NodeRunWatchOptions.Default.DetachGrace, ct);
        await node.ActivateRigAsync(Camera, Mount, ct);
        var client = ClientOf(node);

        var started = await client.StartPolarAlignmentAsync(Quick, ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        started.Value.ShouldNotBeNull().OtaIndex.ShouldBe(0);

        var ended = await UntilEndedAsync(client, ct);
        ended.Phase.ShouldBe(PolarAlignmentPhase.Idle);
        var reason = ended.FailureReason.ShouldNotBeNull();
        ended.StatusMessage.ShouldBe(reason, "a failure keeps its words on the status line through the restore");
        ended.PhaseA.ShouldNotBeNull().Success.ShouldBeFalse();
        ended.Wcs.ShouldBeNull("nothing solved");

        var frame = await client.GetLatestFrameAsync(FrameSources.Ota(0), after: null, new FrameReader(), ct);
        frame.Image.ShouldNotBeNull("its probe frames are the OTA's, as a client watching it sees them").Release();

        var command = await CommandTheCameraAsync(client, ct);
        command.IsSuccess.ShouldBeTrue(command.Error);
    }

    [Fact(Timeout = 60_000)]
    public async Task ARunHoldsTheRigUntilItIsDoneAndThenGivesItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var solver = StandInSolver.NeverAnswering();
        await using var node = await PolarNodeAsync(solver, NodeRunWatchOptions.Default.DetachGrace, ct);
        await node.ActivateRigAsync(Camera, Mount, ct);
        var client = ClientOf(node);
        await using var window = await WatchingAsync(node, ct);

        (await client.StartPolarAlignmentAsync(Holding, ct)).IsSuccess.ShouldBeTrue();
        await UntilProbingAsync(client, solver, ct);
        (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().Run.ShouldNotBeNull().Kind.ShouldBe(NodeRunKind.Polar);

        var command = await CommandTheCameraAsync(client, ct);
        command.StatusCode.ShouldBe(409);
        command.Error.ShouldNotBeNull().ShouldContain(PolarAlignmentRun.LeaseOwner);
        var again = await client.StartPolarAlignmentAsync(Quick, ct);
        (again.StatusCode, again.Error).ShouldBe((409, "Polar alignment is running"));
        var darks = await client.StartDarkLibraryAsync(new DarkLibraryRequestDto { DeviceUri = Camera.DeviceUri.ToString(), ExposureSeconds = 1, Count = 1 }, ct);
        (darks.StatusCode, darks.Error).ShouldBe((409, "Polar alignment is running"));

        var stopping = await client.StopPolarAlignmentAsync(ct);
        stopping.IsSuccess.ShouldBeTrue(stopping.Error);

        var ended = await UntilEndedAsync(client, ct);
        ended.FailureReason.ShouldBeNull("Done is how it ends, not a failure");
        ended.StatusMessage.ShouldBe("Polar alignment ended");
        (await CommandTheCameraAsync(client, ct)).IsSuccess.ShouldBeTrue("the camera is given back as the run ends");
        (await client.StopPolarAlignmentAsync(ct)).StatusCode.ShouldBe(404, "nothing is running to stop");
    }

    [Fact(Timeout = 90_000)]
    public async Task AWatchedRunGoesOnAClientBackWithinTheGraceKeepsItAndAnUnwatchedOneStops()
    {
        var ct = TestContext.Current.CancellationToken;
        var grace = TimeSpan.FromSeconds(4);
        var solver = StandInSolver.NeverAnswering();
        await using var node = await PolarNodeAsync(solver, grace, ct);
        await node.ActivateRigAsync(Camera, Mount, ct);
        var client = ClientOf(node);
        await using var window = await WatchingAsync(node, ct);

        (await client.StartPolarAlignmentAsync(Holding, ct)).IsSuccess.ShouldBeTrue();
        await UntilProbingAsync(client, solver, ct);
        await Task.Delay(grace * 2, ct);
        (await client.GetPolarAlignmentAsync(ct)).Value.ShouldNotBeNull().Running.ShouldBeTrue("a window is watching it");

        // The window freezes for longer than a beat may lapse, and comes back before the grace is spent.
        window.Drawing = false;
        await Task.Delay(NodeWire.PresenceLapse + TimeSpan.FromSeconds(1), ct);
        window.Drawing = true;
        await Task.Delay(grace + TimeSpan.FromSeconds(2), ct);
        (await client.GetPolarAlignmentAsync(ct)).Value.ShouldNotBeNull().Running.ShouldBeTrue("its window came back within the grace");

        // It freezes for good.
        window.Drawing = false;
        var frozeAt = DateTimeOffset.UtcNow;
        var ended = await UntilEndedAsync(client, ct);
        (DateTimeOffset.UtcNow - frozeAt).ShouldBeGreaterThan(grace, "the grace is whole before it is stopped");
        ended.FailureReason.ShouldBeNull();
        var notes = (await client.GetNotificationsAsync(ct)).Value.ShouldNotBeNull();
        notes.ShouldContain(n => n.Message.StartsWith("Polar alignment stopped: no client has watched it"));
        (await CommandTheCameraAsync(client, ct)).IsSuccess.ShouldBeTrue("the camera is given back as the run ends");
    }

    [Fact(Timeout = 60_000)]
    public async Task ARefusedStartSaysWhyAndHoldsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await PolarNodeAsync(StandInSolver.Answering(null), NodeRunWatchOptions.Default.DetachGrace, ct);
        var client = ClientOf(node);

        var noProfile = await client.StartPolarAlignmentAsync(Quick, ct);
        (noProfile.StatusCode, noProfile.Error).ShouldBe((409, "The node has no active profile to align"));

        await node.ActivateRigAsync(Camera, mount: null, ct);
        var noMount = await client.StartPolarAlignmentAsync(Quick, ct);
        noMount.StatusCode.ShouldBe(409);
        noMount.Error.ShouldNotBeNull().ShouldStartWith("Mount not connected");

        node.Node.IsRunning.ShouldBeFalse();
        (await CommandTheCameraAsync(client, ct)).IsSuccess.ShouldBeTrue("a refused start claimed nothing");
    }

    [Fact(Timeout = 60_000)]
    public async Task AStopMeantForAPolarAlignmentNeverAbortsTheRunThatReplacedIt()
    {
        // The watch and both stop routes look at the run, then stop it: a session that replaced it in between must not be
        // the one stopped.
        var ct = TestContext.Current.CancellationToken;
        await using var node = await PolarNodeAsync(StandInSolver.Answering(null), NodeRunWatchOptions.Default.DetachGrace, ct);
        node.Factory.Initialised.SetResult();
        await node.ActivateRigAsync(Camera, Mount, ct);
        var client = ClientOf(node);
        (await client.StartPolarAlignmentAsync(Quick, ct)).IsSuccess.ShouldBeTrue();
        await UntilEndedAsync(client, ct);
        await node.UntilTheNodeIsIdleAsync(ct);
        var polar = node.Node.CurrentRun.ShouldNotBeNull();

        var session = await node.StartSessionAsync(ct);

        node.Node.TryAbort(polar).ShouldBeNull("that run has ended, and a session is the node's run now");
        (await client.StopPolarAlignmentAsync(ct)).StatusCode.ShouldBe(404);
        session.Cancelled.Task.IsCompleted.ShouldBeFalse("a stop meant for polar alignment never reaches a session");
    }

    [Fact(Timeout = 60_000)]
    public async Task TheJournalRecordsAPolarAlignmentByItsKind()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Directory.CreateTempSubdirectory("twj").FullName, "node.journal");
        var solver = StandInSolver.NeverAnswering();
        await using var node = await PolarNodeAsync(solver, NodeRunWatchOptions.Default.DetachGrace, ct,
            services => services.AddSingleton(new NodeJournalOptions(path, null, static () => null, TimeProvider.System)));
        await node.ActivateRigAsync(Camera, Mount, ct);
        var client = ClientOf(node);
        await using var window = await WatchingAsync(node, ct);

        (await client.StartPolarAlignmentAsync(Holding, ct)).IsSuccess.ShouldBeTrue();

        var journal = await UntilTheJournalAsync(path, static j => j.Run is not null, ct);
        journal.Run.ShouldNotBeNull().Kind.ShouldBe(NodeRunKind.Polar, "the next node reports which run a crash interrupted");

        (await client.StopPolarAlignmentAsync(ct)).IsSuccess.ShouldBeTrue();
        await UntilEndedAsync(client, ct);
    }
}
