using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Sequencing.PolarAlignment;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Host-agnostic signal handler. Wires all <see cref="SignalBus"/> subscriptions
    /// and <see cref="TextInputState"/> callbacks for planner search, equipment editing,
    /// and profile management. Shared between GPU and terminal hosts.
    /// <para>
    /// <b>SignalBus delivery contract:</b>
    /// <see cref="SignalBus.PostSignal{T}"/> enqueues the signal; it never delivers inline,
    /// so it is safe to call during rendering or hit testing (no reentrancy risk).
    /// <see cref="SignalBus.ProcessPending"/> is called once per frame from the render thread:
    /// it dequeues all pending signals and invokes subscribers in registration order.
    /// Sync subscribers run inline on the render thread; async subscribers are submitted
    /// to <see cref="BackgroundTaskTracker"/> (never fire-and-forget).
    /// </para>
    /// <para>
    /// Split by concern across partial files (the ImageRendererBase treatment): this file
    /// holds the shared scaffolding (fields, ctor wiring, planner init, telemetry polls,
    /// routing helpers); the subscription groups live in <c>AppSignalHandler.Planner.cs</c> /
    /// <c>.SkyMap.cs</c> / <c>.Equipment.cs</c> / <c>.LiveSession.cs</c> / <c>.Polar.cs</c> /
    /// <c>.Flats.cs</c>.
    /// </para>
    /// </summary>
    public partial class AppSignalHandler
    {
        private readonly IServiceProvider _sp;
        private readonly GuiAppState _appState;
        private readonly PlannerState _plannerState;
        private readonly SessionTabState _sessionState;
        private readonly BackgroundTaskTracker _tracker;
        private readonly CancellationTokenSource _cts;
        private readonly IExternal _external;
        private readonly ViewContexts _contexts;
        private readonly ILogger _logger;
        private readonly ITimeProvider _timeProvider;
        private readonly EquipmentTabState _eqState;
        private readonly SkyMapState _skyMapState;
        private readonly SignalBus _bus;

        /// <summary>
        /// Bound rigs and their live mirrors. Owned here rather than in <see cref="GuiAppState"/> because
        /// a connection has a lifetime (it must be disposed), and the signal handler is what already owns
        /// the app's background work.
        /// </summary>
        private readonly RemoteRigRegistry _rigs = new RemoteRigRegistry();

        /// <summary>Bound rigs, for the profile picker to list alongside local profiles.</summary>
        public RemoteRigRegistry Rigs => _rigs;

        /// <summary>
        /// The rig whose plan is on screen, or null for this computer's own plan. Feeds
        /// <see cref="PlannerPersistence"/> so pinned targets are scoped per rig -- two rigs running a
        /// profile copied from the same source share a profile id, and their pins would otherwise merge
        /// into one file.
        /// </summary>
        private Guid? ActiveRemoteBindingId =>
            _contexts.Active is { IsLocal: false, NodeId: { } nodeId }
                ? _rigs.Bindings.FirstOrDefault(b => string.Equals(b.NodeId, nodeId, StringComparison.OrdinalIgnoreCase))?.BindingId
                : null;

        /// <summary>
        /// The profile of the view on show (P5b part 8): this computer's active profile, or a rig's own, read from the rig
        /// (<see cref="ViewContext.RigProfile"/>; null until it has been). The planner plans with it, so a rig's nights are
        /// planned at its site, and its clock, twilight and sky are drawn there; selecting a rig changes what you look at,
        /// and the site is part of what you look at.
        /// </summary>
        internal Profile? ProfileOnShow => _contexts.ProfileOnShow(_appState.ActiveProfile);

        // The view the planner last planned for. Read and written on the UI thread by CheckRecompute alone, which is how any
        // switch of view (a rig selected, this computer's view, a rig forgotten, a quit) replans without each owing a step.
        private ViewContext? _plannedFor;

        /// <summary>
        /// The LOCAL node's session state. Every handler here drives or guards <i>this node's</i>
        /// equipment -- the preview telemetry poll reads local hub drivers, the session/flats/polar
        /// handlers start local runs, and the profile gate protects local device bindings -- so they all
        /// resolve <see cref="ViewContexts.Local"/> and never the on-screen context: watching a remote
        /// rig must not redirect a local poll, nor let a local profile rebind slip past the gate.
        /// Safe for a subscribe lambda to capture, because the local context's identity is stable for
        /// the process lifetime (see <see cref="ViewContext"/>).
        /// </summary>
        private LiveSessionState LocalLiveSession => _contexts.Local.LiveSession;

        /// <summary>Set by the host after catalog load to enable autocomplete.</summary>
        public Action<string[]> SetAutoCompleteCache { get; }

        /// <summary>
        /// Called when a search commit or suggestion resolves a target index
        /// that should be scrolled into view. The host wires this to its
        /// list widget's scroll mechanism.
        /// </summary>
        public Action<int>? OnPlannerEnsureVisible { get; set; }

        /// <summary>
        /// Extracts filter configuration and optical design from the first OTA in the profile.
        /// Shared between BuildScheduleSignal, StartSessionSignal, and host setup.
        /// </summary>
        public static (IReadOnlyList<InstalledFilter>? Filters, OpticalDesign Design) GetFirstOtaFilterConfig(ProfileData profileData)
        {
            var filters = profileData.OTAs.Length > 0
                ? EquipmentActions.GetFilterConfig(profileData, 0)
                : null;
            var design = profileData.OTAs.Length > 0
                ? profileData.OTAs[0].OpticalDesign
                : OpticalDesign.Unknown;
            return (filters is { Count: > 0 } ? filters : null, design);
        }

        /// <summary>
        /// Applies site coordinates from a transform to the planner state.
        /// </summary>
        public static void ApplySiteFromTransform(PlannerState plannerState, Transform transform)
        {
            plannerState.SiteLatitude = transform.SiteLatitude;
            plannerState.SiteLongitude = transform.SiteLongitude;
            plannerState.SiteTimeZone = transform.SiteTimeZone;
        }

        /// <summary>
        /// Initializes the planner: loads catalog, computes tonight's best targets,
        /// restores persisted pins, and populates autocomplete.
        /// Call via <see cref="BackgroundTaskTracker.Run"/> from the host.
        /// </summary>
        public async Task InitializePlannerAsync(Transform transform, CancellationToken cancellationToken)
        {
            _logger.LogInformation("InitializePlanner: starting");

            // Pre-warm the VSOP87a planet ephemeris concurrently with the catalog load. The first
            // ReduceJ2000 call costs ~330 ms (one-time JIT + static-table init); doing it here keeps
            // it off the first Sky Atlas open, where it otherwise stalls DrawPlanetLabels on the
            // render thread. Tracked (not raw fire-and-forget) so a failure is logged, not swallowed.
            _tracker.Run(() => Task.Run(SkyMapState.PrewarmPlanetEphemeris, cancellationToken),
                "Pre-warm planet ephemeris");

            var objectDb = _sp.GetRequiredService<ICelestialObjectDB>();
            var swInit = System.Diagnostics.Stopwatch.StartNew();
            await objectDb.InitDBAsync(cancellationToken: cancellationToken);
            swInit.Stop();
            _logger.LogInformation("InitializePlanner: catalog ready in {Elapsed}ms; publishing to PlannerState.ObjectDb",
                swInit.ElapsedMilliseconds);
            _plannerState.ObjectDb = objectDb;
            _plannerState.NeedsRedraw = true;
            SetAutoCompleteCache(PlannerActions.BuildAutoCompleteList(objectDb, null));

            // Comet ephemerides load in the background (cache read, else an SBDB fetch) so the Sky Atlas
            // opens immediately; the markers appear as soon as the element set is in. Tracked (not raw
            // fire-and-forget) so a network failure is logged, and a redraw is poked on completion.
            var comets = _sp.GetRequiredService<ICometRepository>();
            _plannerState.Comets = comets;
            _tracker.Run(async () =>
            {
                await comets.EnsureLoadedAsync(cancellationToken);
                // Rebuild the autocomplete cache now that comet keys exist, so the planner-tab search
                // suggests comets too (atomic string[] reference swap; the render thread reads the field).
                SetAutoCompleteCache(PlannerActions.BuildAutoCompleteList(objectDb, comets));
                _plannerState.NeedsRedraw = true;
            }, "Load comet ephemerides");
            await PlannerActions.ComputeTonightsBestAsync(
                _plannerState, objectDb, transform,
                _plannerState.MinHeightAboveHorizon, cancellationToken, comets: comets);
            if (_appState.ActiveProfile is { } profile)
            {
                await PlannerPersistence.TryLoadAsync(_plannerState, profile, _external, _logger, _timeProvider, ActiveRemoteBindingId, cancellationToken);
            }

            // Bound rigs, so the profile picker lists them even before discovery has seen one this run --
            // a rig that is switched off must still appear (as offline), not silently disappear.
            _rigs.SetBindings(await RemoteRigPersistence.LoadAllAsync(_external, _logger, cancellationToken));

            // Then start mirroring all of them, WITHOUT putting any on screen. A binding alone has no
            // live state, so until a mirror runs there is nothing to show per rig; sweeping here is what
            // makes a multi-rig view possible and makes the picker's online/offline labels true rather
            // than inferred from a stale address. Previews stay off (opt-in per mirror), so the cost is
            // one small state poll per rig per tick. No-op when nothing is bound, which is the common
            // single-scope case.
            var sweep = await RemoteRigActions.ConnectAllAsync(
                _rigs, _contexts, _appState, _external, _timeProvider, _logger, cancellationToken);
            if (sweep.DescribeUnsaved() is { } unsavedWarning)
            {
                // Nobody asked for this sweep, so a failure in it has no other way to reach the user --
                // and its consequence (a rig that vanishes from the picker on some later run) shows up
                // far from the cause.
                Notify(NotificationSeverity.Warning, unsavedWarning);
            }
            await FetchWeatherForecastAsync(cancellationToken);
            _plannerState.SelectedTargetIndex = 0;
            RefreshSensorFovAndFraming();
            _plannerState.NeedsRedraw = true;
            _logger.LogInformation("InitializePlanner: complete");
        }

        /// <summary>
        /// Pushes the active profile's primary-OTA sensor FOV onto the planner and recomputes the smart
        /// framing groups. Called on planner (re)init, profile-recompute, and right after a camera connect
        /// captures sensor geometry -- the three moments the FOV or proposal set can change out of band.
        /// </summary>
        private void RefreshSensorFovAndFraming()
        {
            _plannerState.SensorFovDeg = ProfileOnShow?.Data is { } pd ? pd.PrimarySensorFovDeg : null;
            PlannerActions.ComputeFramingGroups(_plannerState);
        }

        // The node's read time of the last sample taken of each camera, keyed by URI path (host+path, no query), so a
        // reading is sampled once however many frames show it. Telemetry-poll-only: written only by PollCameraTelemetry,
        // on the UI thread.
        private readonly Dictionary<string, DateTimeOffset> _telemetryLastRead = new();

        /// <summary>
        /// Samples connected cameras' cooler and temperature telemetry into <see cref="EquipmentTabState.CameraTelemetry"/>,
        /// from what this computer's node last read of each (P6: its device model, kept by its reads and pushes), never from
        /// a camera: one sample per reading, at the node's cadence. Call once per frame from the host's main loop; only on
        /// the Equipment tab, where the sparkline is.
        /// </summary>
        public void PollCameraTelemetry()
        {
            if (_appState.LocalNode is not { } node || _appState.ActiveTab is not GuiTab.Equipment) return;

            foreach (var (key, device) in node.Devices)
            {
                if (device is not { Connected: true, Camera: { } camera, ReadUtc: { } readUtc }
                    || _telemetryLastRead.TryGetValue(key, out var last) && last >= readUtc)
                {
                    continue;
                }
                _telemetryLastRead[key] = readUtc;

                if (!_eqState.CameraTelemetry.TryGetValue(key, out var buffer))
                {
                    buffer = new CameraTelemetryBuffer();
                    _eqState.CameraTelemetry[key] = buffer;
                }
                var reading = camera.ToReading();
                buffer.Add(new CameraTelemetrySample(readUtc, camera.CcdTemperatureC, camera.HeatsinkTemperatureC, camera.SetpointC,
                    camera.CoolerPowerPercent, reading.CoolerOn, reading.IsBusy));
                _appState.NeedsRedraw = true;
            }
        }

        /// <summary>
        /// Stamps <see cref="RemoteRigBinding.LastSeenUtc"/> the first time each connected rig actually
        /// answers, so an offline rig can report its age after a restart.
        /// <para>
        /// One write per rig per run: <see cref="RemoteRigConnection.TryClaimFirstContact"/> is a
        /// one-shot, so this is a couple of reference comparisons per frame in the steady state and the
        /// save itself goes through <see cref="RunTracked"/> rather than touching the disk on the render
        /// thread. The matching flush on quit is what keeps the stamp current for a long watch.
        /// </para>
        /// </summary>
        private void PersistFirstContacts()
        {
            var connections = _rigs.Connections;
            if (connections.IsEmpty) return;

            foreach (var (_, connection) in connections)
            {
                if (!connection.TryClaimFirstContact()) continue;

                var reached = connection.BindingAsReached();
                _rigs.Upsert(reached);
                RunTracked("PersistRigLastSeen", $"Could not record when {reached.Alias} was last seen",
                    async ct => await RemoteRigPersistence.SaveAsync(reached, _external, ct));
            }
        }

        /// <summary>
        /// Records how recently every connected rig answered, for the quit path.
        /// <para>
        /// Without this a rig watched from dusk to dawn would persist only its first-contact stamp and
        /// report "last seen 9 h ago" the next morning, having in fact been alive until minutes before.
        /// Best-effort per rig -- one unwritable file must not stop the others, and nothing here may
        /// throw into a shutdown sequence that still has cameras to warm.
        /// </para>
        /// <para>
        /// Takes its own token rather than using <c>_cts</c>: that one is cancelled as part of quitting,
        /// which would cancel this flush before it ran.
        /// </para>
        /// </summary>
        public async Task FlushRigLastSeenAsync(CancellationToken cancellationToken)
        {
            var written = 0;
            var attempted = 0;

            foreach (var (_, connection) in _rigs.Connections)
            {
                var reached = connection.BindingAsReached();
                if (reached.LastSeenUtc is null) continue; // never answered this run; nothing to record

                attempted++;
                _rigs.Upsert(reached);
                if (await _logger.CatchAsync(
                        ct => RemoteRigPersistence.SaveAsync(reached, _external, ct), cancellationToken))
                {
                    written++;
                }
            }

            if (attempted > 0)
            {
                _logger.LogDebug("Recorded last-seen for {Written}/{Attempted} connected rig(s)", written, attempted);
            }
        }

        /// <summary>
        /// Tells every node this window reads, this computer's and each connected rig's, that it can SEE a prompt (P1 of
        /// docs/plans/hardware-in-the-server.md, #917): called by the host from the loop that draws it, every iteration, and
        /// never while its display is lost. A node holds a prompt only for a client whose beat is fresh, so a frozen window
        /// stops holding a night, this computer's included now that its runs are its node's (P6). It records only (each
        /// stream sends at most one beat a second).
        /// </summary>
        public void BeatNodes()
        {
            _appState.LocalNode?.Mirror.Beat();

            var connections = _rigs.Connections;
            if (connections.IsEmpty)
            {
                return;
            }

            foreach (var (_, connection) in connections)
            {
                connection.Mirror.Beat();
            }
        }

        /// <summary>
        /// Keeps each connected rig's profile label current, so a card can name the optical train a rig is
        /// running rather than just its address.
        /// <para>
        /// Gated on the connection's own synchronous due-check so the common frame does no work at all and
        /// allocates nothing -- the actual request is minutes apart per rig. A change redraws, because
        /// otherwise a relabelled card would not appear until something else happened to dirty the frame.
        /// </para>
        /// </summary>
        private void RefreshNodeProfiles()
        {
            // This computer's node the same way (P6): its profile is the app's active profile, which another client of the
            // node may have edited or switched.
            if (_appState.LocalNode is { ProfileRefreshDue: true } local)
            {
                RefreshProfileOf(local, LocalNodeName);
            }
            foreach (var (_, connection) in _rigs.Connections)
            {
                if (connection.ProfileRefreshDue)
                {
                    RefreshProfileOf(connection, connection.Binding.Alias);
                }
            }
        }

        /// <summary>What this computer's node is called in a note about it.</summary>
        private const string LocalNodeName = "this computer's node";

        private void RefreshProfileOf(NodeConnection connection, string name) =>
            RunTracked("RefreshNodeProfile", $"Could not read which profile {name} runs",
                async ct =>
                {
                    var previous = _appState.ActiveProfile?.ProfileId;
                    if (await connection.MaybeRefreshProfileAsync(ct).ConfigureAwait(false))
                    {
                        // The view on show plans with its profile (ProfileOnShow): a new one is a new site or sensor.
                        await OnNodeProfileChangedAsync(connection, previous, ct).ConfigureAwait(false);
                    }
                });

        /// <summary>
        /// The views' devices, read from their nodes (P5b part 9, P6). This computer's on every tab: they are what the
        /// Equipment tab connects, the Home card counts and the idle Live Session lays out, and a read is of the node's own
        /// last readings (it never reads a device for a client), so it costs no device anything. The rig on show while its
        /// node runs nothing: the OTA panels and the mount of its idle Live Session, and the reticle of its sky map. The
        /// cadence and the one-at-a-time rule are the connection's (<see cref="NodeConnection.MaybeRefreshDevicesAsync"/>),
        /// and the pushes between (<c>DEVICE-STATE</c>) keep them current meanwhile.
        /// </summary>
        private void PollNodeDevices()
        {
            if (_appState.LocalNode is { DevicesRefreshDue: true } local)
            {
                RefreshDevicesOf(local, LocalNodeName);
            }

            if (_contexts.Active is not { IsLocal: false } view || view.LiveSession.IsRunning
                || _appState.ActiveTab is not (GuiTab.LiveSession or GuiTab.SkyMap))
            {
                return;
            }
            foreach (var (_, connection) in _rigs.Connections)
            {
                if (ReferenceEquals(connection.Context, view) && connection.DevicesRefreshDue)
                {
                    RefreshDevicesOf(connection, connection.Binding.Alias);
                }
            }
        }

        private void RefreshDevicesOf(NodeConnection connection, string name) =>
            RunTracked("RefreshNodeDevices", $"Could not read the devices of {name}",
                async ct =>
                {
                    if (await connection.MaybeRefreshDevicesAsync(ct).ConfigureAwait(false))
                    {
                        _appState.NeedsRedraw = true;
                    }
                });

        /// <summary>
        /// The per-frame state mirroring of every view: the rig list and the Home board, each node's profile and devices (the
        /// idle panels, the mount's reticle and its limit verdict are laid out from the devices by the node's connection,
        /// <see cref="RigDevices"/>, P6 of docs/plans/hardware-in-the-server.md). Reads no device: the node does, at the GUI's
        /// cadences, and a read of it is of its last readings. Call once per frame from the host's main loop.
        /// </summary>
        public void PollPreviewTelemetry()
        {
            // Mirror the rig list into the Equipment tab BEFORE the early-returns below: the picker has to
            // list bound rigs on every tab and while a local session runs, which is precisely when those
            // returns fire. Two plain reference/bool assignments, no device I/O.
            _eqState.BoundRigs = _rigs.Bindings;
            _eqState.RemoteContextActive = _contexts.IsRemoteActive;
            PersistFirstContacts();

            // The home board, published the same way and for the same reason: it must be current on every
            // tab, and the tab renders a snapshot rather than reaching into the rig registry itself.
            //
            // Deliberately ABOVE the ActiveTab gate below, and deliberately NOT added to it. Everything
            // here is state mirroring and one throttled HTTP read per rig; the gate guards polling
            // already-connected DRIVERS, so keeping the board on this side of it is what makes "the home
            // screen does zero device I/O" a property that can be stated rather than argued.
            NotifyLimitTransitions(); // first: it also refreshes the local verdict the cards below read
            _appState.HomeCards = HomeBoard.BuildCards(_contexts, _rigs, _appState, _timeProvider.GetUtcNow());
            RefreshNodeProfiles();
            RefreshNodeAccess();
            PollNodeDevices();
        }


        /// <summary>
        /// Checks <see cref="PlannerState.NeedsRecompute"/> and triggers a background recompute
        /// if needed. Call from the host's main loop each frame.
        /// </summary>
        public void CheckRecompute()
        {
            // Another view on show plans with another profile, and keeps its own pins: replan, in full.
            var view = _contexts.Active;
            if (!ReferenceEquals(view, _plannedFor))
            {
                _plannerState.NeedsRecompute = true;
            }

            if (!_plannerState.NeedsRecompute || ProfileOnShow is not { } profileOnShow || _plannerState.IsRecomputing)
            {
                return;
            }

            var viewChanged = !ReferenceEquals(view, _plannedFor);
            _plannedFor = view;
            _plannerState.NeedsRecompute = false;
            _plannerState.IsRecomputing = true;
            _appState.StatusMessage = "Recomputing...";
            _appState.NeedsRedraw = true;
            _tracker.Run(async () =>
            {
                try
                {
                    var objectDb = _sp.GetRequiredService<ICelestialObjectDB>();
                    var transform = TransformFactory.FromProfile(profileOnShow, _timeProvider, out _);

                    if (transform is not null)
                    {
                        // Override transform date if planning for a different night
                        if (_plannerState.PlanningDate is { } pd)
                        {
                            var noon = new DateTimeOffset(pd.Date, pd.Offset).AddHours(12);
                            transform.DateTimeOffset = noon;
                        }

                        // Detect significant site change (>1°): requires full rescan
                        var siteChanged = double.IsNaN(_plannerState.SiteLatitude)
                            || Math.Abs(transform.SiteLatitude - _plannerState.SiteLatitude) > 1.0
                            || Math.Abs(transform.SiteLongitude - _plannerState.SiteLongitude) > 1.0;

                        ApplySiteFromTransform(_plannerState, transform);

                        if (_plannerState.TonightsBest.Length > 0 && !siteChanged && !viewChanged)
                        {
                            PlannerActions.RecomputeForDate(_plannerState, transform);
                        }
                        else
                        {
                            await PlannerActions.ComputeTonightsBestAsync(
                                _plannerState, objectDb, transform,
                                _plannerState.MinHeightAboveHorizon, _cts.Token, comets: _plannerState.Comets);
                            // The pins of the view on show: a rig's are its own, kept per binding. A load only replaces the
                            // pins when the view has some saved, so another view's are dropped first: a rig with none of its
                            // own showed this computer's, and the next save wrote them into the rig's file.
                            if (viewChanged)
                            {
                                _plannerState.Proposals = [];
                                PlannerActions.RecomputeHandoffSliders(_plannerState);
                            }
                            await PlannerPersistence.TryLoadAsync(_plannerState, profileOnShow, _external, _logger, _timeProvider, ActiveRemoteBindingId, _cts.Token);
                            SetAutoCompleteCache(PlannerActions.BuildAutoCompleteList(objectDb, _plannerState.Comets));
                        }
                        await FetchWeatherForecastAsync(_cts.Token);
                        _appState.StatusMessage = null;
                        RefreshSensorFovAndFraming();
                    }
                    else
                    {
                        Notify(NotificationSeverity.Warning, "Set site coordinates in Equipment tab");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Recompute failed");
                    Notify(NotificationSeverity.Error, $"Recompute failed: {ex.InnerException?.Message ?? ex.Message}");
                }
                finally
                {
                    _plannerState.IsRecomputing = false;
                    _appState.NeedsRedraw = true;
                }
            }, "Recompute targets");
        }

        /// <summary>
        /// Refreshes the planning night's weather band and the night calendar behind the status-bar date, from one
        /// multi-day forecast (<see cref="NightCalendarActions.RefreshAsync"/>). Non-fatal.
        /// </summary>
        private async Task FetchWeatherForecastAsync(CancellationToken ct)
        {
            await NightCalendarActions.RefreshAsync(_plannerState, ProfileOnShow, _sp, _timeProvider,
                _logger, ct);
            _appState.NeedsRedraw = true;
        }

        /// <summary>
        /// Loads saved session configuration for the active profile.
        /// Call via <see cref="BackgroundTaskTracker.Run"/> from the host.
        /// </summary>
        public async Task LoadSessionConfigAsync(CancellationToken cancellationToken)
        {
            if (_appState.ActiveProfile is { } profile)
            {
                await SessionPersistence.TryLoadAsync(_sessionState, profile, _external, cancellationToken, _appState.CameraCapabilitiesOf);
            }
        }

        public AppSignalHandler(
            IServiceProvider sp,
            GuiAppState appState,
            PlannerState plannerState,
            SessionTabState sessionState,
            EquipmentTabState eqState,
            ViewContexts contexts,
            SkyMapState skyMapState,
            SignalBus bus,
            BackgroundTaskTracker tracker,
            CancellationTokenSource cts,
            CancellationToken shutdownToken,
            IExternal external)
        {
            _sp = sp;
            _appState = appState;
            _plannerState = plannerState;
            _sessionState = sessionState;
            _eqState = eqState;
            _contexts = contexts;
            _skyMapState = skyMapState;
            _bus = bus;
            _tracker = tracker;
            _cts = cts;
            _external = external;

            // Single site-timezone source: planner + live-session read through to
            // GuiAppState.SiteTimeZone so every time display shares one value and the
            // two states cannot drift apart (see PlannerState.AttachAppState).
            plannerState.AttachAppState(appState);
            contexts.AttachAppState(appState);
            // The planner's first plan is this computer's own, which the host's planner start makes: not a switch of view.
            _plannedFor = contexts.Local;

            _logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AppSignalHandler));
            _timeProvider = sp.GetRequiredService<ITimeProvider>();
            // A host without a credential store (a test's) keeps grants for the process only.
            appState.NodeGrants ??= new NodeGrants(sp.GetService<ICredentialStore>(), _logger);

            // Subscription groups live in the by-area partials (AppSignalHandler.Planner.cs /
            // .SkyMap.cs / .Equipment.cs / .LiveSession.cs / .Polar.cs / .Flats.cs). Order is
            // load-bearing: SignalBus invokes subscribers in registration order.
            SubscribePlannerSearch(bus);
            SubscribeNightCalendar(bus);
            SubscribeSkyMap(bus);
            SubscribeEquipmentTextInputs(bus);
            SubscribeEquipmentActions(bus);
            SubscribeScheduleBuilding(bus);
            SubscribeLiveSession(bus);
            SubscribeAccess(bus);
            SubscribePolarAlignment(bus);
            SubscribeFlats(bus);
            SubscribePreview(bus, shutdownToken);

            // Theme is app state, not host state, so it lives here rather than in a host's own
            // subscriptions the way SDL text input does -- the TUI and web hosts get F12 for free by
            // posting the same signal. GuiTheme owns the remember-and-restore, so this only routes.
            bus.Subscribe<ToggleNightModeSignal>(_ =>
            {
                GuiTheme.ToggleNight();
                _appState.NeedsRedraw = true;
            });

            bus.Subscribe<CycleUiThemeSignal>(_ =>
            {
                GuiTheme.CycleTheme();
                _appState.NeedsRedraw = true;
            });

            // Store autocomplete cache setter as a public action
            SetAutoCompleteCache = cache => _autoCompleteCache = cache;
        }

        // -----------------------------------------------------------------------------
        // Routing helpers (see docs/plans/signal-handler-boilerplate.md).
        // Instance methods so the ctor's subscribe lambdas reach them through captured
        // `this`; they hold no state beyond the injected fields every handler already uses.
        // -----------------------------------------------------------------------------

        /// <summary>
        /// Records a notification stamped with the current time and kicks an
        /// <see cref="_appState"/> redraw. Replaces the
        /// <c>appState.AppendNotification(_timeProvider.GetUtcNow(), ...)</c> ceremony so the
        /// timestamp can never be forgotten. The redraw deliberately targets only
        /// <see cref="_appState"/>: a handler whose surface also needs the kick (sky map, live
        /// session, planner) sets that state's <c>NeedsRedraw</c> explicitly at the call site
        /// -- a redraw-everything hammer would mask missing-redraw bugs and cost frames.
        /// </summary>
        // P4 of mount-safety-limits.md: the limit reaches the feed the moment its verdict changes CLASS --
        // clear -> warning -> acted, or the driver stopping the mount -- and never per poll, since the same
        // verdict repeats every tick for as long as the rig sits in the limit. The latch's downgrade to Warn
        // after acting leaves IsWarningOnly false, so it is not a change here. Only the LOCAL rig: a remote
        // node's own feed carries its limit (EventBroadcaster does the same there) and rides in on its card.
        private (MountLimitKind Kind, bool WarningOnly) _lastLimitClass;

        /// <summary>
        /// Once per frame, by the host: the GUI from <see cref="PollPreviewTelemetry"/>, the TUI from its loop. The local
        /// verdict is the node's (a session's own, else its device plane's, laid out with the devices); a no-op between class
        /// changes.
        /// </summary>
        public void NotifyLimitTransitions()
        {
            var verdict = LocalLiveSession.MountLimitVerdict;
            var cls = (verdict.Kind, verdict.IsWarningOnly);
            if (cls == _lastLimitClass)
            {
                return;
            }
            var wasBreached = _lastLimitClass.Kind is not MountLimitKind.None;
            _lastLimitClass = cls;
            if (!verdict.IsBreached)
            {
                if (wasBreached)
                {
                    Notify(NotificationSeverity.Info, "Mount is clear of its safety limits again.");
                }
                return;
            }
            Notify(verdict.IsWarningOnly ? NotificationSeverity.Warning : NotificationSeverity.Error,
                $"Mount safety limit: {verdict.Describe()}");
        }

        private void Notify(NotificationSeverity severity, string message)
        {
            _appState.AppendNotification(_timeProvider.GetUtcNow(), severity, message);
            _appState.NeedsRedraw = true;

            // Also LOG anything that went wrong. The notification feed is in-memory and dies with the
            // process, so routing the only account of a failure through it means that by the time
            // anyone reads a log to find out what happened -- which is what a log is for -- there is
            // nothing there. A plate solve that failed overnight is the case that motivated this.
            // Info stays feed-only: those are routine progress messages and would just be noise.
            switch (severity)
            {
                case NotificationSeverity.Error:
                    _logger.LogError("{Message}", message);
                    break;
                case NotificationSeverity.Warning:
                    _logger.LogWarning("{Message}", message);
                    break;
            }
        }

        /// <summary>
        /// Guards a run that would drive THIS node's hardware while a remote rig is on screen. Every
        /// handler here acts locally, so starting one from a remote view would silently run a local
        /// session behind a remote overlay -- the failure the local/active split exists to prevent.
        /// Applied to the three run-starting handlers (session, flats, polar alignment); starting a run
        /// ON a rig routes through its API instead (docs/plans/remote-profile.md P4). Also applied to every
        /// device action a remote view reaches (P0b item 9 of docs/plans/hardware-in-the-server.md, #752):
        /// planetary Start, the mount nudges, Goto, Solve and Sync, and the focuser's jog and goto, each of
        /// which drove THIS computer's rig from a remote rig's panel. P6 routes each to its context's node.
        /// </summary>
        /// <summary>
        /// Stops the run on the view on screen through its node (P5b part 5, P6): a rig's, and this computer's, whose runs are
        /// its node's since the cut. The node's abort ends a session's or a flat run's run alike through its own Finalise. A
        /// rig's ABORT and a flat run's Cancel used to act on THIS computer's run.
        /// </summary>
        private void StopActiveRun(string what)
        {
            var active = _contexts.Active;
            if (active.Mirror is not { } mirror)
            {
                if (active.IsLocal)
                {
                    LocalNodeOrSay();
                }
                else
                {
                    Notify(NotificationSeverity.Warning, $"{what}: '{active.DisplayName}' is not connected");
                }
                return;
            }
            var where = active.IsLocal ? "this computer's node" : $"'{active.DisplayName}'";
            _tracker.Run(async () =>
            {
                var sent = await mirror.AbortAsync(_cts.Token);
                Notify(sent.IsSuccess ? NotificationSeverity.Info : NotificationSeverity.Warning, sent.IsSuccess
                    ? $"{what}: sent to {where}"
                    : $"{what}: {where} did not take it ({sent.Error})");
            }, what);
        }

        private bool EnsureLocalContext(string what)
        {
            if (!_contexts.IsRemoteActive)
            {
                return true;
            }
            Notify(NotificationSeverity.Warning,
                $"{what} runs on this computer; switch back from '{_contexts.Active.DisplayName}' first");
            return false;
        }

        /// <summary>
        /// Submits <paramref name="work"/> to the background tracker under <paramref name="name"/> with
        /// the standard error surface: any exception notifies (Error)
        /// "<paramref name="failurePrefix"/>: {message}"; cancellation notifies (Warning)
        /// <paramref name="cancelMessage"/> when non-null (log-only otherwise); and
        /// <paramref name="onFinally"/> (busy-flag clear + redraws) always runs. The generic
        /// run/log/route/finally scaffold lives in <see cref="BackgroundTaskTracker.RunGuarded"/> (it is
        /// not TianWen-specific); this only wires the notification callbacks. The exception is caught in
        /// the tracker, so the task completes non-faulted and its own ProcessCompletions LogError does not
        /// double-fire. The log records <paramref name="name"/> (which already encodes per-call context
        /// like the OTA index or jog direction), so no structured detail is lost. Keep the handler's sync
        /// prefix (busy flag, tab switch, status message) inline BEFORE this call so the render-thread /
        /// background split point stays explicit.
        /// </summary>
        private void RunTracked(
            string name,
            string failurePrefix,
            Func<CancellationToken, Task> work,
            Action? onFinally = null,
            string? cancelMessage = null)
            => _tracker.RunGuarded(
                work, _cts.Token, _logger, name,
                onError: ex => Notify(NotificationSeverity.Error, $"{failurePrefix}: {ex.Message}"),
                onCancel: cancelMessage is null ? null : () => Notify(NotificationSeverity.Warning, cancelMessage),
                onFinally: onFinally);
    }
}
