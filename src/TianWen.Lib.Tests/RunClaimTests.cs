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

    [Fact(Timeout = 60_000)]
    public async Task WhilePlanetaryCaptureStreamsItsCameraIsClaimedAndStoppingGivesItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiSignalHarness.StartAsync(output, ct);

        h.Post(new StartVideoCaptureSignal(OtaIndex: 0));

        h.PlanetaryCapture.IsCapturing.ShouldBeTrue("premise: the capture started");
        h.Claimed(h.CameraUri).ShouldBeTrue("planetary streams off the camera");
        h.Claimed(h.MountUri).ShouldBeFalse("its own nudges drive the mount, and would refuse themselves");

        h.Post(new StopVideoCaptureSignal());
        await h.UntilAsync(() => !h.Claimed(h.CameraUri), ct);
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
