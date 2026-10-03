using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The planetary capture on its own (P5 part 5 of docs/plans/hardware-in-the-server.md, #934): its recenter's coarse
/// mount nudge. The capture claims only the camera, so a nudge asks the ownership gate over the mount first, and one
/// the gate refuses is never issued: a flat run or a device job holding the mount must not have it pulsed underneath.
/// The camera streams through the universal rapid-exposure fallback, which cannot pan its window, so every offset past
/// the deadband falls to the mount.
/// </summary>
[Collection("Session")]
public class PlanetaryCaptureTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The temporary folders this test made, deleted after it (#1197).</summary>
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static readonly Uri MountUri = new Uri("fake://mount/1");

    /// <summary>A 64 px frame with the disk centred 16 px right of the frame centre.</summary>
    private static Image OffCentreDisk()
    {
        const int n = 64;
        var a = new float[n, n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var dx = x - 48.0;
                var dy = y - 32.0;
                a[y, x] = (dx * dx) + (dy * dy) < 64.0 ? 0.6f : 0.03f;
            }
        }
        return Image.FromChannel(a, 1f, 0f);
    }

    private static ICameraDriver Camera()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult<Image?>(OffCentreDisk()));
        return camera;
    }

    private static (IMountDriver Mount, TaskCompletionSource<GuideDirection> Pulsed) Mount()
    {
        var pulsed = new TaskCompletionSource<GuideDirection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mount = Substitute.For<IMountDriver>();
        mount.Connected.Returns(true);
        mount.CanPulseGuide.Returns(true);
        mount.GetGuideRateRightAscensionAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(0.004));
        mount.GetGuideRateDeclinationAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(0.004));
        mount.StartPulseGuideAsync(Arg.Any<GuideDirection>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                pulsed.TrySetResult(call.Arg<GuideDirection>());
                return ValueTask.CompletedTask;
            });
        return (mount, pulsed);
    }

    /// <summary>A hub on which the mount is held by <paramref name="owner"/>, or by nobody when null.</summary>
    private static IDeviceHub Hub(string? owner)
    {
        var hub = Substitute.For<IDeviceHub>();
        hub.TryGetLease(MountUri, out Arg.Any<DeviceLease>()).Returns(call =>
        {
            call[1] = owner is null ? default : new DeviceLease(MountUri, owner);
            return owner is not null;
        });
        return hub;
    }

    private static async Task<PlanetaryCapture> CapturingAsync(IMountDriver mount, IDeviceHub hub, int frames, CancellationToken ct)
    {
        var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance);
        capture.AttachMount(mount, MountUri, hub, pixelScaleArcsec: 1.0);
        capture.ConfigureRecenter(auto: true, mountJog: true, deadbandPixels: 2, gain: 1.0);
        capture.ArmFrameGate();
        capture.Start(Camera(), new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).ShouldBeTrue();
        for (var i = 0; i < frames; i++)
        {
            var next = capture.WaitForNextFrameAsync(ct);
            capture.StepFrame();
            await next;
        }
        return capture;
    }

    [Fact(Timeout = 30_000)]
    public async Task ARecenterTheWindowCannotMakeNudgesTheMountWhenNothingHoldsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, pulsed) = Mount();

        await using var capture = await CapturingAsync(mount, Hub(owner: null), frames: 1, ct);

        capture.LastRecenterActuator.ShouldBe(RecenterActuator.Mount);
        // Its direction is the recenter's own (uncalibrated) sign, which PlanetaryRecenterControllerTests pins.
        await pulsed.Task.WaitAsync(ct);
    }

    [Fact(Timeout = 30_000)]
    public async Task ANudgeIsNeverIssuedWhileAnotherRunHoldsTheMount()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = Mount();

        await using var capture = await CapturingAsync(mount, Hub(owner: "flat run"), frames: 3, ct);

        capture.LastRecenterActuator.ShouldBe(RecenterActuator.Mount, "the recenter still wanted the mount");
        await mount.DidNotReceive().StartPulseGuideAsync(Arg.Any<GuideDirection>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A hub with the fake camera connected, and a profile whose one OTA has it.</summary>
    private async Task<(IDeviceHub Hub, ProfileData Profile, Uri Camera)> RigAsync(CancellationToken ct)
    {
        var hub = new FakeExternal(output).BuildServiceProvider().GetRequiredService<IDeviceHub>();
        var camera = new FakeDevice(DeviceType.Camera, 1);
        await hub.ConnectAsync(camera, ct);
        var profile = new ProfileData(NoneDevice.Instance.DeviceUri, NoneDevice.Instance.DeviceUri,
            [new OTAData("Test OTA", 400, camera.DeviceUri, null, null, null, null, null)]);
        return (hub, profile, camera.DeviceUri);
    }

    private static readonly PlanetaryCaptureRequest Request = new PlanetaryCaptureRequest(0, TimeSpan.FromMilliseconds(5), null, 640, 320);

    [Fact(Timeout = 30_000)]
    public async Task APreparedCaptureHoldsTheCameraStreamsNothingAndGivesItBackIfItNeverStarts()
    {
        // The node prepares as it is asked and starts once the run is its own; a start it loses to another run disposes
        // the capture, which must not keep the camera claimed.
        var ct = TestContext.Current.CancellationToken;
        var (hub, profile, camera) = await RigAsync(ct);
        var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance);

        capture.TryPrepare(Request, profile, hub, out var roi, out var refusal).ShouldBeTrue(refusal);

        roi.ShouldBe((640, 320));
        capture.IsCapturing.ShouldBeFalse("prepared is not started");
        capture.Camera.ShouldNotBeNull();
        hub.TryGetLease(camera, out var lease).ShouldBeTrue();
        lease.OwnerLabel.ShouldBe(PlanetaryCapture.LeaseOwner);
        capture.TryPrepare(Request, profile, hub, out _, out var twice).ShouldBeFalse();
        twice.ShouldBe("A planetary capture is already running");

        await capture.DisposeAsync();

        hub.TryGetLease(camera, out _).ShouldBeFalse("a capture that never started gives its camera back");
        capture.StartPrepared(ct).ShouldBeFalse("nothing is left to start");
    }

    [Fact(Timeout = 30_000)]
    public async Task ARecordingEndsWithTheCaptureThatFeedsIt()
    {
        // A host that stops its capture without disposing it (the GUI's Stop) must not leave a recording waiting for frames
        // that will never come: the file would stay unfinished, its header unwritten, until the host disposed it.
        var ct = TestContext.Current.CancellationToken;
        var (hub, profile, _) = await RigAsync(ct);
        await using var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance);
        capture.ArmFrameGate();
        capture.TryStart(Request, profile, hub, ct, out _, out var refusal).ShouldBeTrue(refusal);
        var path = Path.Combine(_folders.Create("twser").FullName, "capture.ser");
        capture.TryStartRecording(path, TimeSpan.FromHours(1), out var recording, out refusal).ShouldBeTrue(refusal);
        for (var i = 0; i < 2; i++)
        {
            var next = capture.WaitForNextFrameAsync(ct);
            capture.StepFrame();
            await next;
        }

        await capture.StopAsync(ct);
        await recording.Completion.WaitAsync(ct);

        (recording.IsRecording, recording.EndReason, recording.FramesWritten).ShouldBe((false, "the capture ended", 2));
    }

    [Fact(Timeout = 30_000)]
    public async Task ARecordingAskedForBeforeAPreparedCaptureStartsRecordsItsFrames()
    {
        // A node answers a start once the run is its own, and the run's task starts the loop a moment later: a recording asked
        // for in that gap was refused ("No planetary capture is running to record"), which failed a functional test on CI.
        var ct = TestContext.Current.CancellationToken;
        var (hub, profile, _) = await RigAsync(ct);
        await using var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance);
        capture.ArmFrameGate();
        capture.TryPrepare(Request, profile, hub, out _, out var refusal).ShouldBeTrue(refusal);
        var path = Path.Combine(_folders.Create("twser").FullName, "capture.ser");

        capture.IsCapturing.ShouldBeFalse("prepared is not started");
        capture.TryStartRecording(path, TimeSpan.FromHours(1), out var recording, out refusal).ShouldBeTrue(refusal);
        capture.StartPrepared(ct).ShouldBeTrue();
        for (var i = 0; i < 2; i++)
        {
            var next = capture.WaitForNextFrameAsync(ct);
            capture.StepFrame();
            await next;
        }

        await capture.StopAsync(ct);
        await recording.Completion.WaitAsync(ct);

        (recording.EndReason, recording.FramesWritten).ShouldBe(("the capture ended", 2));
    }

    [Fact(Timeout = 30_000)]
    public async Task ARecordingOnACaptureThatNeverStartsEndsAsItIsDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hub, profile, _) = await RigAsync(ct);
        var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance);
        capture.TryPrepare(Request, profile, hub, out _, out var refusal).ShouldBeTrue(refusal);
        var path = Path.Combine(_folders.Create("twser").FullName, "capture.ser");
        capture.TryStartRecording(path, TimeSpan.FromHours(1), out var recording, out refusal).ShouldBeTrue(refusal);

        await capture.DisposeAsync();
        await recording.Completion.WaitAsync(ct);

        (recording.IsRecording, recording.FramesWritten).ShouldBe((false, 0));
    }

    [Fact(Timeout = 30_000)]
    public async Task APreparedCaptureStartsOnTheTokenItIsGivenAndGivesTheCameraBackAsItEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hub, profile, camera) = await RigAsync(ct);
        await using var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance);
        capture.ArmFrameGate();
        capture.TryPrepare(Request, profile, hub, out _, out var refusal).ShouldBeTrue(refusal);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);

        capture.StartPrepared(run.Token).ShouldBeTrue();
        var next = capture.WaitForNextFrameAsync(ct);
        capture.StepFrame();
        await next;
        capture.FramesReceived.ShouldBe(1);

        await run.CancelAsync();
        await capture.WaitForNextFrameAsync(ct);
        capture.IsCapturing.ShouldBeFalse("the token it was started on ends it");
        hub.TryGetLease(camera, out _).ShouldBeFalse("the loop gives the camera back as it ends");
    }
}
