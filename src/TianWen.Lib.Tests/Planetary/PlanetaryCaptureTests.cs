using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Devices;
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
public class PlanetaryCaptureTests
{
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
}
