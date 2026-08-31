using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Astrometry.PlateSolve;
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
        CooldownRampInterval: TimeSpan.FromSeconds(60),
        WarmupRampInterval: TimeSpan.FromSeconds(60),
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
        string? mountPort = null,
        double latitude = 48.2,
        double longitude = 16.3,
        bool withCoverCalibrator = false,
        bool withManualCover = false,
        bool withFilterWheel = false,
        Func<IServiceProvider, Cover>? coverFactory = null,
        MountLimitConfiguration? mountLimits = null,
        IPlateSolver? plateSolverOverride = null,
        bool coupleCameraToMount = true,
        bool withCatalogStarField = false,
        CancellationToken cancellationToken = default)
    {
        var timeProvider = new FakeTimeProviderWrapper(now ?? new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        if (withCatalogStarField)
        {
            // FakeExternal THROWS from GetCelestialObjectDBAsync unless this is set, and
            // FakeCameraDriver swallows that failure and renders a RANDOM star field instead. A random
            // field cannot plate-solve against a real catalog by construction, so anything that solves
            // needs this. Shared process-wide, so the Tycho-2 bulk load is paid once for the suite.
            external.CelestialObjectDB = await SharedCatalogDB.InitAsync(cancellationToken);
        }

        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        var sp = external.BuildServiceProvider();
        var camera = new Camera(cameraDevice, sp);
        var focuser = new Focuser(focuserDevice, sp);

        await camera.Driver.ConnectAsync(cancellationToken);
        await focuser.Driver.ConnectAsync(cancellationToken);

        var cameraDriver = (FakeCameraDriver)camera.Driver;
        var focuserDriver = (FakeFocuserDriver)focuser.Driver;

        cameraDriver.BinX = 1;
        // 512x512 keeps a synthetic render cheap, and at any realistic focal length it is also far too
        // little SKY to plate-solve: 0.28 deg on the IMX294C preset at 480 mm, which holds 2-7 Tycho-2
        // stars against the solver's MinStarsForMatch of 6. Measured in FakeFieldSolveProbe -- 512 never
        // solves, 1024 solves in 28 ms, 2048 solves in both a dense and a sparse field. No renderer or
        // solver change can fix the small ROI: the stars are not in that much sky.
        var roi = withCatalogStarField ? 2048 : 512;
        cameraDriver.NumX = roi;
        cameraDriver.NumY = roi;

        Cover? cover = null;
        if (coverFactory is not null)
        {
            // Caller-supplied cover (e.g. a driver that fails to connect); deliberately NOT connected
            // here -- the session code under test owns cover connection.
            cover = coverFactory(sp);
        }
        else if (withCoverCalibrator)
        {
            cover = new Cover(new FakeDevice(DeviceType.CoverCalibrator, 1), sp);
            await cover.Driver.ConnectAsync(cancellationToken);
        }
        else if (withManualCover)
        {
            // A dumb hand-switched panel: no flap (CoverStatus.NotPresent), calibrator Ready on demand.
            cover = new Cover(new ManualCoverDevice(), sp);
            await cover.Driver.ConnectAsync(cancellationToken);
        }

        FilterWheel? filterWheel = null;
        if (withFilterWheel)
        {
            filterWheel = new FilterWheel(new FakeDevice(DeviceType.FilterWheel, 1), sp);
            await filterWheel.Driver.ConnectAsync(cancellationToken);
        }

        var ota = new OTA(
            "Test Telescope",
            focalLength,
            camera,
            cover,
            focuser,
            new FocusDirection(PreferOutward: true, OutwardIsPositive: true),
            filterWheel,
            Switches: null
        );

        var mountQuery = new NameValueCollection
        {
            { "latitude", latitude.ToString(CultureInfo.InvariantCulture) },
            { "longitude", longitude.ToString(CultureInfo.InvariantCulture) },
            { "elevation", "200" }
        };
        if (mountPort is not null)
        {
            mountQuery.Add("port", mountPort);
        }
        var mountDevice = new FakeDevice(DeviceType.Mount, 1, mountQuery);
        var guiderDevice = new FakeDevice(DeviceType.Guider, 1);
        var guiderCamDevice = new FakeDevice(new Uri($"Camera://{nameof(FakeDevice)}/FakeGuideCam#Fake Guide Cam ({FakeCameraDriver.GuideCameraPreset.SensorName})"));
        if (coupleCameraToMount)
        {
            // Putting the mount in the hub is what couples the cameras to it: FakeCameraDriver finds
            // the mount it renders against by looking there, and ControllableDeviceBase then BORROWS
            // that driver rather than building a second one. This is the production shape -- every
            // real host connects through the hub -- but it turns on the whole coupling at once (the
            // guide camera's drift, the main camera's hidden polar misalignment, the pier side), so
            // it stays opt-in rather than silently changing what every session test images.
            await sp.GetRequiredService<IDeviceHub>().ConnectAsync(mountDevice, cancellationToken);
        }
        var mount = new Mount(mountDevice, sp);
        var guider = new Guider(guiderDevice, sp);
        var guiderCam = new Camera(guiderCamDevice, sp);

        await mount.Driver.ConnectAsync(cancellationToken);
        await guider.Driver.ConnectAsync(cancellationToken);
        await guiderCam.Driver.ConnectAsync(cancellationToken);
        guiderCam.Driver.FocalLength = 130; // typical guide scope
        await ((FakeGuider)guider.Driver).ConnectEquipmentAsync(cancellationToken);

        // Link mount + guide camera into the fake guider
        ((FakeGuider)guider.Driver).LinkDevices(mount.Driver, guiderCam.Driver);

        // Set UTC date on mount so TryGetTransformAsync works
        await mount.Driver.SetUTCDateAsync(timeProvider.GetUtcNow().UtcDateTime, cancellationToken);

        var weatherDevice = new FakeDevice(DeviceType.Weather, 1);
        var weather = new Weather(weatherDevice, sp);
        await weather.Driver.ConnectAsync(cancellationToken);

        var setup = new Setup(mount, guider, new GuiderSetup(guiderCam, FocalLength: 130), [ota], weather, mountLimits);
        // FakePlateSolver reports the target coordinates and nothing else -- no CD matrix, so no
        // orientation. A test that needs the solve to describe how the field LIES supplies its own.
        var plateSolver = plateSolverOverride ?? new FakePlateSolver();

        var config = configuration ?? DefaultConfiguration;
        var obs = observations ?? DefaultScheduledObservations;

        var session = new Session(setup, config, plateSolver, external, sp, new ScheduledObservationTree(obs));

        return new SessionTestContext(session, external, timeProvider, cameraDriver, focuserDriver, mount.Driver, cover?.Driver, filterWheel?.Driver, cancellationToken);
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
        var timeProvider = new FakeTimeProviderWrapper(now ?? new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var sp = external.BuildServiceProvider();

        // OTA 1: OSC camera, no filter wheel (fixed L-Ultimate dual-band)
        var oscCameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var oscCamera = new Camera(oscCameraDevice, sp);
        await oscCamera.Driver.ConnectAsync(cancellationToken);
        var oscCameraDriver = (FakeCameraDriver)oscCamera.Driver;
        oscCameraDriver.BinX = 1;
        oscCameraDriver.NumX = 512;
        oscCameraDriver.NumY = 512;

        // OTA 1 focuser
        var oscFocuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        var oscFocuser = new Focuser(oscFocuserDevice, sp);
        await oscFocuser.Driver.ConnectAsync(cancellationToken);
        var oscFocuserDriver = (FakeFocuserDriver)oscFocuser.Driver;

        // Fixed L-Ultimate (Ha+OIII) dual-band filter in a manual holder
        var oscFilterDevice = new ManualFilterWheelDevice(Filter.HydrogenAlphaOxygenIII);
        var oscFilterWheel = new FilterWheel(oscFilterDevice, sp);
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
        var monoCamera = new Camera(monoCameraDevice, sp);
        await monoCamera.Driver.ConnectAsync(cancellationToken);
        var monoCameraDriver = (FakeCameraDriver)monoCamera.Driver;
        monoCameraDriver.BinX = 1;
        monoCameraDriver.NumX = 512;
        monoCameraDriver.NumY = 512;

        var monoFocuserDevice = new FakeDevice(DeviceType.Focuser, 2);
        var monoFocuser = new Focuser(monoFocuserDevice, sp);
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
        var filterWheel = new FilterWheel(filterWheelDevice, sp);
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
            { "latitude", "48.2" },
            { "longitude", "16.3" },
            { "elevation", "200" }
        });
        var guiderDevice = new FakeDevice(DeviceType.Guider, 1);
        var guiderCamDevice = new FakeDevice(new Uri($"Camera://{nameof(FakeDevice)}/FakeGuideCam#Fake Guide Cam ({FakeCameraDriver.GuideCameraPreset.SensorName})"));
        var mount = new Mount(mountDevice, sp);
        var guider = new Guider(guiderDevice, sp);
        var guiderCam = new Camera(guiderCamDevice, sp);

        await mount.Driver.ConnectAsync(cancellationToken);
        await guider.Driver.ConnectAsync(cancellationToken);
        await guiderCam.Driver.ConnectAsync(cancellationToken);
        guiderCam.Driver.FocalLength = 130;
        await ((FakeGuider)guider.Driver).ConnectEquipmentAsync(cancellationToken);
        ((FakeGuider)guider.Driver).LinkDevices(mount.Driver, guiderCam.Driver);
        await mount.Driver.SetUTCDateAsync(timeProvider.GetUtcNow().UtcDateTime, cancellationToken);

        var dualWeatherDevice = new FakeDevice(DeviceType.Weather, 1);
        var dualWeather = new Weather(dualWeatherDevice, sp);
        await dualWeather.Driver.ConnectAsync(cancellationToken);

        var setup = new Setup(mount, guider, new GuiderSetup(guiderCam, FocalLength: 130), [ota1, ota2], dualWeather);
        var plateSolver = new FakePlateSolver();

        var config = configuration ?? DefaultConfiguration;
        var obs = observations ?? DefaultScheduledObservations;

        var session = new Session(setup, config, plateSolver, external, sp, new ScheduledObservationTree(obs));

        return new DualOTATestContext(session, external, timeProvider, oscCameraDriver, monoCameraDriver, oscFocuserDriver, monoFocuserDriver, filterWheelDriver, mount.Driver, cancellationToken);
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

/// <summary>
/// Session under test plus its fakes. Async-disposable because it OWNS the background tasks the
/// test starts: see <see cref="BackgroundWork"/> for why that matters and what breaks without it.
/// </summary>
internal record DualOTATestContext(
    Session Session,
    FakeExternal External,
    FakeTimeProviderWrapper TimeProvider,
    FakeCameraDriver OSCCamera,
    FakeCameraDriver MonoCamera,
    FakeFocuserDriver OSCFocuser,
    FakeFocuserDriver MonoFocuser,
    FakeFilterWheelDriver FilterWheel,
    IMountDriver Mount,
    CancellationToken TestCancellation = default
) : IAsyncDisposable
{
    private readonly BackgroundWork _work = new BackgroundWork(TestCancellation);

    /// <summary>Token for anything <see cref="Track(Task)"/>ed; cancelled by <see cref="DisposeAsync"/>.</summary>
    public CancellationToken Token => _work.Token;

    /// <inheritdoc cref="BackgroundWork.Track(Task)"/>
    public Task Track(Task task) => _work.Track(task);

    /// <inheritdoc cref="BackgroundWork.Track{T}(Task{T})"/>
    public Task<T> Track<T>(Task<T> task) => _work.Track(task);

    public ValueTask DisposeAsync() => _work.DisposeAsync();
}

/// <summary>
/// A cover device whose driver always fails to connect (models a dead / unplugged panel). Pass via
/// <c>coverFactory: sp => new Cover(new BrokenCoverDevice(), sp)</c> to exercise the connect-failure
/// paths (init fail-fast with a user-facing reason; end-of-session flats skip).
/// </summary>
internal sealed record BrokenCoverDevice() : DeviceBase(new Uri("covercalibrator://BrokenCoverDevice/broken#Broken Panel"))
{
    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp)
    {
        var driver = Substitute.For<ICoverDriver>();
        driver.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException(new InvalidOperationException("Could not open serial port for Broken Panel")));
        return driver;
    }
}

/// <summary>
/// Session under test plus its fakes. Async-disposable because it OWNS the background tasks the
/// test starts: see <see cref="BackgroundWork"/> for why that matters and what breaks without it.
/// </summary>
internal record SessionTestContext(
    Session Session,
    FakeExternal External,
    FakeTimeProviderWrapper TimeProvider,
    FakeCameraDriver Camera,
    FakeFocuserDriver Focuser,
    IMountDriver Mount,
    ICoverDriver? Cover = null,
    IFilterWheelDriver? FilterWheel = null,
    CancellationToken TestCancellation = default
) : IAsyncDisposable
{
    private readonly BackgroundWork _work = new BackgroundWork(TestCancellation);

    /// <summary>Token for anything <see cref="Track(Task)"/>ed; cancelled by <see cref="DisposeAsync"/>.</summary>
    public CancellationToken Token => _work.Token;

    /// <inheritdoc cref="BackgroundWork.Track(Task)"/>
    public Task Track(Task task) => _work.Track(task);

    /// <inheritdoc cref="BackgroundWork.Track{T}(Task{T})"/>
    public Task<T> Track<T>(Task<T> task) => _work.Track(task);

    public ValueTask DisposeAsync() => _work.DisposeAsync();
}

/// <summary>
/// Owns the background tasks a test starts, so none of them can outlive it.
/// <para>
/// This is not tidiness. Session logging is routed to <c>ITestOutputHelper</c> by
/// <see cref="FakeExternal.CreateLogger"/>, and that helper THROWS once its test is over
/// ("There is no currently active test"), which xUnit v3 raises as a CATASTROPHIC failure: the whole
/// run fails while reporting a zero failed-test count. So a session loop still running when its test
/// returns does not merely leak a thread, it can destroy the result of every other test in the
/// assembly. That is what made the old hand-rolled time pump doubly bad: its
/// <c>IsCompleted.ShouldBeTrue(...)</c> failure ended the test with the loop still logging.
/// </para>
/// <para>
/// <see cref="Track(Task)"/> registers a task; <see cref="DisposeAsync"/> cancels <see cref="Token"/>
/// and then awaits it. Because <c>await using</c> disposes INSIDE the test method, that
/// cancel-and-await runs while the test is still active, so the loop's final log lines land legally
/// rather than after the end. Pass <see cref="Token"/> and NOT the raw test token to anything
/// tracked, or teardown can only wait for the task rather than stop it.
/// </para>
/// <para>
/// Deliberately <see cref="IAsyncDisposable"/> and NOT <see cref="IDisposable"/>: a type carrying
/// both lets a plain <c>using</c> silently pick the sync path and skip the await, so declaring only
/// the async form turns every stale call site into a compile error. Teardown never throws, because a
/// <c>using</c> whose body AND dispose both throw propagates the dispose exception and loses the
/// body's, which would mask the test's real verdict.
/// </para>
/// </summary>
internal sealed class BackgroundWork(CancellationToken testCancellation) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
    private readonly ConcurrentBag<Task> _tracked = new ConcurrentBag<Task>();

    /// <summary>Token for anything <see cref="Track(Task)"/>ed; cancelled by <see cref="DisposeAsync"/>.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Registers a background task so teardown cancels and awaits it. Returns it unchanged.</summary>
    public Task Track(Task task)
    {
        _tracked.Add(task);
        return task;
    }

    /// <summary>
    /// Result-preserving <see cref="Track(Task)"/>, so a caller can still <c>await</c> the value.
    /// Without this overload the non-generic form erases <c>Task&lt;T&gt;</c> to <c>Task</c> and every
    /// <c>var result = await tracked;</c> stops compiling.
    /// </summary>
    public Task<T> Track<T>(Task<T> task)
    {
        _tracked.Add(task);
        return task;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        foreach (var task in _tracked)
        {
            try
            {
                // Real time, never the fake clock: an unpumped FakeTimeProvider would never reach a
                // timeout, so a wedged loop would hang the suite here instead of being abandoned.
                await task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception)
            {
                // Cancellation is the expected outcome, and a fault or a timeout is the test's own
                // problem which it has already asserted on. Teardown must not overwrite that verdict.
            }
        }

        _cts.Dispose();
    }
}
