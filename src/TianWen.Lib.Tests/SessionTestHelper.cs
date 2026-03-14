using System;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Shared helper for creating minimal Session instances with fake devices for testing.
/// Used by SessionAutoFocusTests, SessionCoolingTests, and other Session test classes.
/// </summary>
internal static class SessionTestHelper
{
    public static readonly SessionConfiguration DefaultConfiguration = new SessionConfiguration(
        SetpointCCDTemperature: new SetpointTemp(-10, SetpointTempKind.Normal),
        CooldownRampInterval: TimeSpan.FromSeconds(1),
        WarmupRampInterval: TimeSpan.FromSeconds(1),
        MinHeightAboveHorizon: 20,
        DitherPixel: 1.5,
        SettlePixel: 0.3,
        DitherEveryNthFrame: 5,
        SettleTime: TimeSpan.FromSeconds(3),
        GuidingTries: 3,
        AutoFocusRange: 200,
        AutoFocusStepCount: 9,
        FocusDriftThreshold: 1.3f
    );

    public static readonly ScheduledObservation[] DefaultScheduledObservations =
    [
        new ScheduledObservation(
            new Target(6.75, 16.7, "M42", null),
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(30),
            AcrossMeridian: false,
            SubExposure: TimeSpan.FromSeconds(120),
            Gain: 0,
            Offset: 0
        )
    ];

    /// <summary>
    /// Creates a minimal Session with fake devices suitable for Session integration tests.
    /// Camera, focuser, mount, and guider are connected and ready.
    /// </summary>
    public static async Task<SessionTestContext> CreateSessionAsync(
        ITestOutputHelper output,
        SessionConfiguration? configuration = null,
        ScheduledObservation[]? observations = null,
        CancellationToken cancellationToken = default)
    {
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));

        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        var camera = new Camera(cameraDevice, external);
        var focuser = new Focuser(focuserDevice, external);

        await camera.Driver.ConnectAsync(cancellationToken);
        await focuser.Driver.ConnectAsync(cancellationToken);

        var cameraDriver = (FakeCameraDriver)camera.Driver;
        var focuserDriver = (FakeFocuserDriver)focuser.Driver;

        cameraDriver.BinX = 1;
        cameraDriver.NumX = 512;
        cameraDriver.NumY = 512;

        var ota = new OTA(
            "Test Telescope",
            1000,
            camera,
            Cover: null,
            focuser,
            new FocusDirection(PreferOutward: true, OutwardIsPositive: true),
            FilterWheel: null,
            Switches: null
        );

        var mountDevice = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "port", "LX200" },
            { "latitude", "48.2" },
            { "longitude", "16.3" }
        });
        var guiderDevice = new FakeDevice(DeviceType.Guider, 1);
        var mount = new Mount(mountDevice, external);
        var guider = new Guider(guiderDevice, external);

        await mount.Driver.ConnectAsync(cancellationToken);
        await guider.Driver.ConnectAsync(cancellationToken);
        await ((FakeGuider)guider.Driver).ConnectEquipmentAsync(cancellationToken);

        // Set UTC date on mount so TryGetTransformAsync works
        await mount.Driver.SetUTCDateAsync(external.TimeProvider.GetUtcNow().UtcDateTime, cancellationToken);

        var setup = new Setup(mount, guider, new GuiderSetup(), [ota]);
        var plateSolver = new FakePlateSolver();

        var config = configuration ?? DefaultConfiguration;
        var obs = observations ?? DefaultScheduledObservations;

        var session = new Session(setup, config, plateSolver, external, new ScheduledObservationTree(obs));
        var mountDriver = (FakeMeadeLX200ProtocolMountDriver)mount.Driver;

        return new SessionTestContext(session, external, cameraDriver, focuserDriver, mountDriver);
    }
}

internal record SessionTestContext(
    Session Session,
    FakeExternal External,
    FakeCameraDriver Camera,
    FakeFocuserDriver Focuser,
    FakeMeadeLX200ProtocolMountDriver Mount
);
