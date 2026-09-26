using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A live planetary capture as the node's run (P5 part 5 of docs/plans/hardware-in-the-server.md, #934), over the fake
/// camera's drifting planet: the node streams and stacks, a client watches the live frame and the rolling master as frames,
/// the knobs reach the camera after the next frame, the camera is the capture's while it runs and given back as it ends,
/// and the capture stops once no client has been present for the detach grace.
/// </summary>
[Collection("Hosting")]
public class NodePlanetaryTests(ITestOutputHelper outputHelper)
{
    private static readonly FakeDevice Camera = new FakeDevice(DeviceType.Camera, 1);

    private static readonly PlanetaryRequestDto Default = new PlanetaryRequestDto();

    private Task<NodeHarness> PlanetaryNodeAsync(TimeSpan grace, CancellationToken ct, Action<IServiceCollection>? configure = null) =>
        NodeHarness.StartAsync(outputHelper, ct, services =>
        {
            services.AddSingleton(new NodeRunWatchOptions(grace, TimeSpan.FromMilliseconds(100)));
            configure?.Invoke(services);
        });

    private static TianWenNodeClient ClientOf(NodeHarness node) => new TianWenNodeClient(node.Client);

    private static Task<NodeResult<CameraSettingsDto>> CommandTheCameraAsync(TianWenNodeClient client, CancellationToken ct) =>
        client.SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = Camera.DeviceUri.ToString(), Bin = 1 }, ct);

    /// <summary>The capture's state once <paramref name="done"/> holds of it.</summary>
    private static Task<PlanetaryStateDto> UntilStateAsync(TianWenNodeClient client, string what, Func<PlanetaryStateDto, bool> done, CancellationToken ct) =>
        UntilAsync<PlanetaryStateDto>(what, async token =>
        {
            var state = (await client.GetPlanetaryAsync(token)).Value;
            return (state is not null && done(state) ? state : null,
                state is null ? "none" : $"running {state.Running}, {state.FramesReceived} frames, {state.Masters} masters, {state.RoiWidth}x{state.RoiHeight}");
        }, ct);

    private static async Task<Image> FrameAsync(TianWenNodeClient client, string source, CancellationToken ct)
    {
        var frame = await client.GetLatestFrameAsync(source, after: null, new FrameReader(), ct);
        return frame.Image.ShouldNotBeNull($"{source}: {frame.Error}");
    }

    [Fact(Timeout = 90_000)]
    public async Task TheNodeStacksWhatItStreamsAndAClientWatchesTheLiveFrameAndTheMaster()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await PlanetaryNodeAsync(NodeRunWatchOptions.Default.DetachGrace, ct);
        await node.ActivateRigAsync(Camera, mount: null, ct);
        var client = ClientOf(node);
        await using var window = await NodeWindow.OpenAsync(node, outputHelper, ct);
        var pushed = new ConcurrentQueue<FrameAvailableDto>();
        window.Events.EventReceived += (_, e) =>
        {
            if (FrameAvailableDto.TryFromEvent(e, out var frame))
            {
                pushed.Enqueue(frame);
            }
        };

        var started = await client.StartPlanetaryAsync(Default, ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        var state = started.Value.ShouldNotBeNull();
        (state.OtaIndex, state.RoiWidth, state.RoiHeight, state.Running).ShouldBe((0, 640, 320, true));
        state.Camera.ShouldNotBeNullOrEmpty();

        var stacking = await UntilStateAsync(client, "a master to be stacked", s => s.Masters > 0, ct);
        stacking.StackedFrames.ShouldBeGreaterThan(0);
        (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().Run.ShouldNotBeNull().Kind.ShouldBe(NodeRunKind.Planetary);

        // The live frame is the camera's own, a Bayer mosaic at the window's size; the master is demosaiced at the same size.
        var live = await FrameAsync(client, FrameSources.PlanetaryLive, ct);
        (live.Width, live.Height, live.ChannelCount).ShouldBe((640, 320, 1));
        live.Release();
        var master = await FrameAsync(client, FrameSources.PlanetaryMaster, ct);
        (master.Width, master.Height, master.ChannelCount).ShouldBe((640, 320, 3));
        master.Release();
        await UntilAsync("FRAME-AVAILABLE for the live frame and the master", _ => ValueTask.FromResult((
            pushed.Any(f => f.Source == FrameSources.PlanetaryLive) && pushed.Any(f => f.Source == FrameSources.PlanetaryMaster),
            $"{pushed.Count} pushed")), ct);

        // While it runs the camera is the capture's, and the node's one run is too.
        var command = await CommandTheCameraAsync(client, ct);
        command.StatusCode.ShouldBe(409);
        command.Error.ShouldNotBeNull().ShouldContain(PlanetaryCapture.LeaseOwner);
        var again = await client.StartPlanetaryAsync(Default, ct);
        (again.StatusCode, again.Error).ShouldBe((409, "A planetary capture is running"));
        var darks = await client.StartDarkLibraryAsync(new DarkLibraryRequestDto { DeviceUri = Camera.DeviceUri.ToString(), ExposureSeconds = 1, Count = 1 }, ct);
        (darks.StatusCode, darks.Error).ShouldBe((409, "A planetary capture is running"));

        var stopped = await client.StopPlanetaryAsync(ct);
        stopped.IsSuccess.ShouldBeTrue(stopped.Error);
        var ended = stopped.Value.ShouldNotBeNull();
        ended.Running.ShouldBeFalse("a stop is answered once the capture has ended");
        ended.FailureReason.ShouldBeNull("a stop is how it ends, not a failure");
        (await CommandTheCameraAsync(client, ct)).IsSuccess.ShouldBeTrue("the camera is given back as the run ends");
        (await client.StopPlanetaryAsync(ct)).StatusCode.ShouldBe(404, "nothing is running to stop");
    }

    [Fact(Timeout = 90_000)]
    public async Task AControlReachesTheCameraAfterTheNextFrameAndANewWindowRestartsTheStackAtItsFraming()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await PlanetaryNodeAsync(NodeRunWatchOptions.Default.DetachGrace, ct);
        await node.ActivateRigAsync(Camera, mount: null, ct);
        var client = ClientOf(node);
        await using var window = await NodeWindow.OpenAsync(node, outputHelper, ct);
        (await client.StartPlanetaryAsync(Default, ct)).IsSuccess.ShouldBeTrue();
        await UntilStateAsync(client, "a master to be stacked", s => s.Masters > 0, ct);

        var half = await client.SetPlanetaryControlsAsync(new PlanetaryControlsDto { RoiWidth = 320 }, ct);
        (half.StatusCode, half.Error).ShouldBe((400, "A readout window is resized by its width and its height together"));
        var dark = await client.SetPlanetaryControlsAsync(new PlanetaryControlsDto { ExposureMs = 0 }, ct);
        (dark.StatusCode, dark.Error).ShouldBe((400, "A planetary capture needs a positive exposure"));

        // Neither size is on the camera's steps (a width in eights, a height in twos): the capture snaps what a client asks.
        var resized = await client.SetPlanetaryControlsAsync(new PlanetaryControlsDto { RoiWidth = 323, RoiHeight = 201, ExposureMs = 5 }, ct);
        resized.IsSuccess.ShouldBeTrue(resized.Error);
        var reframed = await UntilStateAsync(client, "frames at the new window", s => (s.RoiWidth, s.RoiHeight) == (320, 200), ct);
        await UntilStateAsync(client, "a master at the new window", s => s.Masters > reframed.Masters + 1, ct);
        var master = await FrameAsync(client, FrameSources.PlanetaryMaster, ct);
        (master.Width, master.Height).ShouldBe((320, 200), "the stack started again at the new framing");
        master.Release();

        (await client.StopPlanetaryAsync(ct)).IsSuccess.ShouldBeTrue();
        (await client.SetPlanetaryControlsAsync(new PlanetaryControlsDto { Gain = 1 }, ct)).StatusCode.ShouldBe(404, "nothing is running to adjust");
    }

    [Fact(Timeout = 90_000)]
    public async Task ACaptureNobodyWatchesStopsOnceTheGraceIsSpent()
    {
        var ct = TestContext.Current.CancellationToken;
        var grace = TimeSpan.FromSeconds(3);
        await using var node = await PlanetaryNodeAsync(grace, ct);
        await node.ActivateRigAsync(Camera, mount: null, ct);
        var client = ClientOf(node);
        await using var window = await NodeWindow.OpenAsync(node, outputHelper, ct);

        (await client.StartPlanetaryAsync(Default, ct)).IsSuccess.ShouldBeTrue();
        await UntilStateAsync(client, "frames to arrive", s => s.FramesReceived > 0, ct);

        window.Drawing = false;
        var frozeAt = DateTimeOffset.UtcNow;
        var ended = await UntilStateAsync(client, "the capture to stop", s => !s.Running, ct);
        (DateTimeOffset.UtcNow - frozeAt).ShouldBeGreaterThan(grace, "the grace is whole before it is stopped");
        ended.FailureReason.ShouldBeNull();
        var notes = (await client.GetNotificationsAsync(ct)).Value.ShouldNotBeNull();
        notes.ShouldContain(n => n.Message.StartsWith("The planetary capture stopped: no client has watched it"));
        (await CommandTheCameraAsync(client, ct)).IsSuccess.ShouldBeTrue("the camera is given back as the run ends");
    }

    [Fact(Timeout = 60_000)]
    public async Task ARefusedStartSaysWhyAndHoldsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await PlanetaryNodeAsync(NodeRunWatchOptions.Default.DetachGrace, ct);
        var client = ClientOf(node);

        var noProfile = await client.StartPlanetaryAsync(Default, ct);
        (noProfile.StatusCode, noProfile.Error).ShouldBe((409, "The node has no active profile to capture with"));

        await node.ActivateRigAsync(Camera, mount: null, ct);
        var dark = await client.StartPlanetaryAsync(new PlanetaryRequestDto { ExposureMs = 0 }, ct);
        (dark.StatusCode, dark.Error).ShouldBe((400, "A planetary capture needs a positive exposure"));
        var noOta = await client.StartPlanetaryAsync(new PlanetaryRequestDto { OtaIndex = 3 }, ct);
        (noOta.StatusCode, noOta.Error).ShouldBe((409, "The profile has no OTA #4"));

        // A cool-down ramp takes minutes, and the camera is its job's until it ends.
        var cooling = (await client.CoolCameraAsync(Camera.DeviceUri, -10, rampMinutes: null, ct)).Value.ShouldNotBeNull();
        var busy = await client.StartPlanetaryAsync(Default, ct);
        busy.StatusCode.ShouldBe(409);
        busy.Error.ShouldNotBeNull().ShouldContain(cooling.Id);
        (await client.CancelJobAsync(cooling.Id, ct)).IsSuccess.ShouldBeTrue();

        node.Node.IsRunning.ShouldBeFalse();
        node.App.Services.GetRequiredService<IDeviceHub>().TryGetLease(Camera.DeviceUri, out _).ShouldBeFalse("a refused start claimed nothing");
    }

    [Fact(Timeout = 60_000)]
    public async Task TheJournalRecordsAPlanetaryCaptureByItsKind()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Directory.CreateTempSubdirectory("twj").FullName, "node.journal");
        await using var node = await PlanetaryNodeAsync(NodeRunWatchOptions.Default.DetachGrace, ct,
            services => services.AddSingleton(new NodeJournalOptions(path, null, static () => null, TimeProvider.System)));
        await node.ActivateRigAsync(Camera, mount: null, ct);
        var client = ClientOf(node);
        await using var window = await NodeWindow.OpenAsync(node, outputHelper, ct);

        (await client.StartPlanetaryAsync(Default, ct)).IsSuccess.ShouldBeTrue();

        var journal = await UntilTheJournalAsync(path, static j => j.Run is not null, ct);
        journal.Run.ShouldNotBeNull().Kind.ShouldBe(NodeRunKind.Planetary, "the next node reports which run a crash interrupted");

        (await client.StopPlanetaryAsync(ct)).IsSuccess.ShouldBeTrue();
    }
}
