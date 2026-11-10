using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The GUI's own signal handler over this computer's node, as the GUI runs from P6 of docs/plans/hardware-in-the-server.md
/// (#936): a real node on its own socket holding a rig of fake devices (mount, camera, focuser), connected and assigned to
/// its active profile with a site, and the handler connected to it as the GUI connects at start
/// (<see cref="AppSignalHandler.ConnectLocalNodeAsync"/>), its first discovery run. Optionally a remote rig's context on
/// screen. A real clock, since the node's loops wait as they do in production.
/// </summary>
internal sealed class GuiNodeHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly CancellationTokenSource _cts;
    private readonly List<RemoteSessionMirror> _mirrors = [];
    private readonly List<IAsyncDisposable> _rigs = [];
    private readonly StallWatch _stalls;

    private GuiNodeHarness(NodeHarness node, ServiceProvider services, CancellationTokenSource cts, GuiAppState appState,
        ViewContexts contexts, SkyMapState skyMap, EquipmentTabState equipment, SignalBus bus, BackgroundTaskTracker tracker,
        Uri mountUri, Uri cameraUri, Uri focuserUri, AppSignalHandler handler, PlannerState planner, SessionTabState session, Profile profile,
        StallWatch stalls)
    {
        _stalls = stalls;
        Node = node;
        Session = session;
        _services = services;
        _cts = cts;
        Handler = handler;
        Planner = planner;
        AppState = appState;
        Equipment = equipment;
        Contexts = contexts;
        SkyMap = skyMap;
        Bus = bus;
        Tracker = tracker;
        MountUri = mountUri;
        CameraUri = cameraUri;
        FocuserUri = focuserUri;
        Profile = profile;
        PendingBefore = tracker.PendingCount;
    }

    /// <summary>This computer's node.</summary>
    public NodeHarness Node { get; }

    /// <summary>The node's hub, which holds the rig: what a test asserts a device's state and its claims on.</summary>
    public IDeviceHub Hub => Node.App.Services.GetRequiredService<IDeviceHub>();

    public GuiAppState AppState { get; }

    /// <summary>The handler under test, for what a signal does not reach (the planner's per-frame recompute).</summary>
    public AppSignalHandler Handler { get; }

    public PlannerState Planner { get; }

    /// <summary>The session tab's state: its configuration and each OTA's camera settings, which a start sends.</summary>
    public SessionTabState Session { get; }

    /// <summary>The Equipment tab's state the handler writes, e.g. a disconnect's confirm strip.</summary>
    public EquipmentTabState Equipment { get; }
    public ViewContexts Contexts { get; }
    public SkyMapState SkyMap { get; }
    public SignalBus Bus { get; }
    public BackgroundTaskTracker Tracker { get; }
    public Uri MountUri { get; }
    public Uri CameraUri { get; }
    public Uri FocuserUri { get; }

    /// <summary>The profile the node runs, as it was saved.</summary>
    public Profile Profile { get; }

    /// <summary>The planetary view the handler starts and stops: the node's capture, as this computer shows it.</summary>
    public PlanetaryCaptureController Planetary => _services.GetRequiredService<PlanetaryCaptureController>();

    /// <summary>The token the handler's own background work runs on, which a quit cancels as the host's does.</summary>
    public CancellationToken HostBackground => _cts.Token;

    /// <summary>The host's quit over this harness, as the GUI composes it: its background work is the handler's.</summary>
    public AppQuit Quit()
    {
        var clock = SystemTimeProvider.Instance;
        return new AppQuit(AppState, Contexts, new RigShutdown(clock, NullLogger.Instance), Tracker, _cts, clock, NullLogger.Instance);
    }

    /// <summary>This computer's node as the handler reads it.</summary>
    public LocalNodeConnection Local => AppState.LocalNode.ShouldNotBeNull(AppState.LocalNodeProblem);

    /// <summary>What the tracker held before the test posted anything: the handler's own start-up work.</summary>
    public int PendingBefore { get; private set; }

    /// <param name="withFilterWheel">Gives the rig's OTA the fake wheel (L, R, G, B; focus offsets 0, +20, 0, -15), as a mono camera's.</param>
    public static async Task<GuiNodeHarness> StartAsync(ITestOutputHelper output, CancellationToken ct, bool remoteOnScreen = false,
        bool withFilterWheel = false)
    {
        // First, so the node's own start-up is watched as well: a stall there (8 to 16 s, measured) spends the test's time
        // before its first wait begins, and nothing else in the output would say so (#2023).
        var stalls = new StallWatch(output);
        NodeHarness node;
        try
        {
            node = await NodeHarness.StartAsync(output, ct, onItsSocket: true);
        }
        catch
        {
            stalls.Dispose();
            throw;
        }
        var socketPath = node.SocketPath ?? throw new InvalidOperationException("A node started on its own socket has one.");
        // The view's mirror polls the node's state all along, so a session the node makes answers it as a real one does.
        node.Factory.OnCreated = static controlled => RemoteSessionMirrorTests.Observing(controlled.Session);
        try
        {
            var mount = new FakeDevice(DeviceType.Mount, 1);
            var camera = new FakeDevice(DeviceType.Camera, 1);
            var focuser = new FakeDevice(DeviceType.Focuser, 1);
            var wheel = withFilterWheel ? new FakeDevice(DeviceType.FilterWheel, 1) : null;
            var profile = new Profile(Guid.NewGuid(), "This computer's rig", new ProfileData(
                Mount: mount.DeviceUri,
                Guider: new FakeDevice(DeviceType.Guider, 1).DeviceUri,
                OTAs: [new OTAData("Scope", 500, camera.DeviceUri, null, focuser.DeviceUri, wheel?.DeviceUri, null, null)],
                SiteLatitude: 48.2,
                SiteLongitude: 16.3));
            await profile.SaveAsync(node.External, ct);
            await node.Node.SetActiveProfileAsync(profile.ProfileId, ct);
            // Connected on the node, as a user's connects left them before the window opened.
            var hub = node.App.Services.GetRequiredService<IDeviceHub>();
            await hub.ConnectAsync(mount, ct);
            await hub.ConnectAsync(camera, ct);
            await hub.ConnectAsync(focuser, ct);
            if (wheel is not null)
            {
                await hub.ConnectAsync(wheel, ct);
            }

            var gui = ComposeClient(output);
            var (services, cts, appState, contexts, skyMap, bus, tracker, equipment, planner, session, handler) = gui;

            // In the mode of the harness's node (not local-only), whatever the test process's environment says.
            await gui.ConnectAsync(socketPath, LocalOnlyMode.Off, includeFake: true, ct);

            if (remoteOnScreen)
            {
                contexts.Activate(contexts.GetOrAddRemote("observatory-node", "Observatory"));
            }

            return new GuiNodeHarness(node, services, cts, appState, contexts, skyMap, equipment, bus, tracker,
                mount.DeviceUri, camera.DeviceUri, focuser.DeviceUri, handler, planner, session, profile, stalls);
        }
        catch
        {
            await node.DisposeAsync();
            stalls.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The GUI's client half as a window composes it, connected to no node yet: its signal handler and the states it
    /// writes, over an empty sky, no comets and no plate solver. This harness's, and a probe's on a real tianwen-server.
    /// </summary>
    internal static GuiClient ComposeClient(ITestOutputHelper output)
    {
        // An empty sky for the planner: a full recompute walks every visible cell of the catalogue's grid.
        var catalog = Substitute.For<ICelestialObjectDB>();
        catalog.DeepSkyCoordinateGrid.Returns(new RaDecIndex());
        catalog.AllObjectIndices.Returns(new HashSet<CatalogIndex>());
        var external = new FakeExternal(output);
        // No comets: an empty list, never the substitute's default array, which a planner recompute enumerates.
        var comets = Substitute.For<ICometRepository>();
        comets.All.Returns(System.Collections.Immutable.ImmutableArray<CometElements>.Empty);
        var services = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
            .AddLogging()
            .AddSingleton<ViewerState>()
            .AddSingleton<PlanetaryCaptureController>()
            .AddSingleton<LiveViewController>()
            .AddSingleton<GuideLoopController>()
            .AddSingleton(catalog)
            // The planner's start loads the comets beside the catalogue: none, here.
            .AddSingleton(comets)
            .AddSingleton(Substitute.For<IPlateSolverFactory>())
            .BuildServiceProvider();

        var appState = new GuiAppState();
        var contexts = new ViewContexts();
        var skyMap = new SkyMapState();
        var bus = new SignalBus();
        var tracker = new BackgroundTaskTracker();
        var cts = new CancellationTokenSource();
        var equipment = new EquipmentTabState();
        var planner = new PlannerState();
        var session = new SessionTabState();
        var handler = new AppSignalHandler(services, appState, planner, session, equipment,
            contexts, skyMap, bus, tracker, cts, cts.Token, external);
        return new GuiClient(services, cts, appState, contexts, skyMap, bus, tracker, equipment, planner, session, handler);
    }

    /// <summary>The GUI's client half (<see cref="ComposeClient"/>): what its handler writes, and the handler.</summary>
    internal sealed record GuiClient(ServiceProvider Services, CancellationTokenSource Cts, GuiAppState AppState, ViewContexts Contexts,
        SkyMapState SkyMap, SignalBus Bus, BackgroundTaskTracker Tracker, EquipmentTabState Equipment, PlannerState Planner,
        SessionTabState Session, AppSignalHandler Handler)
    {
        /// <summary>
        /// Connects to the node on <paramref name="socketPath"/> as the GUI connects at start, in <paramref name="localOnly"/>
        /// whatever the test process's environment says, and runs the first discovery the connect asks for to its end, so
        /// the Equipment tab lists what the node reaches.
        /// </summary>
        public async Task ConnectAsync(string socketPath, LocalOnlyMode localOnly, bool includeFake, CancellationToken ct)
        {
            // The launcher asks the node on its socket within 2 s and the handler gives up with the node's no-answer text, which on a
            // runner that did not schedule the node for longer (a 5.9 s GC pause in j4184) lost the whole start of the test. A
            // window's host asks again (MaybeReconnectLocalNode) and so does this, for that and for nothing else: any other problem
            // is an answer, and fails at once. Bounded by the caller's token, which a test's timeout cancels (#2023).
            var options = new LocalNodeOptions { NamedSocket = socketPath, AnotherAccountProbe = null, LocalOnly = localOnly };
            var asked = 0;
            do
            {
                if (asked++ > 0)
                {
                    TestContext.Current.TestOutputHelper?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} info The node did not answer ({AppState.LocalNodeProblem}); asking again, ask {asked}");
                    await Task.Delay(100, ct);
                }
                await Handler.ConnectLocalNodeAsync(options, requestedProfile: null, includeFake, ct);
            }
            while (AppState.LocalNode is null && AppState.LocalNodeProblem?.Contains(TianWenNodeClient.NoAnswerText, StringComparison.Ordinal) == true);
            AppState.LocalNode.ShouldNotBeNull(AppState.LocalNodeProblem);
            if (asked > 1)
            {
                // Each ask that went unanswered left its Error note ("This computer's rig cannot be used: No node answers ..."),
                // and a test built on this harness asserts its feed holds no Error: the start it got is clean, so its feed is.
                AppState.ClearNotifications();
            }
            var before = AppState.Notifications;
            Bus.ProcessPending(Tracker);
            try
            {
                while (Equipment.IsDiscovering || Equipment.DiscoveredDevices.Count == 0)
                {
                    Tracker.ProcessCompletions(NullLogger.Instance);
                    await Task.Delay(20, ct);
                }
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                // Not NodeWait's: the real-node probes that share this connect declare no timeout of their own. The same words,
                // though: a test that never got past its start-up said nothing of where it was (#2023).
                var ended = $"The test's time ran out waiting for the first discovery to list the node's devices ("
                    + $"{(Equipment.IsDiscovering ? "discovering" : "not discovering")}, {Equipment.DiscoveredDevices.Count} listed; {HandlerSaid(AppState, Tracker, before)})";
                TestContext.Current.TestOutputHelper?.WriteLine(ended);
                throw new OperationCanceledException(ended, ex, ct);
            }
        }
    }

    /// <summary>
    /// Connects the remote rig on screen as <see cref="RemoteRigConnection"/> does, its node a recorder: every request the
    /// view sends it, as "METHOD path", each answered with success.
    /// </summary>
    public ConcurrentQueue<string> ConnectRemoteRig()
    {
        var sent = new ConcurrentQueue<string>();
        var http = new HttpClient(new RecordingNode(sent)) { BaseAddress = new Uri("http://rig.local:1888/") };
        var clock = SystemTimeProvider.Instance;
        var mirror = new RemoteSessionMirror(NodeTestClients.ClientOf(http),
            new TianWenEventStream(http.BaseAddress, clock, NullLogger.Instance), clock, NullLogger.Instance);
        _mirrors.Add(mirror);
        Contexts.Active.LiveSession.ActiveSession = mirror;
        Contexts.Active.Mirror = mirror;
        return sent;
    }

    /// <summary>
    /// A real rig on the LAN (P6b): a node of its own over loopback TCP, which it treats as the LAN, holding a mount, a camera
    /// and a focuser of its own under an active profile with a site; connected as <see cref="RemoteRigActions"/> connects a
    /// bound rig (through the handler's grants, into its registry) and put on screen, its profile and access read once.
    /// This client holds no grant on it until the test gives one (<see cref="GrantAsync"/>).
    /// </summary>
    public async Task<(NodeHarness Node, RemoteRigConnection Connection, Profile Profile)> ConnectRigAsync(ITestOutputHelper output,
        CancellationToken ct)
    {
        var node = await NodeHarness.StartAsync(output, ct);
        _rigs.Add(node);
        var mount = new FakeDevice(DeviceType.Mount, 2);
        var camera = new FakeDevice(DeviceType.Camera, 2);
        var focuser = new FakeDevice(DeviceType.Focuser, 2);
        var profile = new Profile(Guid.NewGuid(), "The observatory's rig", new ProfileData(
            Mount: mount.DeviceUri,
            Guider: new FakeDevice(DeviceType.Guider, 2).DeviceUri,
            OTAs: [new OTAData("Observatory scope", 800, camera.DeviceUri, null, focuser.DeviceUri, null, null, null)],
            SiteLatitude: 48.2,
            SiteLongitude: 16.3));
        await profile.SaveAsync(node.External, ct);
        await node.Node.SetActiveProfileAsync(profile.ProfileId, ct);
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        await hub.ConnectAsync(mount, ct);
        await hub.ConnectAsync(camera, ct);
        await hub.ConnectAsync(focuser, ct);

        var nodeId = (await NodeTestClients.ClientOf(node.Client).GetNodeAsync(ct)).Value.ShouldNotBeNull().NodeId;
        var binding = new RemoteRigBinding
        {
            BindingId = Guid.NewGuid(),
            NodeId = nodeId,
            Alias = "Observatory",
            LastAddress = node.Transport.BaseAddress.ToString(),
        };
        var rig = RemoteRigConnection.TryConnect(binding, Contexts, peers: null, AppState.NodeGrants, SystemTimeProvider.Instance,
            NullLogger.Instance, _cts.Token).ShouldNotBeNull();
        _rigs.Add(rig);
        Handler.Rigs.Upsert(binding);
        Handler.Rigs.Attach(rig);
        Contexts.Activate(rig.Context);
        (await rig.MaybeRefreshProfileAsync(ct)).ShouldBeTrue("the rig's view reads its profile");
        await rig.RefreshAccessNowAsync(ct);
        return (node, rig, profile);
    }

    /// <summary>Grants this client control of <paramref name="rig"/>, as the rig's owner would, and has its view learn it.</summary>
    public static async Task GrantAsync(NodeHarness node, RemoteRigConnection rig, CancellationToken ct)
    {
        var issued = await node.App.Services.GetRequiredService<TianWen.Hosting.NodeAccess>().GrantAsync("This computer", ct);
        rig.Transport.Grant.Token = issued.Token;
        await rig.RefreshAccessNowAsync(ct);
        rig.MayCommand.ShouldBeTrue();
    }

    private sealed class RecordingNode(ConcurrentQueue<string> sent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.Enqueue($"{request.Method} {request.RequestUri?.AbsolutePath}");
            var body = JsonSerializer.Serialize(ResponseEnvelope<string>.Ok("ok"), HostingJsonContext.Default.ResponseEnvelopeString);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    public void Post<T>(T signal) where T : notnull
    {
        Bus.Post(signal);
        Bus.ProcessPending(Tracker);
    }

    /// <summary>Counts the tracker's work from here on, for a test that started a run first.</summary>
    public void MarkPending() => PendingBefore = Tracker.PendingCount;

    public void ShouldHaveStartedNothing(string what)
        => Tracker.PendingCount.ShouldBe(PendingBefore, $"{what} was started");

    public void ShouldHaveRefused(string reason)
        => AppState.Notifications.ShouldContain(n => n.Severity == NotificationSeverity.Warning && n.Message.Contains(reason),
            "the user is told why nothing happened");

    public bool Claimed(Uri deviceUri) => !DeviceOwnershipGate.Evaluate(Hub, deviceUri, DeviceAction.Actuate).Allowed;

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, draining the tracker and beating presence as the host's loop does:
    /// without the beat the node counts this window as gone and ends an interactive run after its grace, so a view that
    /// never asked for a stop still sees its run end, and a test of the stop passes slowly on nothing.
    /// <para>
    /// The wait is <see cref="NodeWait"/>'s, bounded by the test's own timeout and by nothing shorter, and it is named:
    /// <paramref name="waitingFor"/> goes to the output with what the handler has done meanwhile (the notes it raised, a
    /// refusal or a node's "No answer" among them, and the work it still has running), and again when the timeout ends the
    /// wait, so a hang says what it waited for. A bare timeout said only that 30 s had passed (#2023).
    /// </para>
    /// </summary>
    /// <param name="waitingFor">What the wait is for, completing "Waiting for ...": the state it waits on, never the code.</param>
    public Task UntilAsync(string waitingFor, Func<bool> condition, CancellationToken ct)
    {
        var before = AppState.Notifications;
        return NodeWait.UntilAsync(waitingFor, _ =>
        {
            var done = condition();
            if (!done)
            {
                Handler.BeatNodes();
                Tracker.ProcessCompletions(NullLogger.Instance);
            }
            return ValueTask.FromResult((done, HandlerSaid(AppState, Tracker, before)));
        }, ct);
    }

    /// <summary>
    /// Reads the node's devices into the view, asking again until one read is answered: what a window's host does every two
    /// seconds and this harness has no loop to do. A row built before a read says every device is off, whatever the node
    /// holds, and a click on it then asks the node to CONNECT a device it holds, which is nothing a test would be looking for.
    /// </summary>
    public Task UntilDevicesReadAsync(CancellationToken ct) =>
        NodeWait.UntilAsync("the view to read the node's devices", async token =>
        {
            var read = await Local.RefreshDevicesNowAsync(token);
            return (read, read ? $"{Local.Devices.Count} device(s) read" : "not answered yet");
        }, ct);

    /// <summary>
    /// Does what a user does, and again as a user would when the window did not answer. <paramref name="act"/> is the
    /// gesture (a click, a key); the signal it posts is run, and the wait is <see cref="UntilAsync"/>'s for
    /// <paramref name="done"/>. When the handler ended an ask with the node's <see cref="TianWenNodeClient.NoAnswerText"/> and
    /// no connect or disconnect is in flight, the gesture is made again.
    /// <para>
    /// That is the ONE failure it makes up for: a call that outran its client's budget (10 s for a control call) because a loaded runner
    /// had not scheduled the node, where the handler notes it and gives up (the click is lost, nothing else comes), and the
    /// test, which waits on what the click would have done, waits for ever. CI run 41939984127 hung 30 s in
    /// <c>TuiEquipmentSlotClickTests</c> with a thread pool starved for longer than that. A refusal, a wrong signal or a
    /// click that is never routed says something else, is never asked again, and still fails the test.
    /// </para>
    /// </summary>
    public Task ActUntilAsync(string waitingFor, Action act, Func<bool> done, CancellationToken ct)
    {
        var asked = AppState.Notifications;
        void Ask()
        {
            asked = AppState.Notifications;
            act();
            Bus.ProcessPending(Tracker);
        }

        Ask();
        return UntilAsync(waitingFor, () =>
        {
            if (done())
            {
                return true;
            }
            if (Equipment.PendingTransitions.IsEmpty && NotesSince(AppState, asked).Any(static note => note.Message.Contains(TianWenNodeClient.NoAnswerText, StringComparison.Ordinal)))
            {
                Ask();
            }
            return false;
        }, ct);
    }

    /// <summary>The notes raised after <paramref name="before"/> was read, newest first.</summary>
    private static IEnumerable<NotificationEntry> NotesSince(GuiAppState appState, ImmutableArray<NotificationEntry> before)
    {
        foreach (var note in appState.Notifications)
        {
            if (!before.IsDefaultOrEmpty && note.Equals(before[0]))
            {
                yield break;
            }
            yield return note;
        }
    }

    /// <summary>What the handler did since <paramref name="before"/>: its notes, newest first (the last three), and its work running.</summary>
    internal static string HandlerSaid(GuiAppState appState, BackgroundTaskTracker tracker, ImmutableArray<NotificationEntry> before)
    {
        var notes = 0;
        var latest = new List<string>(3);
        foreach (var note in NotesSince(appState, before))
        {
            notes++;
            if (latest.Count < 3)
            {
                latest.Add($"[{note.Severity}] {note.Message}");
            }
        }
        return $"{tracker.PendingCount} tracked task(s) running, {notes} note(s) raised" + (notes > 0 ? $": {string.Join(" | ", latest)}" : "");
    }

    /// <summary>
    /// Waits until every piece of the handler's work has ended, as a host drains its tracker: the handler awaits its node
    /// now, so a refusal or a note lands after the post returns. Not for a test that starts a job which runs on (a cooling
    /// ramp): that waits for it to end.
    /// </summary>
    public async Task UntilSettledAsync(CancellationToken ct)
    {
        await Tracker.DrainAsync().WaitAsync(ct);
        Tracker.ProcessCompletions(NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await Tracker.DrainAsync();
        foreach (var mirror in _mirrors)
        {
            await mirror.DisposeAsync();
        }
        // A rig's connection before its node, as a window goes before the rig does.
        for (var i = _rigs.Count - 1; i >= 0; i--)
        {
            await _rigs[i].DisposeAsync();
        }
        if (AppState.LocalNode is { } local)
        {
            await local.DisposeAsync();
        }

        // Stop the rig before the node goes, as a host does: a cancelled run can leave a fake camera mid-exposure.
        Uri[] rig = [CameraUri, FocuserUri, MountUri];
        foreach (var uri in rig)
        {
            await Hub.DisconnectAsync(uri, force: true, CancellationToken.None);
        }
        await Node.DisposeAsync();
        await _services.DisposeAsync();
        _cts.Dispose();
        // Last, so a teardown that stalls (7 s, measured) is said as well.
        _stalls.Dispose();
    }
}
