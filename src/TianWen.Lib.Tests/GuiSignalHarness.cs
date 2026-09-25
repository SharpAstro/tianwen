using System;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The GUI's own signal handler over a local rig of fake devices (mount, camera, focuser), connected in the hub
/// and assigned to the active profile with a site, optionally with a remote rig's context on screen. A real
/// clock, since a planetary start or a polar run spins up loops that an auto-advancing fake clock would turn
/// into busy loops.
/// </summary>
internal sealed class GuiSignalHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly CancellationTokenSource _cts;

    private GuiSignalHarness(ServiceProvider services, CancellationTokenSource cts, GuiAppState appState, ViewContexts contexts,
        SkyMapState skyMap, SignalBus bus, BackgroundTaskTracker tracker, IDeviceHub hub, Uri mountUri, Uri cameraUri, Uri focuserUri)
    {
        _services = services;
        _cts = cts;
        AppState = appState;
        Contexts = contexts;
        SkyMap = skyMap;
        Bus = bus;
        Tracker = tracker;
        Hub = hub;
        MountUri = mountUri;
        CameraUri = cameraUri;
        FocuserUri = focuserUri;
        PlanetaryCapture = services.GetRequiredService<PlanetaryCaptureController>();
        PendingBefore = tracker.PendingCount;
    }

    public GuiAppState AppState { get; }
    public ViewContexts Contexts { get; }
    public SkyMapState SkyMap { get; }
    public SignalBus Bus { get; }
    public BackgroundTaskTracker Tracker { get; }
    public IDeviceHub Hub { get; }
    public PlanetaryCaptureController PlanetaryCapture { get; }
    public Uri MountUri { get; }
    public Uri CameraUri { get; }
    public Uri FocuserUri { get; }

    /// <summary>What the tracker held before the test posted anything: the handler's own start-up work.</summary>
    public int PendingBefore { get; private set; }

    public static async Task<GuiSignalHarness> StartAsync(ITestOutputHelper output, CancellationToken ct, bool remoteOnScreen = false)
    {
        var external = new FakeExternal(output);
        var services = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(new SystemTimeProvider())
            .AddSingleton<IDeviceHub, DeviceHub>()
            .AddLogging()
            .AddSingleton<ViewerState>()
            .AddSingleton<PlanetaryCaptureController>()
            // Handed to the planner's and the sky map's search boxes as the handler wires them, and to a polar
            // run; no test here searches or solves anything.
            .AddSingleton(Substitute.For<ICelestialObjectDB>())
            .AddSingleton(Substitute.For<IPlateSolverFactory>())
            .BuildServiceProvider();

        var hub = services.GetRequiredService<IDeviceHub>();
        var mount = new FakeDevice(DeviceType.Mount, 1);
        var camera = new FakeDevice(DeviceType.Camera, 1);
        var focuser = new FakeDevice(DeviceType.Focuser, 1);
        await hub.ConnectAsync(mount, ct);
        await hub.ConnectAsync(camera, ct);
        await hub.ConnectAsync(focuser, ct);
        var profile = new Profile(Guid.NewGuid(), "This computer's rig", new ProfileData(
            Mount: mount.DeviceUri,
            Guider: new FakeDevice(DeviceType.Guider, 1).DeviceUri,
            OTAs: [new OTAData("Scope", 500, camera.DeviceUri, null, focuser.DeviceUri, null, null, null)],
            SiteLatitude: 48.2,
            SiteLongitude: 16.3));

        var appState = new GuiAppState { ActiveProfile = profile, DeviceHub = hub };
        var contexts = new ViewContexts();
        if (remoteOnScreen)
        {
            contexts.Activate(contexts.GetOrAddRemote("observatory-node", "Observatory"));
        }

        var skyMap = new SkyMapState();
        var bus = new SignalBus();
        var tracker = new BackgroundTaskTracker();
        var cts = new CancellationTokenSource();
        _ = new AppSignalHandler(services, appState, new PlannerState(), new SessionTabState(), new EquipmentTabState(),
            contexts, skyMap, bus, tracker, cts, cts.Token, external);

        return new GuiSignalHarness(services, cts, appState, contexts, skyMap, bus, tracker, hub,
            mount.DeviceUri, camera.DeviceUri, focuser.DeviceUri);
    }

    public void Post<T>(T signal) where T : notnull
    {
        Bus.Post(signal);
        Bus.ProcessPending();
    }

    /// <summary>Counts the tracker's work from here on, for a test that started a run first.</summary>
    public void MarkPending() => PendingBefore = Tracker.PendingCount;

    public void ShouldHaveStartedNothing(string what)
        => Tracker.PendingCount.ShouldBe(PendingBefore, $"{what} was started");

    public void ShouldHaveRefused(string reason)
        => AppState.Notifications.ShouldContain(n => n.Severity == NotificationSeverity.Warning && n.Message.Contains(reason),
            "the user is told why nothing happened");

    public bool Claimed(Uri deviceUri) => !DeviceOwnershipGate.Evaluate(Hub, deviceUri, DeviceAction.Actuate).Allowed;

    /// <summary>Waits until <paramref name="condition"/> holds, draining the tracker as the host does.</summary>
    public async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            Tracker.ProcessCompletions(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            await Task.Delay(20, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Contexts.Local.LiveSession.PolarAlignmentCts?.Cancel();
        await _cts.CancelAsync();
        await PlanetaryCapture.DisposeAsync();
        await Tracker.DrainAsync();

        // Stop the rig before its services go, as a host does: a cancelled run can leave a fake camera mid-exposure,
        // and the frame's end resolves from the provider. The provider marks itself disposed before it disposes the
        // hub, so leaving this to it lets that end land in between.
        Uri[] rig = [CameraUri, FocuserUri, MountUri];
        foreach (var uri in rig)
        {
            await Hub.DisconnectAsync(uri, force: true, CancellationToken.None);
        }
        await _services.DisposeAsync();
        _cts.Dispose();
    }
}
