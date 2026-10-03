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
/// A live view in the Preview mode as the node's run (#1111, P4 of docs/plans/live-session-preview.md), over the fake
/// camera: the node streams the whole sensor and keeps none of it, a client watches the frames at <c>/frames/live</c>,
/// exposure and gain reach the camera after the next frame, the camera is the live view's while it runs and given back as
/// it ends, and it stops once no client has been present for the detach grace.
/// </summary>
[Collection("Hosting")]
public class NodeLiveViewTests(ITestOutputHelper outputHelper) : IDisposable
{
    /// <summary>The temporary folders this test made, deleted after it (#1197).</summary>
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static readonly FakeDevice Camera = new FakeDevice(DeviceType.Camera, 1);

    // Binned 4, as a Preview may be: the fake's whole sensor is 4144 x 2822, slow to render a frame of in a Debug build.
    private const short Bin = 4;

    private static readonly LiveViewRequestDto Default = new LiveViewRequestDto { ExposureMs = 10, Binning = Bin };

    private Task<NodeHarness> LiveViewNodeAsync(TimeSpan grace, CancellationToken ct, Action<IServiceCollection>? configure = null) =>
        NodeHarness.StartAsync(outputHelper, ct, services =>
        {
            services.AddSingleton(new NodeRunWatchOptions(grace, TimeSpan.FromMilliseconds(100)));
            configure?.Invoke(services);
        });

    private static TianWenNodeClient ClientOf(NodeHarness node) => new TianWenNodeClient(node.Client);

    private static Task<NodeResult<CameraSettingsDto>> CommandTheCameraAsync(TianWenNodeClient client, CancellationToken ct) =>
        client.SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = Camera.DeviceUri.ToString(), Bin = 1 }, ct);

    private static ICameraDriver CameraOf(NodeHarness node)
        => node.App.Services.GetRequiredService<IDeviceHub>().TryGetConnectedDriver<ICameraDriver>(Camera.DeviceUri, out var camera)
            ? camera
            : throw new InvalidOperationException("The fake camera is not connected");

    /// <summary>The live view's state once <paramref name="done"/> holds of it.</summary>
    private static Task<LiveViewStateDto> UntilStateAsync(TianWenNodeClient client, string what, Func<LiveViewStateDto, bool> done, CancellationToken ct) =>
        UntilAsync<LiveViewStateDto>(what, async token =>
        {
            var state = (await client.GetLiveViewAsync(token)).Value;
            return (state is not null && done(state) ? state : null,
                state is null ? "none" : $"running {state.Running}, {state.FramesReceived} frames, {state.Width}x{state.Height}");
        }, ct);

    [Fact(Timeout = 90_000)]
    public async Task TheNodeStreamsTheWholeSensorKeepsNoneOfItAndAClientWatchesTheFrames()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await LiveViewNodeAsync(NodeRunWatchOptions.Default.DetachGrace, ct);
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
        var camera = CameraOf(node);
        // The whole binned sensor, snapped to the camera's window rule (the fake's is ZWO's, a width a multiple of 8).
        var snapped = camera.RoiConstraints.Snap(new RoiRect(0, 0, camera.CameraXSize / Bin, camera.CameraYSize / Bin));
        var sensor = (snapped.Width, snapped.Height);

        var started = await client.StartLiveViewAsync(Default, ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        var state = started.Value.ShouldNotBeNull();
        (state.OtaIndex, state.Running).ShouldBe((0, true));
        state.Camera.ShouldNotBeNullOrEmpty();

        var streaming = await UntilStateAsync(client, "frames to arrive", s => s.FramesReceived > 0, ct);
        (streaming.Width, streaming.Height).ShouldBe(sensor, "a live view is the whole sensor, at the binning asked");
        (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().Run.ShouldNotBeNull().Kind.ShouldBe(NodeRunKind.LiveView);

        // The frame is the camera's own, at the sensor's size; and the run keeps no ring of frames, as a stack would need.
        var frame = await client.GetLatestFrameAsync(FrameSources.LiveView, after: null, new FrameReader(), ct);
        var live = frame.Image.ShouldNotBeNull(frame.Error);
        (live.Width, live.Height).ShouldBe(sensor);
        live.Release();
        node.Node.CurrentRun.ShouldBeOfType<NodeLiveViewRun>().Capture.Stream.ShouldBeNull("a live view keeps no frames");
        await UntilAsync("FRAME-AVAILABLE for the live frame", _ => ValueTask.FromResult((
            pushed.Any(f => f.Source == FrameSources.LiveView), $"{pushed.Count} pushed")), ct);

        // While it runs the camera is the live view's, and the node's one run is too.
        var command = await CommandTheCameraAsync(client, ct);
        command.StatusCode.ShouldBe(409);
        command.Error.ShouldNotBeNull().ShouldContain(PlanetaryCapture.LiveViewLeaseOwner);
        var again = await client.StartLiveViewAsync(Default, ct);
        (again.StatusCode, again.Error).ShouldBe((409, "A live view is running"));
        var planetary = await client.StartPlanetaryAsync(new PlanetaryRequestDto(), ct);
        (planetary.StatusCode, planetary.Error).ShouldBe((409, "A live view is running"));

        var stopped = await client.StopLiveViewAsync(ct);
        stopped.IsSuccess.ShouldBeTrue(stopped.Error);
        var ended = stopped.Value.ShouldNotBeNull();
        ended.Running.ShouldBeFalse("a stop is answered once the live view has ended");
        ended.FailureReason.ShouldBeNull("a stop is how it ends, not a failure");
        (await CommandTheCameraAsync(client, ct)).IsSuccess.ShouldBeTrue("the camera is given back as the run ends");
        (await client.StopLiveViewAsync(ct)).StatusCode.ShouldBe(404, "nothing is running to stop");
    }

    [Fact(Timeout = 90_000)]
    public async Task AGainReachesTheCameraAfterTheNextFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await LiveViewNodeAsync(NodeRunWatchOptions.Default.DetachGrace, ct);
        await node.ActivateRigAsync(Camera, mount: null, ct);
        var client = ClientOf(node);
        await using var window = await NodeWindow.OpenAsync(node, outputHelper, ct);

        (await client.SetLiveViewControlsAsync(new LiveViewControlsDto { Gain = 1 }, ct)).StatusCode.ShouldBe(404, "no live view to adjust");
        (await client.StartLiveViewAsync(Default, ct)).IsSuccess.ShouldBeTrue();
        await UntilStateAsync(client, "frames to arrive", s => s.FramesReceived > 0, ct);

        var dark = await client.SetLiveViewControlsAsync(new LiveViewControlsDto { ExposureMs = 0 }, ct);
        (dark.StatusCode, dark.Error).ShouldBe((400, "A live view needs a positive exposure"));
        var noStep = await client.SetLiveViewControlsAsync(new LiveViewControlsDto { LensStep = (LensFocusStep)4 }, ct);
        (noStep.StatusCode, noStep.Error).ShouldBe((400, "A lens step is -3 to -1 (Near) or 1 to 3 (Far)"));
        (await client.GetLiveViewAsync(ct)).Value.ShouldNotBeNull().CanDriveLens
            .ShouldBeFalse("an astro camera has no lens of its own to drive, so no lens buttons are offered");

        var camera = CameraOf(node);
        var gain = await camera.GetGainAsync(ct) == camera.GainMax ? camera.GainMin : camera.GainMax;
        var set = await client.SetLiveViewControlsAsync(new LiveViewControlsDto { Gain = gain, ExposureMs = 20 }, ct);
        set.IsSuccess.ShouldBeTrue(set.Error);
        await UntilAsync($"the camera's gain to be {gain}", async token =>
        {
            var now = await camera.GetGainAsync(token);
            return (now == gain, $"gain {now}");
        }, ct);

        (await client.StopLiveViewAsync(ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 90_000)]
    public async Task ALiveViewNobodyWatchesStopsOnceTheGraceIsSpent()
    {
        var ct = TestContext.Current.CancellationToken;
        var grace = TimeSpan.FromSeconds(3);
        await using var node = await LiveViewNodeAsync(grace, ct);
        await node.ActivateRigAsync(Camera, mount: null, ct);
        var client = ClientOf(node);
        await using var window = await NodeWindow.OpenAsync(node, outputHelper, ct);

        (await client.StartLiveViewAsync(Default, ct)).IsSuccess.ShouldBeTrue();
        await UntilStateAsync(client, "frames to arrive", s => s.FramesReceived > 0, ct);

        window.Drawing = false;
        var frozeAt = DateTimeOffset.UtcNow;
        var ended = await UntilStateAsync(client, "the live view to stop", s => !s.Running, ct);
        (DateTimeOffset.UtcNow - frozeAt).ShouldBeGreaterThan(grace, "the grace is whole before it is stopped");
        ended.FailureReason.ShouldBeNull();
        var notes = (await client.GetNotificationsAsync(ct)).Value.ShouldNotBeNull();
        notes.ShouldContain(n => n.Message.StartsWith("The live view stopped: no client has watched it"));
        (await CommandTheCameraAsync(client, ct)).IsSuccess.ShouldBeTrue("the camera is given back as the run ends");
    }

    [Fact(Timeout = 60_000)]
    public async Task ARefusedStartSaysWhyAndHoldsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await LiveViewNodeAsync(NodeRunWatchOptions.Default.DetachGrace, ct);
        var client = ClientOf(node);

        var noProfile = await client.StartLiveViewAsync(Default, ct);
        (noProfile.StatusCode, noProfile.Error).ShouldBe((409, "The node has no active profile to show a live view of"));

        await node.ActivateRigAsync(Camera, mount: null, ct);
        var dark = await client.StartLiveViewAsync(new LiveViewRequestDto { ExposureMs = 0 }, ct);
        (dark.StatusCode, dark.Error).ShouldBe((400, "A live view needs a positive exposure"));
        var unbinned = await client.StartLiveViewAsync(new LiveViewRequestDto { Binning = 0 }, ct);
        (unbinned.StatusCode, unbinned.Error).ShouldBe((400, "A binning is 1 or more"));
        var noOta = await client.StartLiveViewAsync(new LiveViewRequestDto { OtaIndex = 3 }, ct);
        (noOta.StatusCode, noOta.Error).ShouldBe((409, "The profile has no OTA #4"));

        // A cool-down ramp takes minutes, and the camera is its job's until it ends.
        var cooling = (await client.CoolCameraAsync(Camera.DeviceUri, -10, rampMinutes: null, ct)).Value.ShouldNotBeNull();
        var busy = await client.StartLiveViewAsync(Default, ct);
        busy.StatusCode.ShouldBe(409);
        busy.Error.ShouldNotBeNull().ShouldContain(cooling.Id);
        (await client.CancelJobAsync(cooling.Id, ct)).IsSuccess.ShouldBeTrue();

        node.Node.IsRunning.ShouldBeFalse();
        node.App.Services.GetRequiredService<IDeviceHub>().TryGetLease(Camera.DeviceUri, out _).ShouldBeFalse("a refused start claimed nothing");
    }

    [Fact(Timeout = 60_000)]
    public async Task TheJournalRecordsALiveViewByItsKind()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_folders.Create("twj").FullName, "node.journal");
        await using var node = await LiveViewNodeAsync(NodeRunWatchOptions.Default.DetachGrace, ct,
            services => services.AddSingleton(new NodeJournalOptions(path, null, static () => null, TimeProvider.System)));
        await node.ActivateRigAsync(Camera, mount: null, ct);
        var client = ClientOf(node);
        await using var window = await NodeWindow.OpenAsync(node, outputHelper, ct);

        (await client.StartLiveViewAsync(Default, ct)).IsSuccess.ShouldBeTrue();

        var journal = await UntilTheJournalAsync(path, static j => j.Run is not null, ct);
        journal.Run.ShouldNotBeNull().Kind.ShouldBe(NodeRunKind.LiveView, "the next node reports which run a crash interrupted");

        (await client.StopLiveViewAsync(ct)).IsSuccess.ShouldBeTrue();
    }
}
