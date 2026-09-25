using System.Threading.Tasks;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Polar alignment and planetary capture claim what they drive, as a session and a flat run already did, and
/// the preview capture asks the claim rather than a session flag (P0c item 2 of
/// docs/plans/hardware-in-the-server.md, #788). Only <c>Session.RunAsync</c> and <c>RunFlatsOnlyAsync</c> took
/// a lease, and the gate is lease-only, so nothing stopped a jog or a second run from moving a mount polar was
/// rotating, or a capture from reconfiguring a camera planetary was streaming.
/// </summary>
public class RunClaimTests(ITestOutputHelper output)
{
    private const string PolarOwner = "polar alignment";
    private const string PlanetaryOwner = "planetary capture";

    [Fact(Timeout = 60_000)]
    public async Task WhilePolarAlignmentRunsItsMountAndCameraAreClaimed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiSignalHarness.StartAsync(output, ct);

        h.Post(new StartPolarAlignmentSignal(OtaIndex: 0));

        h.Claimed(h.MountUri).ShouldBeTrue("polar rotates the mount");
        h.Claimed(h.CameraUri).ShouldBeTrue("polar exposes on the camera");
        h.Claimed(h.FocuserUri).ShouldBeFalse("it only reads the focuser, for the frames' cards");

        h.MarkPending();
        h.Post(new JogMountSignal(GuideDirection.North, Arcsec: 10));
        h.ShouldHaveStartedNothing("a jog of the mount polar is rotating");
        h.ShouldHaveRefused(PolarOwner);
    }

    [Fact(Timeout = 60_000)]
    public async Task APolarRunGivesItsDevicesBackOnceItHasEnded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiSignalHarness.StartAsync(output, ct);
        h.Post(new StartPolarAlignmentSignal(OtaIndex: 0));

        h.Post(new CancelPolarAlignmentSignal());
        await h.UntilAsync(() => h.Contexts.Local.LiveSession.PolarRunEnded.IsCompleted, ct);

        h.Claimed(h.MountUri).ShouldBeFalse();
        h.Claimed(h.CameraUri).ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task APolarStartIsRefusedWhileAnotherRunHoldsItsMount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiSignalHarness.StartAsync(output, ct);
        h.Hub.TryAcquireLease(h.MountUri, "the imaging session", out var sessionClaim).ShouldBeTrue();

        h.Post(new StartPolarAlignmentSignal(OtaIndex: 0));

        h.Contexts.Local.LiveSession.PolarAlignmentCts.ShouldBeNull("polar never started");
        h.Claimed(h.CameraUri).ShouldBeFalse("a refused start leaves ownership as it found it");
        h.ShouldHaveRefused("the imaging session");
        sessionClaim.Dispose();
    }

    [Fact(Timeout = 60_000)]
    public async Task WhilePlanetaryCaptureStreamsItsCameraIsClaimedAndStoppingGivesItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiSignalHarness.StartAsync(output, ct);

        h.Post(new StartVideoCaptureSignal(OtaIndex: 0));

        h.PlanetaryCapture.IsCapturing.ShouldBeTrue("premise: the capture started");
        h.Claimed(h.CameraUri).ShouldBeTrue("planetary streams off the camera");
        h.Claimed(h.MountUri).ShouldBeFalse("its own nudges drive the mount, and would refuse themselves");

        h.MarkPending();
        h.Post(new TakePreviewSignal(OtaIndex: 0, ExposureSeconds: 1));
        h.ShouldHaveStartedNothing("an exposure on the camera planetary is streaming");
        h.ShouldHaveRefused(PlanetaryOwner);

        h.Post(new StopVideoCaptureSignal());
        await h.UntilAsync(() => !h.Claimed(h.CameraUri), ct);
    }

    /// <summary>
    /// The preview Capture asked whether a SESSION ran, and <c>IsRunning</c> is false during a flat run, so it
    /// exposed on the camera a flat run was metering.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task APreviewCaptureOnACameraARunHoldsIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiSignalHarness.StartAsync(output, ct);
        h.Hub.TryAcquireLease(h.CameraUri, "the flat run", out var flatClaim).ShouldBeTrue();

        h.Post(new TakePreviewSignal(OtaIndex: 0, ExposureSeconds: 1));

        h.ShouldHaveStartedNothing("an exposure on the camera the flat run is metering");
        h.ShouldHaveRefused("the flat run");
        flatClaim.Dispose();
    }

    /// <summary>
    /// Warm-and-disconnect ramped a claimed camera's cooler all the way up, and only then had its disconnect
    /// refused by the claim: a run's cooled camera warmed under it. Both warm-ups refuse before the ramp now.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWarmUpOfACameraARunHoldsIsRefusedBeforeItTouchesTheCooler(bool disconnect)
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var hub = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<IDeviceHub>(external.BuildServiceProvider());
        var device = new Devices.Fake.FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);
        await camera.SetCoolerOnAsync(true, ct);
        await camera.SetSetCCDTemperatureAsync(-10, ct);
        hub.TryAcquireLease(device.DeviceUri, "the imaging session", out var sessionClaim).ShouldBeTrue();

        var warmUp = disconnect
            ? hub.WarmAndDisconnectAsync(device.DeviceUri, external.TimeProvider, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, force: false, ct)
            : hub.WarmAndCoolerOffAsync(device.DeviceUri, external.TimeProvider, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ct);
        await Should.ThrowAsync<DeviceLeasedException>(warmUp.AsTask());

        (await camera.GetCoolerOnAsync(ct)).ShouldBeTrue("the run's camera stays cold");
        (await camera.GetSetCCDTemperatureAsync(ct)).ShouldBe(-10);
        sessionClaim.Dispose();
    }
}
