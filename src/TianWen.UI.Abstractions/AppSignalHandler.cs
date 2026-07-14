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
    /// <see cref="SignalBus.PostSignal{T}"/> enqueues the signal — it never delivers inline,
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
        private readonly LiveSessionState _liveSessionState;
        private readonly ILogger _logger;
        private readonly ITimeProvider _timeProvider;
        private readonly EquipmentTabState _eqState;
        private readonly SkyMapState _skyMapState;

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
                await PlannerPersistence.TryLoadAsync(_plannerState, profile, _external, _logger, _timeProvider, cancellationToken);
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
            _plannerState.SensorFovDeg = _appState.ActiveProfile?.Data is { } pd ? pd.PrimarySensorFovDeg : null;
            PlannerActions.ComputeFramingGroups(_plannerState);
        }

        // Per-camera last-poll timestamps, keyed by URI path (host+path, no query).
        // UI-thread-only; never touched from tracker continuations.
        private readonly Dictionary<string, long> _telemetryLastSampleTicks = new();
        // In-flight poll task per camera path, prevents overlapping samples.
        // Touched from both the UI thread (Add) and tracker continuations (Remove),
        // so it MUST be a concurrent collection — a plain HashSet/Dictionary will
        // crash with IndexOutOfRangeException on a bucket-array race.
        private readonly ConcurrentDictionary<string, byte> _telemetryInFlight = new();

        // Preview-mode poll state (mirrors _telemetryLastSampleTicks pattern;
        // also UI-thread-only — never touched from tracker continuations).
        private readonly Dictionary<string, long> _previewTelemetryLastTicks = new();
        private readonly ConcurrentDictionary<string, byte> _previewTelemetryInFlight = new();
        private long _previewMountLastTicks;
        // 0 = idle, 1 = poll in flight. Uses Interlocked because the UI thread checks
        // and sets it before kicking off the tracker task, and the tracker continuation
        // clears it on completion — same UI/background split as the telemetry sets.
        private int _previewMountInFlight;
        private bool _loggedFirstPreviewMountSample;

        // Mount-reticle poll cadence ramps down instead of snapping straight to the slow
        // steady rate the instant tracking is detected. Fast while slewing, fast for a
        // settle window after the mount lands and starts tracking, then relaxing to the
        // slow steady cadence only once it has tracked undisturbed for that window.
        // Sidereal tracking is sub-pixel on any sky-map FOV, so the slow cadence is purely
        // a serial-load saver - the ramp means the reticle never lags a deliberate move by
        // up to the steady interval. _steadyTrackingSinceTicks holds the timestamp steady
        // tracking began (0 = not steady); _wasSteadyTracking is last frame's flag for the
        // transition edge. Both are UI-thread-only - read + written only in the
        // PollPreviewTelemetry gate, never from tracker continuations.
        private long _steadyTrackingSinceTicks;
        private bool _wasSteadyTracking;
        // Set by RequestPreviewMountRefresh (called from the goto / solve-and-sync tracker
        // continuations) to force the next poll tick to sample immediately, bypassing the
        // interval so a deliberate move lands on the reticle within a frame. Volatile
        // because the setter runs on a background thread while the gate reads it on the UI
        // thread; consumed (cleared) only once a poll actually starts.
        private int _forcePreviewMountPoll;

        // Mount-reticle poll intervals (see _steadyTrackingSinceTicks). Slewing keeps up
        // with visible motion; Settling is the fast post-landing rate held for the settle
        // window; Steady is the relaxed sidereal rate; Idle covers parked / not-tracking.
        private static readonly TimeSpan MountPollSlewing = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan MountPollSettling = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MountPollSteady = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MountPollIdle = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MountTrackingSettleWindow = TimeSpan.FromSeconds(10);


        /// <summary>
        /// Polls connected cameras for cooler/temperature telemetry and appends samples
        /// to <see cref="EquipmentTabState.CameraTelemetry"/>. Call once per frame from the
        /// host's main loop. Internally rate-limits per-camera so polling stays at ~2s,
        /// regardless of frame rate. Cheap to call when nothing needs sampling.
        /// </summary>
        public void PollCameraTelemetry()
        {
            if (_appState.DeviceHub is not { } hub) return;
            // Only on the equipment tab — avoids hammering cameras with reads when the
            // user isn't looking at the data. (Live-session view will be wired later.)
            if (_appState.ActiveTab is not GuiTab.Equipment) return;

            var nowTicks = _timeProvider.GetTimestamp();
            var sampleInterval = TimeSpan.FromSeconds(2);

            foreach (var (uri, driver) in hub.ConnectedDevices)
            {
                if (driver is not TianWen.Lib.Devices.ICameraDriver) continue;
                var key = uri.GetLeftPart(UriPartial.Path);

                if (_telemetryInFlight.ContainsKey(key)) continue;
                if (_telemetryLastSampleTicks.TryGetValue(key, out var lastTicks)
                    && _timeProvider.GetElapsedTime(lastTicks, nowTicks) < sampleInterval)
                {
                    continue;
                }

                _telemetryLastSampleTicks[key] = nowTicks;
                _telemetryInFlight.TryAdd(key, 0);
                var capturedUri = uri;
                _tracker.Run(async () =>
                {
                    try
                    {
                        var sample = await SampleCameraAsync(hub, capturedUri, _cts.Token);
                        if (sample is { } s)
                        {
                            if (!_eqState.CameraTelemetry.TryGetValue(key, out var buffer))
                            {
                                buffer = new CameraTelemetryBuffer();
                                _eqState.CameraTelemetry[key] = buffer;
                            }
                            buffer.Add(s);
                            _appState.NeedsRedraw = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Telemetry poll failed for {Uri}", capturedUri);
                    }
                    finally
                    {
                        _telemetryInFlight.TryRemove(key, out _);
                    }
                }, $"Telemetry {key}");
            }
        }

        private async Task<CameraTelemetrySample?> SampleCameraAsync(
            IDeviceHub hub, Uri uri, System.Threading.CancellationToken ct)
        {
            if (!hub.TryGetConnectedDriver<TianWen.Lib.Devices.ICameraDriver>(uri, out var camera))
            {
                return null;
            }

            double? ccd = null, sink = null, setpoint = null, power = null;
            bool coolerOn = false, busy = false;

            try
            {
                if (camera.CanGetCCDTemperature) ccd = await camera.GetCCDTemperatureAsync(ct);
            }
            catch { /* tolerate transient driver errors mid-sample */ }

            try
            {
                if (camera.CanGetHeatsinkTemperature) sink = await camera.GetHeatSinkTemperatureAsync(ct);
            }
            catch { }

            try
            {
                if (camera.CanGetCoolerOn) coolerOn = await camera.GetCoolerOnAsync(ct);
            }
            catch { }

            try
            {
                if (camera.CanGetCoolerPower) power = await camera.GetCoolerPowerAsync(ct);
            }
            catch { }

            try
            {
                setpoint = await camera.GetSetCCDTemperatureAsync(ct);
            }
            catch { }

            try
            {
                var state = await camera.GetCameraStateAsync(ct);
                busy = state is not (TianWen.Lib.Devices.CameraState.Idle or TianWen.Lib.Devices.CameraState.NotConnected);
            }
            catch { }

            return new CameraTelemetrySample(
                _timeProvider.GetUtcNow(),
                ccd, sink, setpoint, power, coolerOn, busy);
        }

        /// <summary>
        /// Polls connected devices for preview telemetry when the Live Session tab is visible
        /// and no session is running. Reads camera, focuser, filter wheel, and mount state
        /// from hub-connected drivers via the active profile's OTA configuration.
        /// Call once per frame from the host's main loop. Internally rate-limited.
        /// </summary>
        public void PollPreviewTelemetry()
        {
            if (_liveSessionState.IsRunning) return;

            // Preview polling drives the Live Session tab, the Sky Map tab (for the
            // mount-position reticle overlay), and the Equipment tab (for the mount
            // status expander). Any tab that displays live mount / focuser state should
            // be added here rather than spinning up a parallel poll path — two concurrent
            // polls on the same serial mount would race the port.
            if (_appState.ActiveTab is not (GuiTab.LiveSession or GuiTab.SkyMap or GuiTab.Equipment)) return;
            if (_appState.ActiveProfile?.Data is not { OTAs: { Length: > 0 } otas } profileData) return;
            if (_appState.DeviceHub is not { } hub) return;

            var nowTicks = _timeProvider.GetTimestamp();

            _liveSessionState.ResizePreviewArrays(otas.Length);

            // Per-OTA camera + focuser + filter polling — rate-adaptive on focuser state.
            // When the focuser is actively moving the user wants sub-second feedback on
            // the position readout; in steady state a 2s cadence is plenty (temperature
            // and filter changes are slow or user-triggered). The last-known moving flag
            // is up to one poll interval stale; the first tick after a move starts is
            // therefore up to 2s slow, which matches perceived click-to-refresh latency.
            for (var i = 0; i < otas.Length; i++)
            {
                var ota = otas[i];
                var key = ota.Camera.GetLeftPart(UriPartial.Path);

                if (_previewTelemetryInFlight.ContainsKey(key)) continue;

                var prevTelemetry = i < _liveSessionState.PreviewOTATelemetry.Length
                    ? _liveSessionState.PreviewOTATelemetry[i]
                    : default;
                var focuserMoving = prevTelemetry.FocuserIsMoving;
                var sampleInterval = focuserMoving ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(2);

                if (_previewTelemetryLastTicks.TryGetValue(key, out var last)
                    && _timeProvider.GetElapsedTime(last, nowTicks) < sampleInterval)
                {
                    continue;
                }

                _previewTelemetryLastTicks[key] = nowTicks;
                _previewTelemetryInFlight.TryAdd(key, 0);

                var capturedOta = ota;
                var capturedIndex = i;

                _tracker.Run(async () =>
                {
                    try
                    {
                        var telemetry = await LiveSessionActions.SampleOTATelemetryAsync(hub, capturedOta, _logger, _cts.Token);
                        var arr = _liveSessionState.PreviewOTATelemetry;
                        if (capturedIndex < arr.Length)
                        {
                            var builder = arr.ToBuilder();
                            builder[capturedIndex] = telemetry;
                            _liveSessionState.PreviewOTATelemetry = builder.ToImmutable();
                            _appState.NeedsRedraw = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Preview telemetry poll failed for OTA {Index}", capturedIndex);
                    }
                    finally
                    {
                        _previewTelemetryInFlight.TryRemove(key, out _);
                    }
                }, $"PreviewTelemetry OTA{capturedIndex}");
            }

            // Mount polling - rate-adaptive on last-known slew/tracking state, with a
            // ramp-down so the reticle stays responsive right after a move. Steady sidereal
            // tracking is ~15 arcsec/s, which is sub-pixel on any sky-map FOV we support, so
            // the slow steady cadence is purely a serial-load saver (6 reads per tick). But
            // snapping straight to it the instant tracking is detected made a freshly-landed
            // goto / sync lag by up to the steady interval. Instead: fast while slewing, fast
            // for MountTrackingSettleWindow after the mount lands and starts tracking, then
            // relaxing to the steady rate only once it has tracked undisturbed that long.
            var prevMount = _liveSessionState.MountState;
            var steadyNow = prevMount.IsTracking && !prevMount.IsSlewing;
            if (steadyNow && !_wasSteadyTracking)
            {
                _steadyTrackingSinceTicks = nowTicks;   // just entered steady tracking - start the settle clock
            }
            else if (!steadyNow)
            {
                _steadyTrackingSinceTicks = 0;           // moving or parked - cancel the settle clock
            }
            _wasSteadyTracking = steadyNow;

            TimeSpan mountInterval;
            if (prevMount.IsSlewing)
            {
                mountInterval = MountPollSlewing;
            }
            else if (steadyNow)
            {
                var settledFor = _steadyTrackingSinceTicks != 0
                    ? _timeProvider.GetElapsedTime(_steadyTrackingSinceTicks, nowTicks)
                    : TimeSpan.Zero;
                mountInterval = settledFor >= MountTrackingSettleWindow ? MountPollSteady : MountPollSettling;
            }
            else
            {
                mountInterval = MountPollIdle;
            }

            // A forced refresh (deliberate move just issued) bypasses the interval entirely.
            var forceNow = Volatile.Read(ref _forcePreviewMountPoll) == 1;
            if ((forceNow || _timeProvider.GetElapsedTime(_previewMountLastTicks, nowTicks) >= mountInterval)
                && profileData.Mount is { Scheme: not "none" } mountUri
                && Interlocked.CompareExchange(ref _previewMountInFlight, 1, 0) == 0)
            {
                // Consume the force flag only once a poll actually starts; if the in-flight
                // guard above lost (a poll is already running) the flag persists for next frame.
                Volatile.Write(ref _forcePreviewMountPoll, 0);
                _previewMountLastTicks = nowTicks;

                _tracker.Run(async () =>
                {
                    try
                    {
                        var (ms, displayName) = await SamplePreviewMountAsync(hub, mountUri, _cts.Token);
                        // One-shot diagnostic log the first time a preview mount sample comes
                        // back. Confirms RA/Dec reads work end-to-end against the driver
                        // without spamming the log every 2-10 seconds during steady tracking.
                        if (!_loggedFirstPreviewMountSample)
                        {
                            _loggedFirstPreviewMountSample = true;
                            _logger.LogInformation("Preview mount first sample: RA={RA:F4}h Dec={Dec:F4}° HA={HA:F4}h slewing={Slewing} tracking={Tracking}",
                                ms.RightAscension, ms.Declination, ms.HourAngle, ms.IsSlewing, ms.IsTracking);
                        }
                        _liveSessionState.MountState = ms;
                        if (displayName is not null)
                        {
                            _liveSessionState.MountDisplayName = displayName;
                        }
                        _appState.NeedsRedraw = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Preview mount poll failed");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _previewMountInFlight, 0);
                    }
                }, "PreviewMount");
            }
        }

        /// <summary>
        /// Forces the next <see cref="PollPreviewTelemetry"/> tick to sample the mount
        /// immediately, bypassing the cadence interval. Call after a deliberate move (a
        /// goto kicking off, a solve &amp; sync landing) so the reticle reflects the new
        /// pointing within a frame instead of waiting out the steady poll interval. The
        /// in-flight guard still serialises against any poll already running; if one is,
        /// the request persists until the next free tick. Safe to call from a background
        /// tracker continuation.
        /// </summary>
        private void RequestPreviewMountRefresh() => Volatile.Write(ref _forcePreviewMountPoll, 1);

        // De-duplicate the mount-transform-unavailable warning so a misconfigured
        // profile doesn't log once per poll. Reset to null to re-arm when the error
        // message changes or after a successful conversion (a future refinement).
        private string? _lastMountTransformError;

        private void LogMountTransformUnavailable(string? reason)
        {
            if (reason is null || reason == _lastMountTransformError)
            {
                return;
            }
            _lastMountTransformError = reason;
            _logger.LogWarning(
                "Mount J2000 conversion unavailable — reticle will fall back to native coords (silent {Offset:F2} deg shift). {Reason}",
                0.35, // approx topocentric<->J2000 shift for an epoch-2025 observer
                reason);
        }

        private async Task<(MountState State, string? DisplayName)> SamplePreviewMountAsync(
            IDeviceHub hub, Uri mountUri, CancellationToken ct)
        {
            if (!hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var mount) || mount is null)
            {
                return (default, null);
            }

            // Default to NaN (not the struct default 0.0) so a FAILED read is treated as "unavailable"
            // by the !IsNaN guard below, instead of being mistaken for a valid RA0/Dec0 and painted at
            // the celestial-equator origin (the "mount suddenly jumps to 0,0 / forgets its site" bug).
            var ra = await _logger.CatchAsync(mount.GetRightAscensionAsync, ct, double.NaN);
            var dec = await _logger.CatchAsync(mount.GetDeclinationAsync, ct, double.NaN);
            var ha = await _logger.CatchAsync(mount.GetHourAngleAsync, ct, double.NaN);
            var slewing = await _logger.CatchAsync(mount.IsSlewingAsync, ct);
            var tracking = await _logger.CatchAsync(mount.IsTrackingAsync, ct);
            var pier = await _logger.CatchAsync(mount.GetSideOfPierAsync, ct);

            // Derive J2000 coordinates for the sky-map overlay. J2000 mounts skip the
            // Transform entirely; topocentric mounts use a Transform built from the
            // profile's site coordinates (TransformFactory.FromProfile — zero hardware
            // I/O, unlike mount.TryGetTransformAsync which round-trips for lat/lon/elev).
            // We rebuild it on every poll because it's cheap (one object alloc + three
            // scalar assignments); the only genuinely expensive step is transform.Refresh()
            // which runs the SOFA kernel after SetTopocentric.
            var (raJ2000, decJ2000) = (double.NaN, double.NaN);
            if (!double.IsNaN(ra) && !double.IsNaN(dec))
            {
                if (mount.EquatorialSystem == EquatorialCoordinateType.J2000)
                {
                    (raJ2000, decJ2000) = (ra, dec);
                }
                else if (mount.EquatorialSystem == EquatorialCoordinateType.Topocentric
                    && _appState.ActiveProfile is { } profile)
                {
                    if (TransformFactory.FromProfile(profile, _timeProvider, out var transformError) is { } transform)
                    {
                        transform.SetTopocentric(ra, dec);
                        transform.Refresh();
                        raJ2000 = transform.RAJ2000;
                        decJ2000 = transform.DecJ2000;
                    }
                    else
                    {
                        LogMountTransformUnavailable(transformError);
                    }
                }
            }

            string? displayName = null;
            if (hub.TryGetDeviceFromUri(mountUri, out var dev) && dev is not null)
            {
                displayName = dev.DisplayName;
            }

            return (new MountState(ra, dec, ha, pier, slewing, tracking, raJ2000, decJ2000), displayName);
        }

        /// <summary>
        /// Checks <see cref="PlannerState.NeedsRecompute"/> and triggers a background recompute
        /// if needed. Call from the host's main loop each frame.
        /// </summary>
        public void CheckRecompute()
        {
            if (!_plannerState.NeedsRecompute || _appState.ActiveProfile is null || _plannerState.IsRecomputing)
            {
                return;
            }

            _plannerState.NeedsRecompute = false;
            _plannerState.IsRecomputing = true;
            _appState.StatusMessage = "Recomputing...";
            _appState.NeedsRedraw = true;
            _tracker.Run(async () =>
            {
                try
                {
                    var objectDb = _sp.GetRequiredService<ICelestialObjectDB>();
                    var transform = TransformFactory.FromProfile(_appState.ActiveProfile, _timeProvider, out _);

                    if (transform is not null)
                    {
                        // Override transform date if planning for a different night
                        if (_plannerState.PlanningDate is { } pd)
                        {
                            var noon = new DateTimeOffset(pd.Date, pd.Offset).AddHours(12);
                            transform.DateTimeOffset = noon;
                        }

                        // Detect significant site change (>1°) — requires full rescan
                        var siteChanged = double.IsNaN(_plannerState.SiteLatitude)
                            || Math.Abs(transform.SiteLatitude - _plannerState.SiteLatitude) > 1.0
                            || Math.Abs(transform.SiteLongitude - _plannerState.SiteLongitude) > 1.0;

                        ApplySiteFromTransform(_plannerState, transform);

                        if (_plannerState.TonightsBest.Length > 0 && !siteChanged)
                        {
                            PlannerActions.RecomputeForDate(_plannerState, transform);
                        }
                        else
                        {
                            await PlannerActions.ComputeTonightsBestAsync(
                                _plannerState, objectDb, transform,
                                _plannerState.MinHeightAboveHorizon, _cts.Token, comets: _plannerState.Comets);
                            if (_appState.ActiveProfile is { } profile)
                            {
                                await PlannerPersistence.TryLoadAsync(_plannerState, profile, _external, _logger, _timeProvider, _cts.Token);
                            }
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
        /// Fetches weather forecast for the current night window if a weather device is assigned in the profile.
        /// Non-fatal: silently logs and clears forecast on failure.
        /// </summary>
        private async Task FetchWeatherForecastAsync(CancellationToken ct)
        {
            if (_appState.ActiveProfile?.Data is not { Weather: { } weatherUri } data
                || weatherUri == NoneDevice.Instance.DeviceUri)
            {
                _plannerState.WeatherForecast = null;
                return;
            }

            if (double.IsNaN(_plannerState.SiteLatitude) || double.IsNaN(_plannerState.SiteLongitude))
            {
                _plannerState.WeatherForecast = null;
                return;
            }

            try
            {
                var device = EquipmentActions.TryDeviceFromUri(weatherUri);
                if (device is null || !device.TryInstantiateDriver<IWeatherDriver>(_sp, out var weatherDriver))
                {
                    _plannerState.WeatherForecast = null;
                    return;
                }

                using (weatherDriver)
                {
                    var tStart = _plannerState.CivilSet ?? _plannerState.AstroDark - TimeSpan.FromHours(1);
                    var tEnd = _plannerState.CivilRise ?? _plannerState.AstroTwilight + TimeSpan.FromHours(1);
                    _plannerState.WeatherForecast = await weatherDriver.GetHourlyForecastAsync(
                        _plannerState.SiteLatitude, _plannerState.SiteLongitude,
                        tStart, tEnd, ct);
                    _plannerState.NeedsRedraw = true;
                    _appState.NeedsRedraw = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Weather forecast fetch failed");
                _plannerState.WeatherForecast = null;
            }
        }

        /// <summary>
        /// Loads saved session configuration for the active profile.
        /// Call via <see cref="BackgroundTaskTracker.Run"/> from the host.
        /// </summary>
        public async Task LoadSessionConfigAsync(CancellationToken cancellationToken)
        {
            if (_appState.ActiveProfile is { } profile)
            {
                await SessionPersistence.TryLoadAsync(_sessionState, profile, _external, cancellationToken, _appState.DeviceHub);
            }
        }

        public AppSignalHandler(
            IServiceProvider sp,
            GuiAppState appState,
            PlannerState plannerState,
            SessionTabState sessionState,
            EquipmentTabState eqState,
            LiveSessionState liveSessionState,
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
            _liveSessionState = liveSessionState;
            _skyMapState = skyMapState;
            _tracker = tracker;
            _cts = cts;
            _external = external;

            // Single site-timezone source: planner + live-session read through to
            // GuiAppState.SiteTimeZone so every time display shares one value and the
            // two states cannot drift apart (see PlannerState.AttachAppState).
            plannerState.AttachAppState(appState);
            liveSessionState.AttachAppState(appState);

            _logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AppSignalHandler));
            _timeProvider = sp.GetRequiredService<ITimeProvider>();

            // Subscription groups live in the by-area partials (AppSignalHandler.Planner.cs /
            // .SkyMap.cs / .Equipment.cs / .LiveSession.cs / .Polar.cs / .Flats.cs). Order is
            // load-bearing: SignalBus invokes subscribers in registration order.
            SubscribePlannerSearch(bus);
            SubscribeSkyMap(bus);
            SubscribeEquipmentTextInputs(bus);
            SubscribeEquipmentActions(bus);
            SubscribeScheduleBuilding(bus);
            SubscribeLiveSession(bus);
            SubscribePolarAlignment(bus);
            SubscribeFlats(bus);
            SubscribePreview(bus, shutdownToken);

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
        private void Notify(NotificationSeverity severity, string message)
        {
            _appState.AppendNotification(_timeProvider.GetUtcNow(), severity, message);
            _appState.NeedsRedraw = true;
        }

        /// <summary>
        /// Guards an action that must not run during a session. Returns true (proceed) when idle;
        /// when a session is running it notifies <paramref name="message"/> (Warning) and returns
        /// false. Call as <c>if (!EnsureSessionIdle("...")) return;</c>. The message is bespoke per
        /// action ("Cannot slew manually ...", "Session already running", ...), so it is a required
        /// argument rather than a templated default.
        /// </summary>
        private bool EnsureSessionIdle(string message)
        {
            if (!_liveSessionState.IsRunning)
            {
                return true;
            }
            Notify(NotificationSeverity.Warning, message);
            return false;
        }

        /// <summary>
        /// Resolves a connected driver of type <typeparamref name="T"/> for <paramref name="uri"/>.
        /// Returns true with a non-null <paramref name="driver"/> when connected; otherwise notifies
        /// (Warning) "<paramref name="label"/> not connected" (or <paramref name="message"/> when the
        /// wording is bespoke) and returns false. Collapses the
        /// <c>TryGetConnectedDriver + "|| x is null" + Notify + return</c> guard to one line; the
        /// <c>|| x is null</c> was dead code (<see cref="IDeviceHub.TryGetConnectedDriver"/> is
        /// <c>[NotNullWhen(true)]</c>).
        /// </summary>
        private bool TryGetConnected<T>(IDeviceHub hub, Uri uri, string label,
            [NotNullWhen(true)] out T? driver, string? message = null)
            where T : class, IDeviceDriver
        {
            if (hub.TryGetConnectedDriver(uri, out driver))
            {
                return true;
            }
            Notify(NotificationSeverity.Warning, message ?? $"{label} not connected");
            return false;
        }

        /// <summary>
        /// Silent Live-Session prologue shared by the focuser jog/goto handlers: requires no running
        /// session, a valid OTA index in the active profile, a device hub, and a connected focuser
        /// assigned to that OTA. Returns false (deliberately without a notification: these are
        /// click-driven and self-explanatory) if any link is missing.
        /// </summary>
        private bool TryResolveIdleOtaFocuser(int otaIndex, [NotNullWhen(true)] out IFocuserDriver? focuser)
        {
            focuser = null;
            if (_liveSessionState.IsRunning) return false;
            if (_appState.ActiveProfile?.Data is not { OTAs: var otas } || otaIndex >= otas.Length) return false;
            if (_appState.DeviceHub is not { } hub) return false;
            if (otas[otaIndex].Focuser is not { } focUri) return false;
            return hub.TryGetConnectedDriver(focUri, out focuser);
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
