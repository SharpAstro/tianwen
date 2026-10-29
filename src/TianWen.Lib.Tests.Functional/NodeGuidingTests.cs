using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Devices.OpenPHD2;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Calibrating and guiding off the Guider tab's loop, outside a session (#1915, docs/plans/live-session-preview.md, P17): a
/// run the node owns, which claims the mount and the guide camera, replaces the guide-camera loop keeping its star and
/// exposure, reports the calibration and the guide steps, and puts the loop back as it ends. Against a fake SkyWatcher mount
/// and the fake guide camera, which the mount's pulses move.
/// </summary>
[Collection("Hosting")]
public class NodeGuidingTests(ITestOutputHelper outputHelper)
{
    private static readonly FakeDevice Camera = new FakeDevice(DeviceType.Camera, 1);

    // The URI names it GuideCam, which is what couples the fake's star field to the mount's pulses.
    private static readonly FakeDevice GuideCamera = new FakeDevice(new Uri("camera://FakeDevice/FakeGuideCam1#Fake Guide Cam"));

    private static readonly FakeDevice Mount = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
    {
        { "port", "SkyWatcher" },
        { "latitude", "48.2" },
        { "longitude", "16.3" },
    });

    // Pulses go to the mount, which the fake guide camera follows.
    private static readonly Uri Guider = new BuiltInGuiderDevice(new Uri("guider://BuiltInGuiderDevice/builtin?pulseGuideSource=Mount#Built-in Guider")).DeviceUri;

    // Binned 2 and a short exposure: the fake's whole guide sensor is slow to render at full resolution.
    private static readonly LiveViewRequestDto Loop = new LiveViewRequestDto { CameraRole = LiveViewCamera.Guider, ExposureMs = 100, Binning = 2 };

    private static TianWenNodeClient ClientOf(NodeHarness node) => new TianWenNodeClient(node.Client);

    private async Task<NodeHarness> StartRigAsync(CancellationToken ct, Uri? guider = null, bool withGuideCamera = true)
    {
        var node = await NodeHarness.StartAsync(outputHelper, ct, configure: services => services.AddBuiltInGuider());
        await node.ActivateRigAsync(Camera, Mount, ct, guideCamera: withGuideCamera ? GuideCamera : null, guider: guider ?? Guider);

        // Tracking, centred away from the pole, as the guider's own end-to-end test sets the mount up.
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        hub.TryGetConnectedDriver<IMountDriver>(Mount.DeviceUri, out var mount).ShouldBeTrue();
        await mount.SetSiteLatitudeAsync(48.2, ct);
        await mount.SetSiteLongitudeAsync(16.3, ct);
        await mount.SetTrackingAsync(true, ct);
        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        // A 130 mm guide scope: the fake renders the star field the mount's pulses move only for a camera with a focal length.
        if (withGuideCamera)
        {
            hub.TryGetConnectedDriver<ICameraDriver>(GuideCamera.DeviceUri, out var guideCamera).ShouldBeTrue();
            guideCamera.FocalLength = 130;
        }
        return node;
    }

    private static Task<GuidingStateDto> UntilGuidingAsync(TianWenNodeClient client, string what, Func<GuidingStateDto, bool> done, CancellationToken ct) =>
        UntilAsync<GuidingStateDto>(what, async token =>
        {
            var state = (await client.GetGuidingAsync(token)).Value;
            return (state is not null && done(state) ? state : null,
                state is null ? "none" : $"running {state.Running}, phase {state.Phase}, {state.Guider?.RecentSteps.Length ?? 0} steps, failure {state.FailureReason}");
        }, ct);

    private static Task<LiveViewStateDto> UntilTheLoopAsync(TianWenNodeClient client, string what, Func<LiveViewStateDto, bool> done, CancellationToken ct) =>
        UntilAsync<LiveViewStateDto>(what, async token =>
        {
            var state = (await client.GetLiveViewAsync(token)).Value;
            return (state is not null && done(state) ? state : null,
                state is null ? "none" : $"{state.CameraRole} running {state.Running}, {state.FramesReceived} frames");
        }, ct);

    // The star the tab would have picked on the looped frame: the same readout the Guider tab's click and Auto use.
    private static async Task<(double X, double Y)> PickAStarAsync(TianWenNodeClient client, CancellationToken ct)
    {
        var frame = await UntilAsync<Image>("a guide frame", async token =>
        {
            var latest = await client.GetLatestFrameAsync(FrameSources.Guider, after: null, new FrameReader(), token);
            return (latest.Image, latest.Error ?? "no frame");
        }, ct);
        try
        {
            var star = GuideStarReadout.Measure(frame.GetChannel(0).Data, 1f);
            star.ShouldNotBeNull("the looped guide frame holds a star to choose");
            return (star.Value.X, star.Value.Y);
        }
        finally
        {
            frame.Release();
        }
    }

    private async Task<(TianWenNodeClient Client, NodeHarness Node)> LoopAsync(CancellationToken ct, Uri? guider = null)
    {
        var node = await StartRigAsync(ct, guider);
        var client = ClientOf(node);
        var started = await client.StartLiveViewAsync(Loop, ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        await UntilTheLoopAsync(client, "guide frames to arrive", s => s.FramesReceived > 0, ct);
        return (client, node);
    }

    [Fact(Timeout = 240_000)]
    public async Task NodeGuidingCalibratesOnDemandAndReportsTheResultThenTheLoopComesBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, node) = await LoopAsync(ct);
        await using var rig = node;
        var star = await PickAStarAsync(client, ct);

        var started = await client.CalibrateGuiderAsync(new GuidingRequestDto { StarX = star.X, StarY = star.Y }, ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        (started.Value.ShouldNotBeNull().Mode, started.Value.Running).ShouldBe((GuideRunMode.Calibrate, true));

        // The run holds the mount and the guide camera for its life, and the loop it replaced has let go of the camera.
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        hub.TryGetLease(Mount.DeviceUri, out var held).ShouldBeTrue();
        held.OwnerLabel.ShouldBe("guiding");
        hub.TryGetLease(GuideCamera.DeviceUri, out _).ShouldBeTrue();

        var done = await UntilGuidingAsync(client, "the calibration to end", s => !s.Running, ct);
        done.FailureReason.ShouldBeNull();
        var calibration = done.Calibration.ShouldNotBeNull("a calibration on demand reports what it measured");
        calibration.RaRateArcsecPerSec.ShouldNotBeNull().ShouldBeGreaterThan(0);
        calibration.DecRateArcsecPerSec.ShouldNotBeNull().ShouldBeGreaterThan(0);
        calibration.RaAngleDeg.ShouldNotBeNull();
        calibration.DecAngleDeg.ShouldNotBeNull();
        calibration.OrthogonalityErrorDeg.ShouldNotBeNull().ShouldBeLessThan(30);
        calibration.Overlay.ShouldNotBeNull().RaSteps.ShouldNotBeEmpty();

        // The loop is back, on the guide camera, and the node still remembers the result beside it.
        await UntilTheLoopAsync(client, "the loop to come back", s => s.Running && s.CameraRole == LiveViewCamera.Guider, ct);
        hub.TryGetLease(Mount.DeviceUri, out _).ShouldBeFalse("the run gave the mount back");
        (await client.GetGuidingAsync(ct)).Value.ShouldNotBeNull().Calibration.ShouldNotBeNull();
    }

    [Fact(Timeout = 300_000)]
    public async Task NodeGuidingGuidesFromTheLoopOnTheChosenStarAndProducesSamples()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, node) = await LoopAsync(ct);
        await using var rig = node;
        var star = await PickAStarAsync(client, ct);

        var started = await client.StartGuidingAsync(new GuidingRequestDto { StarX = star.X, StarY = star.Y, SettleTimeSeconds = 2 }, ct);
        started.IsSuccess.ShouldBeTrue(started.Error);

        // Guiding follows its calibration, and produces the samples the graph and the RMS are drawn from.
        var guiding = await UntilGuidingAsync(client, "guide samples",
            s => s.Running && s.Phase is GuidingPhase.Settling or GuidingPhase.Guiding && s.Guider?.RecentSteps.Length > 2, ct);
        guiding.Mode.ShouldBe(GuideRunMode.Guide);
        guiding.Calibration.ShouldNotBeNull("a guide run with no calibration calibrates first");

        // The lock is on the star that was chosen, never another of the field: a swap is the bug this keeps out.
        var locked = guiding.Guider.ShouldNotBeNull();
        var dx = locked.GuideStarX.ShouldNotBeNull() - star.X;
        var dy = locked.GuideStarY.ShouldNotBeNull() - star.Y;
        Math.Sqrt(dx * dx + dy * dy).ShouldBeLessThan(GuideStarReadout.PickRadius, "the guided star is the chosen one");
        locked.RecentSteps.Any(step => step.RaError is not null && step.DecError is not null).ShouldBeTrue();

        // The guide frames are served as a session's are.
        (await client.GetLatestFrameAsync(FrameSources.Guider, after: null, new FrameReader(), ct)).Image.ShouldNotBeNull().Release();

        // A stop ends the run and returns to looping; guiding's calibration stays.
        var stopped = await client.StopGuidingAsync(ct);
        stopped.IsSuccess.ShouldBeTrue(stopped.Error);
        stopped.Value.ShouldNotBeNull().Running.ShouldBeFalse();
        await UntilTheLoopAsync(client, "the loop to come back", s => s.Running && s.CameraRole == LiveViewCamera.Guider, ct);
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        hub.TryGetLease(Mount.DeviceUri, out _).ShouldBeFalse();
        hub.TryGetLease(GuideCamera.DeviceUri, out var camera).ShouldBeTrue("the loop holds the guide camera again");
        camera.OwnerLabel.ShouldNotBe("guiding");
    }

    [Fact(Timeout = 120_000)]
    public async Task NodeGuidingStopReturnsToLoopingAndASessionStartIsRefusedWhileItGoesOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, node) = await LoopAsync(ct);
        await using var rig = node;

        var started = await client.StartGuidingAsync(new GuidingRequestDto(), ct);
        started.IsSuccess.ShouldBeTrue(started.Error);

        // One run at a time: a session cannot start over it, and the refusal names the run; neither can a second guiding run.
        var session = await client.StartSessionAsync(null, null, ct);
        (session.StatusCode, session.Error).ShouldBe((409, "Guiding is running"));
        var again = await client.CalibrateGuiderAsync(new GuidingRequestDto(), ct);
        (again.StatusCode, again.Error).ShouldBe((409, "Guiding is running"));
        (await client.GetNodeAsync(ct)).Value.ShouldNotBeNull().Run.ShouldNotBeNull().Kind.ShouldBe(NodeRunKind.Guiding);

        var stopped = await client.StopGuidingAsync(ct);
        stopped.IsSuccess.ShouldBeTrue(stopped.Error);
        await UntilTheLoopAsync(client, "the loop to come back", s => s.Running && s.CameraRole == LiveViewCamera.Guider, ct);

        // With no guiding run, a stop says so.
        (await client.StopGuidingAsync(ct)).StatusCode.ShouldBe(404);
    }

    [Fact(Timeout = 90_000)]
    public async Task NodeGuidingRefusesInWordsWhatItCannotRunAndAClaimedMountLeavesTheLoopGoing()
    {
        var ct = TestContext.Current.CancellationToken;

        // PHD2 owns its camera and chooses its star.
        var phd2 = new OpenPHD2GuiderDevice(DeviceType.Guider, "localhost/1", "PHD2").DeviceUri;
        await using (var withPhd2 = await StartRigAsync(ct, guider: phd2))
        {
            var refused = await ClientOf(withPhd2).StartGuidingAsync(new GuidingRequestDto(), ct);
            (refused.StatusCode, refused.Error).ShouldBe((409, "PHD2 owns the guide camera and chooses its star, so there is nothing to calibrate or guide here: do it in PHD2"));
        }

        // A profile with no guide camera has nothing to guide with.
        await using (var noCamera = await StartRigAsync(ct, withGuideCamera: false))
        {
            var refused = await ClientOf(noCamera).CalibrateGuiderAsync(new GuidingRequestDto(), ct);
            (refused.StatusCode, refused.Error).ShouldBe((409, "The profile has no guide camera to guide with"));
        }

        // A start the mount's claim refuses names the holder, and the loop that was stopped to make room goes back.
        var (client, node) = await LoopAsync(ct);
        await using var rig = node;
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        hub.TryAcquireLease(Mount.DeviceUri, "polar alignment", out var lease).ShouldBeTrue();
        using (lease)
        {
            var refused = await client.StartGuidingAsync(new GuidingRequestDto(), ct);
            refused.StatusCode.ShouldBe(409);
            refused.Error.ShouldContain("polar alignment");
        }
        await UntilTheLoopAsync(client, "the loop to come back", s => s.Running && s.CameraRole == LiveViewCamera.Guider && s.FramesReceived > 0, ct);

        // Malformed requests are refused before anything is touched.
        var half = await client.StartGuidingAsync(new GuidingRequestDto { StarX = 10 }, ct);
        (half.StatusCode, half.Error).ShouldBe((400, "A chosen star needs both its X and its Y"));
        (await client.GetLiveViewAsync(ct)).Value.ShouldNotBeNull().Running.ShouldBeTrue("a malformed request left the loop alone");
    }
}
