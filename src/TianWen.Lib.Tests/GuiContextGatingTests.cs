using System;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// With a REMOTE rig on screen, the GUI's actions that drive THIS computer's rig refuse, as the session, flat and
/// polar starts already did (<c>EnsureLocalContext</c>), until P6 routes each to its context's node (P0b item 9 of
/// docs/plans/hardware-in-the-server.md, #752). Each drove the local rig from a remote view: planetary Start (the
/// remote mode pill offers Planetary), the planetary nudges, Goto from an object panel, and Solve and Sync, whose
/// reticle is the Active mount's.
/// <para>
/// Each handler either starts its local work at once or hands it to the tracker, so "the tracker was handed
/// nothing" is what shows the local rig was left alone, deterministically and without waiting on a slew.
/// </para>
/// </summary>
public class GuiContextGatingTests(ITestOutputHelper output)
{
    private const string Refusal = "runs on this computer";

    [Fact(Timeout = 30_000)]
    public async Task APlanetaryStartWithARemoteRigOnScreenLeavesTheLocalCameraAlone()
    {
        await using var h = await Harness.StartAsync(output, TestContext.Current.CancellationToken);

        h.Post(new StartVideoCaptureSignal(OtaIndex: 0));

        h.PlanetaryCapture.IsCapturing.ShouldBeFalse("the local camera started streaming");
        h.Contexts.Local.LiveSession.Mode.ShouldNotBe(LiveSessionMode.Planetary);
        h.ShouldHaveRefused();
    }

    [Fact(Timeout = 30_000)]
    public async Task AMountNudgeWithARemoteRigOnScreenDoesNotPulseTheLocalMount()
    {
        await using var h = await Harness.StartAsync(output, TestContext.Current.CancellationToken);

        h.Post(new JogMountSignal(GuideDirection.North, Arcsec: 10));

        h.ShouldHaveStartedNothing("a pulse on the local mount");
        h.ShouldHaveRefused();
    }

    [Fact(Timeout = 30_000)]
    public async Task AGotoWithARemoteRigOnScreenDoesNotSlewTheLocalMount()
    {
        await using var h = await Harness.StartAsync(output, TestContext.Current.CancellationToken);

        h.Post(new SkyMapSlewToObjectSignal("M 42", 5.588, -5.39, Index: null, ObjectType.Unknown));

        h.ShouldHaveStartedNothing("a slew of the local mount");
        h.ShouldHaveRefused();
    }

    [Fact(Timeout = 30_000)]
    public async Task ASolveAndSyncWithARemoteRigOnScreenDoesNotTouchTheLocalRig()
    {
        await using var h = await Harness.StartAsync(output, TestContext.Current.CancellationToken);

        h.Post(new SkyMapSolveSyncSignal());

        h.SkyMap.SolveSyncInProgress.ShouldBeFalse("a solve on the local camera, to sync the local mount");
        h.ShouldHaveStartedNothing("a solve on the local camera, to sync the local mount");
        h.ShouldHaveRefused();
    }

    /// <summary>
    /// The planetary panel's focuser jog, beside its nudges: not in the review's list, and reachable the same way.
    /// Its handler resolves the focuser as the preview panel's jog and goto do, so all three refuse together.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AFocuserJogWithARemoteRigOnScreenDoesNotMoveTheLocalFocuser()
    {
        await using var h = await Harness.StartAsync(output, TestContext.Current.CancellationToken);

        h.Post(new JogFocuserSignal(OtaIndex: 0, Steps: 10));

        h.ShouldHaveStartedNothing("a move of the local focuser");
        h.ShouldHaveRefused();
    }

    /// <summary>
    /// The control: the same Goto with this computer's rig on screen does drive it, so a refusal above is the
    /// context's doing and not a rig the harness failed to wire.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AGotoWithTheLocalRigOnScreenSlewsIt()
    {
        await using var h = await Harness.StartAsync(output, TestContext.Current.CancellationToken, remoteOnScreen: false);

        h.Post(new SkyMapSlewToObjectSignal("M 42", 5.588, -5.39, Index: null, ObjectType.Unknown));

        h.Tracker.PendingCount.ShouldBeGreaterThan(h.PendingBefore, "the local mount's slew was handed to the tracker");
        h.AppState.Notifications.ShouldNotContain(n => n.Message.Contains(Refusal));
    }

    /// <summary>
    /// The GUI's signal handler over a local rig of fake devices, connected in the hub and assigned to the active
    /// profile, with a remote rig's context on screen. A real clock, since a planetary start spins up capture loops
    /// that an auto-advancing fake clock would turn into busy loops.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CancellationTokenSource _cts;

        private Harness(ServiceProvider services, CancellationTokenSource cts, GuiAppState appState, ViewContexts contexts,
            SkyMapState skyMap, SignalBus bus, BackgroundTaskTracker tracker, PlanetaryCaptureController planetaryCapture)
        {
            _services = services;
            _cts = cts;
            AppState = appState;
            Contexts = contexts;
            SkyMap = skyMap;
            Bus = bus;
            Tracker = tracker;
            PlanetaryCapture = planetaryCapture;
            PendingBefore = tracker.PendingCount;
        }

        public GuiAppState AppState { get; }
        public ViewContexts Contexts { get; }
        public SkyMapState SkyMap { get; }
        public SignalBus Bus { get; }
        public BackgroundTaskTracker Tracker { get; }
        public PlanetaryCaptureController PlanetaryCapture { get; }

        /// <summary>What the tracker held before the test posted anything: the handler's own start-up work.</summary>
        public int PendingBefore { get; }

        public static async Task<Harness> StartAsync(ITestOutputHelper output, CancellationToken ct, bool remoteOnScreen = true)
        {
            var external = new FakeExternal(output);
            var services = new ServiceCollection()
                .AddSingleton<IExternal>(external)
                .AddSingleton<ITimeProvider>(new SystemTimeProvider())
                .AddSingleton<IDeviceHub, DeviceHub>()
                .AddLogging()
                .AddSingleton<ViewerState>()
                .AddSingleton<PlanetaryCaptureController>()
                // Handed to the planner's and the sky map's search boxes as the handler wires them; no test
                // here searches anything.
                .AddSingleton(Substitute.For<ICelestialObjectDB>())
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
                OTAs: [new OTAData("Scope", 500, camera.DeviceUri, null, focuser.DeviceUri, null, null, null)]));

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

            return new Harness(services, cts, appState, contexts, skyMap, bus, tracker,
                services.GetRequiredService<PlanetaryCaptureController>());
        }

        public void Post<T>(T signal) where T : notnull
        {
            Bus.Post(signal);
            Bus.ProcessPending();
        }

        public void ShouldHaveStartedNothing(string what)
            => Tracker.PendingCount.ShouldBe(PendingBefore, $"{what} was started");

        public void ShouldHaveRefused()
            => AppState.Notifications.ShouldContain(n => n.Severity == NotificationSeverity.Warning && n.Message.Contains(Refusal),
                "the user is told why nothing happened");

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await PlanetaryCapture.DisposeAsync();
            await Tracker.DrainAsync();
            await _services.DisposeAsync();
            _cts.Dispose();
        }
    }
}
