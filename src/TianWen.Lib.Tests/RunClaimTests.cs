using System.Threading.Tasks;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A run's claim holds against the hub's own device operations (P0c item 2 of docs/plans/hardware-in-the-server.md, #788).
/// Which runs claim what, and that the GUI's runs claim on its node, is the node's to answer and is tested there
/// (<c>NodePlanetaryTests</c>, <c>NodePolarAlignmentTests</c>, and the GUI's own <c>RunsThroughTheNodeTests</c> and
/// <c>PlanetaryThroughTheNodeTests</c> in the functional suite).
/// </summary>
public class RunClaimTests(ITestOutputHelper output)
{
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
