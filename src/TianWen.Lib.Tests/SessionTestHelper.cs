using System;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
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
        FocusDriftThreshold: 1.07f
    );

    public static readonly ScheduledObservation[] DefaultScheduledObservations =
    [
        new ScheduledObservation(
            new Target(6.75, 16.7, "M42", null),
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(30),
            AcrossMeridian: false,
            FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(120)),
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
        DateTimeOffset? now = null,
        int focalLength = 1000,
        string? mountPort = "LX200",
        CancellationToken cancellationToken = default)
    {
        var external = new FakeExternal(output, now: now ?? new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));

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
            focalLength,
            camera,
            Cover: null,
            focuser,
            new FocusDirection(PreferOutward: true, OutwardIsPositive: true),
            FilterWheel: null,
            Switches: null
        );

        var mountQuery = new NameValueCollection
        {
            { "latitude", "48.2" },
            { "longitude", "16.3" }
        };
        if (mountPort is not null)
        {
            mountQuery.Add("port", mountPort);
        }
        var mountDevice = new FakeDevice(DeviceType.Mount, 1, mountQuery);
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

        return new SessionTestContext(session, external, cameraDriver, focuserDriver, mount.Driver);
    }

    /// <summary>
    /// Creates a dual-OTA Session modelling a dual plate setup:
    /// OTA 1: OSC camera with fixed dual-band filter (no filter wheel).
    /// OTA 2: Mono camera with a 5-position filter wheel and focuser.
    /// Both OTAs share the same mount and guider.
    /// </summary>
    public static async Task<DualOTATestContext> CreateDualOTASessionAsync(
        ITestOutputHelper output,
        SessionConfiguration? configuration = null,
        ScheduledObservation[]? observations = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var external = new FakeExternal(output, now: now ?? new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));

        // OTA 1: OSC camera, no filter wheel (fixed L-Ultimate dual-band)
        var oscCameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var oscCamera = new Camera(oscCameraDevice, external);
        await oscCamera.Driver.ConnectAsync(cancellationToken);
        var oscCameraDriver = (FakeCameraDriver)oscCamera.Driver;
        oscCameraDriver.BinX = 1;
        oscCameraDriver.NumX = 512;
        oscCameraDriver.NumY = 512;

        // OTA 1 focuser
        var oscFocuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        var oscFocuser = new Focuser(oscFocuserDevice, external);
        await oscFocuser.Driver.ConnectAsync(cancellationToken);
        var oscFocuserDriver = (FakeFocuserDriver)oscFocuser.Driver;

        // Fixed L-Ultimate (Ha+OIII) dual-band filter in a manual holder
        var oscFilterDevice = new ManualFilterWheelDevice(Filter.HydrogenAlphaOxygenIII);
        var oscFilterWheel = new FilterWheel(oscFilterDevice, external);
        await oscFilterWheel.Driver.ConnectAsync(cancellationToken);

        var ota1 = new OTA(
            "Samyang 135 OSC",
            135,
            oscCamera,
            Cover: null,
            oscFocuser,
            new FocusDirection(PreferOutward: true, OutwardIsPositive: true),
            oscFilterWheel,
            Switches: null,
            Aperture: 68,
            OpticalDesign: OpticalDesign.Astrograph
        );

        // OTA 2: Mono camera + filter wheel + focuser
        var monoCameraDevice = new FakeDevice(DeviceType.Camera, 2);
        var monoCamera = new Camera(monoCameraDevice, external);
        await monoCamera.Driver.ConnectAsync(cancellationToken);
        var monoCameraDriver = (FakeCameraDriver)monoCamera.Driver;
        monoCameraDriver.BinX = 1;
        monoCameraDriver.NumX = 512;
        monoCameraDriver.NumY = 512;

        var monoFocuserDevice = new FakeDevice(DeviceType.Focuser, 2);
        var monoFocuser = new Focuser(monoFocuserDevice, external);
        await monoFocuser.Driver.ConnectAsync(cancellationToken);
        var monoFocuserDriver = (FakeFocuserDriver)monoFocuser.Driver;

        // 5-position filter wheel: L, SII, R, G, B with focus offsets
        var filterWheelDevice = new FakeDevice(DeviceType.FilterWheel, 1, new NameValueCollection
        {
            { "filter1", "Luminance" }, { "offset1", "0" },
            { "filter2", "SII" },       { "offset2", "25" },
            { "filter3", "Red" },       { "offset3", "20" },
            { "filter4", "Green" },     { "offset4", "0" },
            { "filter5", "Blue" },      { "offset5", "-15" }
        });
        var filterWheel = new FilterWheel(filterWheelDevice, external);
        await filterWheel.Driver.ConnectAsync(cancellationToken);
        var filterWheelDriver = (FakeFilterWheelDriver)filterWheel.Driver;

        var ota2 = new OTA(
            "Samyang 135 Mono",
            135,
            monoCamera,
            Cover: null,
            monoFocuser,
            new FocusDirection(PreferOutward: true, OutwardIsPositive: true),
            filterWheel,
            Switches: null,
            Aperture: 68,
            OpticalDesign: OpticalDesign.Astrograph
        );

        // Shared mount + guider
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
        await mount.Driver.SetUTCDateAsync(external.TimeProvider.GetUtcNow().UtcDateTime, cancellationToken);

        var setup = new Setup(mount, guider, new GuiderSetup(), [ota1, ota2]);
        var plateSolver = new FakePlateSolver();

        var config = configuration ?? DefaultConfiguration;
        var obs = observations ?? DefaultScheduledObservations;

        var session = new Session(setup, config, plateSolver, external, new ScheduledObservationTree(obs));

        return new DualOTATestContext(session, external, oscCameraDriver, monoCameraDriver, oscFocuserDriver, monoFocuserDriver, filterWheelDriver, mount.Driver);
    }

    /// <summary>
    /// Filter plan for the 5-slot L/SII/R/G/B wheel, ordered as altitude ladder:
    /// SII (narrowband, pos 1) → R (pos 2) → G (pos 3) → B (pos 4) → L (luminance, pos 0).
    /// </summary>
    public static readonly ImmutableArray<FilterExposure> FakeWheelLSIIRGBFilterPlan = FilterPlanBuilder.BuildAutoFilterPlan(
        [
            new InstalledFilter(Filter.Luminance),
            new InstalledFilter(Filter.SulphurII, +25),
            new InstalledFilter(Filter.Red, +20),
            new InstalledFilter(Filter.Green),
            new InstalledFilter(Filter.Blue, -15)
        ],
        broadbandExposure: TimeSpan.FromSeconds(120),
        narrowbandExposure: TimeSpan.FromSeconds(300));
}

internal record DualOTATestContext(
    Session Session,
    FakeExternal External,
    FakeCameraDriver OSCCamera,
    FakeCameraDriver MonoCamera,
    FakeFocuserDriver OSCFocuser,
    FakeFocuserDriver MonoFocuser,
    FakeFilterWheelDriver FilterWheel,
    IMountDriver Mount
)
{
    internal void CleanupOutputFolder()
    {
        if (Environment.GetEnvironmentVariable("TIANWEN_TESTS_DISABLE_CLEANUP") == "1")
        {
            return;
        }

        var outputDir = External.OutputFolder;
        if (!outputDir.Exists)
        {
            return;
        }

        var fitsFiles = outputDir.GetFiles("*.fits", SearchOption.AllDirectories);
        foreach (var file in fitsFiles.Skip(1))
        {
            file.Delete();
        }

        foreach (var dir in outputDir.GetDirectories("*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.FullName.Length))
        {
            if (dir.Exists && dir.GetFileSystemInfos().Length == 0)
            {
                dir.Delete();
            }
        }
    }
}

internal record SessionTestContext(
    Session Session,
    FakeExternal External,
    FakeCameraDriver Camera,
    FakeFocuserDriver Focuser,
    IMountDriver Mount
)
{
    /// <summary>
    /// Deletes the test output directory, keeping only the first FITS file as a sample.
    /// Call after assertions pass to avoid accumulating large test artifacts.
    /// </summary>
    internal void CleanupOutputFolder()
    {
        if (Environment.GetEnvironmentVariable("TIANWEN_TESTS_DISABLE_CLEANUP") == "1")
        {
            return;
        }

        var outputDir = External.OutputFolder;
        if (!outputDir.Exists)
        {
            return;
        }

        var fitsFiles = outputDir.GetFiles("*.fits", SearchOption.AllDirectories);
        foreach (var file in fitsFiles.Skip(1))
        {
            file.Delete();
        }

        // Remove empty subdirectories
        foreach (var dir in outputDir.GetDirectories("*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.FullName.Length))
        {
            if (dir.Exists && dir.GetFileSystemInfos().Length == 0)
            {
                dir.Delete();
            }
        }
    }
}
