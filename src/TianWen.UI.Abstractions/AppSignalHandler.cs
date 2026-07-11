using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    /// </summary>
    public class AppSignalHandler
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
        /// Runs a device connect/disconnect on a thread-pool thread so its <b>synchronous prefix
        /// never executes on the render thread</b>. <see cref="SignalBus.ProcessPending"/> invokes
        /// async signal handlers inline on the render thread, running each handler up to its first
        /// yielding <c>await</c> before the returned task is handed to the tracker. Several drivers
        /// block synchronously <i>before</i> that first await — most notably ASCOM COM drivers whose
        /// <c>Connected = true/false</c> setter busy-spins <c>Application.DoEvents()</c> for ~1&#160;s
        /// (Gemini FlatPanel Lite, iOptron, …). Left inline that freezes the GUI (Not Responding), and
        /// on a host with no message pump it can crash the process. Offloading the call moves that
        /// blocking prefix off the render loop. The fake devices connect instantly precisely because
        /// they have no such blocking prefix.
        /// </summary>
        private static Task RunDeviceOpOffRenderThreadAsync(Func<Task> deviceOp, CancellationToken ct)
            => Task.Run(deviceOp, ct);

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
                        _appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Warning, "Set site coordinates in Equipment tab");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Recompute failed");
                    _appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Error, $"Recompute failed: {ex.InnerException?.Message ?? ex.Message}");
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

        /// <summary>
        /// The UTC instant the sky map is currently displaying: the base planning date (or the
        /// live clock when no date is pinned) PLUS the sky-map time-scrub offset. EVERY sky-map
        /// handler that computes alt/az or hit-tests an ephemeris position must resolve the
        /// viewing time through here so the info panel / selection matches what the renderer
        /// actually drew -- e.g. a scrubbed Zenith reticle must still report Alt 90 deg, not the
        /// un-scrubbed altitude. The fixed-point / mount info handlers used to omit TimeOffset and
        /// drifted from the click-select path once the map was scrubbed.
        /// </summary>
        private DateTimeOffset SkyMapViewingUtc()
            => (_plannerState.PlanningDate?.ToUniversalTime() ?? _timeProvider.GetUtcNow()) + _skyMapState.TimeOffset;

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

            var logger = _logger;

            // ---------------------------------------------------------------
            // Wire planner search input callbacks
            // ---------------------------------------------------------------
            string[]? autoCompleteCache = null;

            plannerState.SearchInput.OnCommit = text =>
            {
                plannerState.Suggestions.Clear();
                plannerState.SuggestionIndex = -1;
                plannerState.LastSuggestionQuery = "";

                if (appState.ActiveProfile is not null && text.Length > 0)
                {
                    var transform = TransformFactory.FromProfile(appState.ActiveProfile, _timeProvider, out _);
                    if (transform is not null)
                    {
                        var db = sp.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>();
                        var resultIdx = PlannerActions.SearchTargets(plannerState, db, transform, text, plannerState.Comets);
                        if (resultIdx >= 0)
                        {
                            plannerState.SelectedTargetIndex = resultIdx;
                            OnPlannerEnsureVisible?.Invoke(resultIdx);
                        }
                    }
                }
                return Task.CompletedTask;
            };

            plannerState.SearchInput.OnCancel = () =>
            {
                plannerState.SearchInput.Clear();
                plannerState.SearchResults = [];
                plannerState.Suggestions.Clear();
                plannerState.SuggestionIndex = -1;
                plannerState.LastSuggestionQuery = "";
                bus.Post(new DeactivateTextInputSignal());
                plannerState.NeedsRedraw = true;
            };

            plannerState.SearchInput.OnTextChanged = text =>
            {
                if (autoCompleteCache is not null)
                {
                    PlannerActions.UpdateSuggestions(plannerState, autoCompleteCache, text);
                }
            };

            // Autocomplete navigation: Up/Down/Return/Escape when suggestions are visible
            plannerState.SearchInput.OnKeyOverride = key =>
            {
                if (plannerState.Suggestions.Count == 0)
                {
                    return false;
                }

                switch (key)
                {
                    case TextInputKey.Backspace or TextInputKey.Delete:
                        return false; // Let the text input handle it, OnTextChanged will update suggestions

                    case TextInputKey.Enter when plannerState.SuggestionIndex >= 0:
                        CommitSuggestion(plannerState.Suggestions[plannerState.SuggestionIndex]);
                        return true;

                    case TextInputKey.Escape:
                        plannerState.Suggestions.Clear();
                        plannerState.SuggestionIndex = -1;
                        plannerState.LastSuggestionQuery = "";
                        appState.NeedsRedraw = true;
                        return true;

                    default:
                        return false;
                }
            };

            // ---------------------------------------------------------------
            // Wire sky-map F3 search
            // ---------------------------------------------------------------
            var skySearch = skyMapState.Search;

            skySearch.SearchInput.OnTextChanged = _ =>
            {
                var db = sp.GetRequiredService<ICelestialObjectDB>();
                SkyMapSearchActions.FilterResults(skySearch, db);
                skyMapState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            };

            skySearch.SearchInput.OnCommit = _ =>
            {
                bus.Post(new SkyMapSearchCommitSignal());
                return Task.CompletedTask;
            };

            skySearch.SearchInput.OnCancel = () => bus.Post(new CloseSkyMapSearchSignal());

            bus.Subscribe<OpenSkyMapSearchSignal>(_ =>
            {
                var db = sp.GetRequiredService<ICelestialObjectDB>();
                SkyMapSearchActions.OpenSearch(skySearch, db, plannerState.Comets);
                bus.Post(new ActivateTextInputSignal(skySearch.SearchInput));
                skyMapState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<CloseSkyMapSearchSignal>(_ =>
            {
                SkyMapSearchActions.CloseSearch(skySearch);
                bus.Post(new DeactivateTextInputSignal());
                skyMapState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<SkyMapSearchCommitSignal>(_ =>
            {
                var db = sp.GetRequiredService<ICelestialObjectDB>();
                // Include the sky-map scrub offset so a planet commit resolves the SAME live position
                // the map is showing (and reads the render's planet cache without thrashing it).
                var viewingUtc = SkyMapViewingUtc();
                var site = SiteContext.Create(plannerState.SiteLatitude, plannerState.SiteLongitude, viewingUtc);
                SkyMapSearchActions.CommitResult(
                    skySearch, skyMapState, db,
                    plannerState.SiteLatitude, plannerState.SiteLongitude,
                    viewingUtc, site, plannerState.Comets);
                bus.Post(new DeactivateTextInputSignal());
                skyMapState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<ViewInPlannerSignal>(sig =>
            {
                // Ensure the target is scored + profiled so the planner list has
                // something to select. Reuses the same CommitSuggestion path the
                // planner search uses, so alias population and altitude profile
                // are consistent across the two entry points.
                var target = new Target(sig.RA, sig.Dec, sig.Name, sig.Index);
                if (appState.ActiveProfile is { } prof)
                {
                    var transform = TransformFactory.FromProfile(prof, _timeProvider, out _);
                    if (transform is not null && !plannerState.ScoredTargets.ContainsKey(target))
                    {
                        var db = sp.GetRequiredService<ICelestialObjectDB>();
                        PlannerActions.CommitSuggestion(plannerState, db, transform, sig.Name, plannerState.Comets);
                    }
                }

                // Find the target in the filtered list and scroll it into view.
                var filtered = PlannerActions.GetFilteredTargets(plannerState);
                for (var i = 0; i < filtered.Count; i++)
                {
                    if (filtered[i].Target == target)
                    {
                        plannerState.SelectedTargetIndex = i;
                        OnPlannerEnsureVisible?.Invoke(i);
                        break;
                    }
                }

                appState.ActiveTab = GuiTab.Planner;
                appState.NeedsRedraw = true;
                plannerState.NeedsRedraw = true;
            });

            bus.Subscribe<SkyMapPinObjectSignal>(sig =>
            {
                var target = new Target(sig.RA, sig.Dec, sig.Name, sig.Index);
                var transform = appState.ActiveProfile is { } prof
                    ? TransformFactory.FromProfile(prof, _timeProvider, out _)
                    : null;
                var db = sp.GetRequiredService<ICelestialObjectDB>();
                PlannerActions.TogglePinFromExternal(
                    plannerState, db, transform, target, sig.ObjectType);
                bus.Post(new SavePlannerSessionSignal());
                skyMapState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<SkyMapSlewToObjectSignal>(sig =>
            {
                if (liveSessionState.IsRunning)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Cannot slew manually while a session is running");
                    appState.NeedsRedraw = true;
                    return;
                }
                if (appState.ActiveProfile is not { Data: { } pdata } profile
                    || pdata.Mount is not { Scheme: not "none" } mountUri)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "No mount configured in the active profile");
                    appState.NeedsRedraw = true;
                    return;
                }
                if (appState.DeviceHub is not { } hub
                    || !hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var mount)
                    || mount is null)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Mount is not connected \u2014 connect it from the Equipment tab first");
                    appState.NeedsRedraw = true;
                    return;
                }

                // Two-click confirmation for Sun slew. First click arms, second click
                // within the window proceeds. The arm/confirm state machine lives on
                // GuiAppState; this routes.
                if (sig.Index == CatalogIndex.Sol
                    && appState.GateSunSlew(CatalogIndex.Sol, _timeProvider.GetUtcNow(), TimeSpan.FromSeconds(5))
                        == GuiAppState.SunSlewGate.Armed)
                {
                    appState.StatusMessage =
                        "\u26A0 SUN \u2014 click Goto again within 5s to confirm. Verify a solar filter is installed.";
                    skyMapState.NeedsRedraw = true;
                    appState.NeedsRedraw = true;
                    return;
                }

                var minAlt = System.Math.Max((int)plannerState.MinHeightAboveHorizon, 1);
                var capturedMount = mount;
                var capturedSig = sig;
                var capturedProfile = profile;
                tracker.Run(async () =>
                {
                    try
                    {
                        var (post, msg) = await MountActions.SlewToJ2000Async(
                            capturedMount, capturedSig.Name, capturedSig.RA, capturedSig.Dec, capturedSig.Index,
                            profile: capturedProfile, timeProvider: _timeProvider,
                            minAboveHorizonDegrees: minAlt, logger: logger,
                            cancellationToken: cts.Token);
                        var severity = post == SlewPostCondition.Slewing
                            ? NotificationSeverity.Info
                            : NotificationSeverity.Warning;
                        appState.AppendNotification(_timeProvider.GetUtcNow(), severity, msg);
                        skyMapState.NeedsRedraw = true;
                        appState.NeedsRedraw = true;

                        // Follow up with a "Reached <name>" / "Slew timed out" notification when
                        // the mount actually stops slewing, so the status bar isn't permanently
                        // stuck on the kick-off "Slewing to ..." message.
                        if (post == SlewPostCondition.Slewing)
                        {
                            // Surface the slew destination on the sky map (marker + ETA, the
                            // latter estimated in the render path from the polled reticle).
                            // The signal carries J2000 catalog coords, matching the overlay frame.
                            skyMapState.ActiveSlewTarget = new SlewTargetInfo(
                                capturedSig.Name, capturedSig.RA, capturedSig.Dec);
                            skyMapState.SlewEtaSeconds = double.NaN;
                            // Kick the mount poll so the reticle picks up IsSlewing (and the
                            // fast slew cadence) this frame instead of up to a steady interval later.
                            RequestPreviewMountRefresh();
                            var (completion, completionMsg) = await MountActions.AwaitSlewCompletionAsync(
                                capturedMount, capturedSig.Name, _timeProvider,
                                logger: logger, cancellationToken: cts.Token);
                            var completionSeverity = completion == MountActions.SlewCompletion.Reached
                                ? NotificationSeverity.Info
                                : NotificationSeverity.Warning;
                            appState.AppendNotification(_timeProvider.GetUtcNow(), completionSeverity, completionMsg);
                        }
                    }
                    catch (OperationCanceledException oce)
                    {
                        // Shutdown / explicit cancel — no notification needed, but log so a
                        // mid-slew abort is still traceable in the file logger.
                        logger.LogDebug(oce, "Slew to {Name} cancelled", capturedSig.Name);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Slew to {Name} failed", capturedSig.Name);
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Error, $"Slew failed: {ex.Message}");
                    }
                    finally
                    {
                        // Slew finished (reached / timed out / cancelled / failed): drop the
                        // destination marker so it doesn't linger after the mount settles.
                        skyMapState.ActiveSlewTarget = null;
                        skyMapState.SlewEtaSeconds = double.NaN;
                        skyMapState.NeedsRedraw = true;
                        appState.NeedsRedraw = true;
                    }
                }, $"Goto {sig.Name}");
            });

            bus.Subscribe<SkyMapClickSelectSignal>(sig =>
            {
                var rect = skyMapState.LastContentRect;
                if (rect.Width <= 0 || rect.Height <= 0) return;

                var db = sp.GetRequiredService<ICelestialObjectDB>();
                var ppr = SkyMapProjection.PixelsPerRadian(rect.Height, skyMapState.FieldOfViewDeg);
                var cx = rect.X + rect.Width * 0.5f;
                var cy = rect.Y + rect.Height * 0.5f;
                // Match the render's viewing instant: base date/now PLUS the sky-map scrub offset
                // (State.TimeOffset), via SkyMapViewingUtc. Planet positions are ephemeris-computed
                // and move with time, so hit-testing them (and the panel's alt/az) must use the same
                // instant the renderer drew -- otherwise a scrubbed planet dot is unclickable.
                var viewingUtc = SkyMapViewingUtc();
                var site = SiteContext.Create(plannerState.SiteLatitude, plannerState.SiteLongitude, viewingUtc);

                SkyMapSearchActions.SelectObjectByClick(
                    skySearch, skyMapState, db,
                    plannerState.SiteLatitude, plannerState.SiteLongitude,
                    viewingUtc, site,
                    sig.ScreenX, sig.ScreenY,
                    skyMapState.CurrentViewMatrix, ppr, cx, cy,
                    preferPointSource: (sig.Modifiers & InputModifier.Ctrl) != 0,
                    pinnedCatalogIndices: PlannerActions.GetPinnedCatalogIndices(plannerState.Proposals),
                    comets: plannerState.Comets);

                skyMapState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<SkyMapSetViewSignal>(sig =>
            {
                // Route-only: the actions helper owns the centre + FOV-clamp + toggle logic.
                if (SkyMapViewActions.SetView(skyMapState,
                        sig.CenterRaHours, sig.CenterDecDeg, sig.FieldOfViewDeg,
                        sig.ShowObjectOverlay, sig.ShowDarkNebulae))
                {
                    appState.NeedsRedraw = true;
                }
            });

            bus.Subscribe<SkyMapShowFixedPointInfoSignal>(sig =>
            {
                // Must include the sky-map scrub offset (via SkyMapViewingUtc): the Zenith/NCP/SCP
                // reticles are placed by the renderer at the scrubbed LST, so the panel's alt/az
                // has to be computed at the SAME instant or a scrubbed Zenith reads e.g. +9.9 deg
                // instead of +90 deg.
                var viewingUtc = SkyMapViewingUtc();
                var site = SiteContext.Create(plannerState.SiteLatitude, plannerState.SiteLongitude, viewingUtc);
                skySearch.InfoPanel = SkyMapInfoPanelData.FromPosition(
                    sig.Name, sig.RaHours, sig.DecDeg,
                    plannerState.SiteLatitude, plannerState.SiteLongitude,
                    viewingUtc, site) with { FixedPoint = sig.FixedPoint };
                skyMapState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<SkyMapShowMountInfoSignal>(sig =>
            {
                // Same scrub-consistency requirement as the fixed-point handler: the mount reticle
                // is projected at the scrubbed LST, so its reported alt/az must be too.
                var viewingUtc = SkyMapViewingUtc();
                var site = SiteContext.Create(plannerState.SiteLatitude, plannerState.SiteLongitude, viewingUtc);
                skySearch.InfoPanel = SkyMapInfoPanelData.FromMount(
                    sig.Name, sig.RaHours, sig.DecDeg,
                    plannerState.SiteLatitude, plannerState.SiteLongitude,
                    viewingUtc, site);
                skyMapState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<SkyMapSolveSyncSignal>(sig =>
            {
                if (liveSessionState.IsRunning)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Cannot solve & sync while a session is running");
                    appState.NeedsRedraw = true;
                    return;
                }
                // Re-entrancy guard: ignore a second click while a solve is already in
                // flight (the button also shows "Solving ..." and drops its handler, but
                // a queued signal could still arrive). UI-thread-only read here.
                if (skyMapState.SolveSyncInProgress)
                {
                    return;
                }
                if (appState.ActiveProfile is not { Data: { } pdata } profile
                    || pdata.Mount is not { Scheme: not "none" } mountUri)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "No mount configured in the active profile");
                    appState.NeedsRedraw = true;
                    return;
                }
                if (appState.DeviceHub is not { } hub
                    || !hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var mount)
                    || mount is null)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Mount is not connected \u2014 connect it from the Equipment tab first");
                    appState.NeedsRedraw = true;
                    return;
                }
                if (pdata.OTAs is not { Length: > 0 } otas || sig.OtaIndex >= otas.Length)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "No OTA configured in the active profile");
                    appState.NeedsRedraw = true;
                    return;
                }
                var ota = otas[sig.OtaIndex];
                if (!hub.TryGetConnectedDriver<ICameraDriver>(ota.Camera, out var camera) || camera is null)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Camera not connected");
                    appState.NeedsRedraw = true;
                    return;
                }

                // Optional per-OTA devices for FITS denorm stamping (same as TakePreview);
                // the mount is resolved separately above, so discard it here.
                var (ssFocuser, ssFilterWheel, _) = EquipmentActions.ResolveOtaCaptureDevices(hub, pdata, sig.OtaIndex);

                // Mirror the preview-capture progress UI while the solve frame exposes.
                // ExposureSeconds is now trustworthy even for the button's parameterless
                // `new SkyMapSolveSyncSignal()` thanks to its explicit parameterless ctor.
                if (sig.OtaIndex < liveSessionState.PreviewCapturing.Length)
                {
                    liveSessionState.PreviewCapturing[sig.OtaIndex] = true;
                    liveSessionState.PreviewCaptureStart[sig.OtaIndex] = _timeProvider.GetUtcNow();
                    liveSessionState.PreviewExposureDuration[sig.OtaIndex] = TimeSpan.FromSeconds(sig.ExposureSeconds);
                }
                appState.StatusMessage = "Solve & sync\u2026";
                skyMapState.SolveSyncInProgress = true; // drives the "Solving ..." button label
                appState.NeedsRedraw = true;

                var capturedSig = sig;
                var capturedMount = mount;
                var capturedCamera = camera;
                var capturedProfile = profile;
                var capturedOta = ota;
                tracker.Run(async () =>
                {
                    try
                    {
                        var outcome = await MountActions.SolveAndSyncAsync(
                            capturedMount, capturedCamera,
                            capturedOta.Name, capturedOta.FocalLength, capturedOta.Aperture,
                            ssFocuser, ssFilterWheel,
                            sp.GetRequiredService<ICelestialObjectDB>(),
                            sp.GetRequiredService<IPlateSolverFactory>(),
                            capturedProfile, _timeProvider,
                            TimeSpan.FromSeconds(capturedSig.ExposureSeconds),
                            capturedSig.Gain is { } g ? (short)g : null,
                            capturedSig.Binning,
                            logger, cts.Token);

                        // Stash the solve frame into the preview slot (ownership transfer from
                        // the outcome; release the previous occupant so its ChannelBuffer drops).
                        if (outcome.CapturedImage is { } image
                            && capturedSig.OtaIndex < liveSessionState.LastCapturedImages.Length)
                        {
                            liveSessionState.LastCapturedImages[capturedSig.OtaIndex]?.Release();
                            liveSessionState.LastCapturedImages[capturedSig.OtaIndex] = image;
                        }
                        else
                        {
                            outcome.CapturedImage?.Release();
                        }
                        if (outcome.SolveResult is { } solveResult)
                        {
                            liveSessionState.PreviewPlateSolveResult = solveResult;
                        }

                        var severity = outcome.Result == MountActions.SolveSyncResult.Synced
                            ? NotificationSeverity.Info
                            : NotificationSeverity.Warning;
                        appState.AppendNotification(_timeProvider.GetUtcNow(), severity, outcome.StatusMessage);
                    }
                    catch (OperationCanceledException oce)
                    {
                        // Shutdown / explicit cancel - log so a mid-solve abort stays traceable.
                        logger.LogDebug(oce, "Solve & sync cancelled");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Solve & sync failed");
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Error, $"Solve & sync failed: {ex.Message}");
                    }
                    finally
                    {
                        if (capturedSig.OtaIndex < liveSessionState.PreviewCapturing.Length)
                        {
                            liveSessionState.PreviewCapturing[capturedSig.OtaIndex] = false;
                        }
                        skyMapState.SolveSyncInProgress = false; // re-enable the Solve & Sync button
                        // A sync doesn't change slew/track state, so the cadence ramp won't
                        // notice it - force a poll so the reticle jumps to the synced pointing.
                        RequestPreviewMountRefresh();
                        skyMapState.NeedsRedraw = true;
                        liveSessionState.NeedsRedraw = true;
                        appState.NeedsRedraw = true;
                    }
                }, "SolveAndSync");
            });

            // ---------------------------------------------------------------
            // Wire equipment text input callbacks
            // ---------------------------------------------------------------

            eqState.ProfileNameInput.OnCommit = async text =>
            {
                if (text.Length > 0)
                {
                    var profile = await EquipmentActions.CreateProfileAsync(text, external, cts.Token);
                    appState.ActiveProfile = profile;
                    eqState.IsCreatingProfile = false;
                    eqState.ProfileNameInput.Clear();
                    bus.Post(new DeactivateTextInputSignal());
                    plannerState.NeedsRecompute = true;
                    appState.NeedsRedraw = true;
                }
            };

            eqState.ProfileNameInput.OnCancel = () =>
            {
                eqState.IsCreatingProfile = false;
                eqState.ProfileNameInput.Clear();
            };

            // Site inputs share a commit: save site on Enter from any of the three fields.
            // Parse/validate + the mount push live in EquipmentActions; this routes.
            Func<Task> saveSite = async () =>
            {
                if (appState.ActiveProfile is not { } siteProfile)
                {
                    return;
                }

                if (!EquipmentActions.TryParseSite(
                        eqState.LatitudeInput.Text, eqState.LongitudeInput.Text, eqState.ElevationInput.Text,
                        out var sLat, out var sLon, out var sElev))
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Invalid coordinates (lat: -90..90, lon: -180..180)");
                    return;
                }

                var sData = siteProfile.Data ?? ProfileData.Empty;
                var newSiteData = EquipmentActions.SetSite(sData, sLat, sLon, sElev);
                var updatedSite = siteProfile.WithData(newSiteData);
                // Update UI immediately, save in background
                appState.ActiveProfile = updatedSite;
                eqState.IsEditingSite = false;
                bus.Post(new DeactivateTextInputSignal());
                plannerState.SiteLatitude = sLat;
                plannerState.SiteLongitude = sLon;
                plannerState.NeedsRecompute = true;
                appState.NeedsRedraw = true;

                // If the catalog was blocked on a missing site, load it now.
                if (plannerState.ObjectDb is null
                    && TransformFactory.FromProfile(updatedSite, _timeProvider, out _) is { } siteTransform)
                {
                    _tracker.Run(() => InitializePlannerAsync(siteTransform, cts.Token),
                        "Load catalog after site edit");
                }

                await EquipmentActions.PushSiteToMountIfProfileWinsAsync(
                    appState.DeviceHub, newSiteData, sLat, sLon, sElev, logger, cts.Token);
                await updatedSite.SaveAsync(external, cts.Token);
            };

            Action cancelSite = () =>
            {
                eqState.IsEditingSite = false;
                eqState.LatitudeInput.Deactivate();
                eqState.LongitudeInput.Deactivate();
                eqState.ElevationInput.Deactivate();
                appState.ActiveTextInput = null;
            };

            eqState.LatitudeInput.OnCommit = _ => saveSite();
            eqState.LongitudeInput.OnCommit = _ => saveSite();
            eqState.ElevationInput.OnCommit = _ => saveSite();
            eqState.LatitudeInput.OnCancel = cancelSite;
            eqState.LongitudeInput.OnCancel = cancelSite;
            eqState.ElevationInput.OnCancel = cancelSite;

            // Guide scope focal length — commit on Enter
            eqState.GuiderFocalLengthInput.OnCommit = async text =>
            {
                if (appState.ActiveProfile is { } profile && profile.Data is { } pd)
                {
                    int? guiderFl = int.TryParse(text, out var fl) && fl > 0 ? fl : null;
                    var updated = profile.WithData(pd with { GuiderFocalLength = guiderFl });
                    appState.ActiveProfile = updated;
                    appState.NeedsRedraw = true;
                    await updated.SaveAsync(external, cts.Token);
                }
            };

            // OTA name / focal length / aperture — commit on Enter saves the OTA edit
            Task saveOta(string _)
            {
                if (appState.ActiveProfile is { Data: { } editData } && eqState.EditingOtaIndex >= 0)
                {
                    var otaIdx = eqState.EditingOtaIndex;
                    var newName = eqState.OtaNameInput.Text is { Length: > 0 } n ? n : null;
                    int? newFl = int.TryParse(eqState.FocalLengthInput.Text, out var fl) && fl > 0 ? fl : null;
                    int? newAp = int.TryParse(eqState.ApertureInput.Text, out var ap) ? ap : null;
                    var newData = EquipmentActions.UpdateOTA(editData, otaIdx, name: newName, focalLength: newFl, aperture: newAp);
                    bus.Post(new UpdateProfileSignal(newData));
                    eqState.StopEditingOta();
                }
                return Task.CompletedTask;
            }
            eqState.OtaNameInput.OnCommit = saveOta;
            eqState.FocalLengthInput.OnCommit = saveOta;
            eqState.ApertureInput.OnCommit = saveOta;

            // Device string settings (API keys, ports, etc.) — commit on Enter saves the setting
            eqState.StringSettingInput.OnCommit = async _ =>
            {
                if (eqState.EditingStringSettingKey is not { } key || eqState.EditingDeviceUri is not { } editUri)
                {
                    return;
                }

                var value = eqState.StringSettingInput.Text;
                eqState.EditingStringSettingKey = null;

                // The masked-secret-vs-URI-param decision (credential-store write for secrets,
                // query-param URI for the rest) lives in EquipmentActions; this routes.
                var commit = EquipmentActions.CommitDeviceSetting(
                    editUri, key, value, sp.GetRequiredService<ICredentialStore>());
                if (commit.Kind == EquipmentActions.DeviceSettingCommitKind.StoredSecret)
                {
                    // The URI/profile is unchanged, so the refetch-on-weather-URI-change path won't
                    // fire; re-fetch here now that the key may have become available.
                    if (commit.IsWeatherSecret)
                    {
                        await FetchWeatherForecastAsync(cts.Token);
                    }
                    appState.NeedsRedraw = true;
                    return;
                }

                // Non-secret: keep as a query param on the device URI (existing behaviour).
                eqState.EditingDeviceUri = commit.NewUri;
                if (appState.ActiveProfile is { Data: { } data } && commit.NewUri is { } newUri
                    && eqState.SavedDeviceSettingsUri is { } savedUri)
                {
                    var newData = EquipmentActions.UpdateDeviceUri(data, savedUri, newUri);
                    bus.Post(new UpdateProfileSignal(newData));
                    eqState.BeginEditingDeviceSettings(newUri);
                }
            };

            // ---------------------------------------------------------------
            // Equipment action signal subscriptions (DI-dependent handlers)
            // ---------------------------------------------------------------

            bus.Subscribe<DiscoverDevicesSignal>(async sig =>
            {
                if (eqState.IsDiscovering) return;

                eqState.IsDiscovering = true;
                appState.StatusMessage = sig.IncludeFake ? "Discovering devices (+ fake)..." : "Discovering devices...";
                appState.NeedsRedraw = true;
                try
                {
                    var dm = sp.GetRequiredService<IDeviceDiscovery>();
                    await dm.CheckSupportAsync(cts.Token);
                    await dm.DiscoverAsync(cts.Token);
                    eqState.DiscoveredDevices = [.. dm.RegisteredDeviceTypes
                        .Where(t => t is not DeviceType.Profile and not DeviceType.None)
                        .SelectMany(dm.RegisteredDevices)
                        .Where(d => sig.IncludeFake || d is not TianWen.Lib.Devices.Fake.FakeDevice)
                        .OrderBy(d => d.DeviceType).ThenBy(d => d.DisplayName)];

                    // Route post-discovery profile reconciliation to EquipmentActions,
                    // then reflect any active-profile update back into UI state.
                    var reconciledProfiles = await EquipmentActions.ReconcileAllProfilesAsync(dm, external, cts.Token);
                    foreach (var (original, updated) in reconciledProfiles)
                    {
                        if (appState.ActiveProfile?.ProfileId == original.ProfileId)
                        {
                            appState.ActiveProfile = updated;
                        }

                        // Log exactly what URI moved so site / gain / filter clobbers are
                        // visible in the log instead of silently drifting.
                        var diffs = EquipmentActions.DiffProfileData(original.Data!.Value, updated.Data!.Value);
                        foreach (var (field, before, after) in diffs)
                        {
                            logger.LogInformation(
                                "Reconcile {Profile} {Field}: {Before} -> {After}",
                                original.DisplayName, field, before, after);
                        }
                    }
                    if (reconciledProfiles.Count > 0)
                    {
                        logger.LogInformation("Post-discovery reconcile: updated {Count} profile(s)", reconciledProfiles.Count);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Device discovery failed");
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Error, "Discovery failed");
                }
                finally
                {
                    eqState.IsDiscovering = false;
                    if (eqState.DiscoveredDevices.Count > 0)
                    {
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Info, $"Found {eqState.DiscoveredDevices.Count} devices");
                    }
                    appState.NeedsRedraw = true;
                }
            });

            bus.Subscribe<AddOtaSignal>(async _ =>
            {
                if (appState.ActiveProfile is not { } p) return;

                var data = p.Data ?? ProfileData.Empty;
                var newOta = new OTAData(
                    Name: $"Telescope #{data.OTAs.Length}",
                    FocalLength: 1000,
                    Camera: NoneDevice.Instance.DeviceUri,
                    Cover: null, Focuser: null, FilterWheel: null,
                    PreferOutwardFocus: null, OutwardIsPositive: null,
                    Aperture: null, OpticalDesign: OpticalDesign.Unknown);
                var updated = p.WithData(EquipmentActions.AddOTA(data, newOta));
                appState.ActiveProfile = updated;
                appState.NeedsRedraw = true;
                await updated.SaveAsync(external, cts.Token);
            });

            bus.Subscribe<EditSiteSignal>(_ =>
            {
                eqState.IsEditingSite = true;
                if (appState.ActiveProfile?.Data is { } pd)
                {
                    var existingSite = EquipmentActions.GetSiteFromProfile(pd);
                    if (existingSite.HasValue)
                    {
                        eqState.LatitudeInput.Text = existingSite.Value.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        eqState.LatitudeInput.CursorPos = eqState.LatitudeInput.Text.Length;
                        eqState.LongitudeInput.Text = existingSite.Value.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        eqState.LongitudeInput.CursorPos = eqState.LongitudeInput.Text.Length;
                        eqState.ElevationInput.Text = existingSite.Value.Elev?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
                        eqState.ElevationInput.CursorPos = eqState.ElevationInput.Text.Length;
                    }
                }
                bus.Post(new ActivateTextInputSignal(eqState.LatitudeInput));
            });

            bus.Subscribe<CreateProfileSignal>(_ =>
            {
                if (!eqState.IsCreatingProfile)
                {
                    eqState.IsCreatingProfile = true;
                    bus.Post(new ActivateTextInputSignal(eqState.ProfileNameInput));
                }
            });

            bus.Subscribe<AssignDeviceSignal>(async sig =>
            {
                var deviceIndex = sig.DeviceIndex;
                if (deviceIndex < 0 || deviceIndex >= eqState.DiscoveredDevices.Count) return;

                if (eqState.ActiveAssignment is { } target && appState.ActiveProfile is { } profile)
                {
                    var device = eqState.DiscoveredDevices[deviceIndex];

                    if (device.DeviceType != target.ExpectedDeviceType)
                    {
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Warning, $"Expected {target.ExpectedDeviceType}, got {device.DeviceType}");
                        return;
                    }

                    var data = profile.Data ?? ProfileData.Empty;

                    // Capture the URI previously assigned to THIS slot. If still connected
                    // via the hub, we'll auto-disconnect it after assignment iff safe
                    // (cooler off, idle). Cool/busy orphans are left connected and the
                    // user is told to disconnect manually so warm-up runs.
                    var prevSlotUri = EquipmentActions.GetAssignedDevice(data, target);

                    data = EquipmentActions.UnassignDevice(data, device.DeviceUri);

                    var newData = EquipmentActions.ApplyAssignment(data, target, device.DeviceType, device.DeviceUri);

                    var updated = profile.WithData(newData);
                    appState.ActiveProfile = updated;
                    // Keep the slot active so the user can swap the assigned device by
                    // clicking another row immediately, without re-clicking the slot.
                    // Click the slot itself again to deactivate.
                    appState.NeedsRedraw = true;
                    await updated.SaveAsync(external, cts.Token);

                    // Fetch weather forecast immediately when a weather device is assigned
                    if (device.DeviceType is DeviceType.Weather)
                    {
                        await FetchWeatherForecastAsync(cts.Token);
                        appState.NeedsRedraw = true;
                    }

                    // Auto-disconnect the orphan if it's still connected and safe
                    // (EquipmentActions.AutoDisconnectOrphanAsync); this lambda only maps
                    // the outcome to notifications.
                    if (appState.DeviceHub is { } hub)
                    {
                        var (outcome, safety) = await EquipmentActions.AutoDisconnectOrphanAsync(
                            hub, prevSlotUri, device.DeviceUri, logger, cts.Token);
                        switch (outcome)
                        {
                            case EquipmentActions.OrphanDisconnectOutcome.Disconnected:
                                appState.AppendNotification(_timeProvider.GetUtcNow(),
                                    NotificationSeverity.Info, $"Previous {target.ExpectedDeviceType} disconnected");
                                break;
                            case EquipmentActions.OrphanDisconnectOutcome.LeftConnected:
                                appState.AppendNotification(_timeProvider.GetUtcNow(),
                                    NotificationSeverity.Warning, $"Previous {target.ExpectedDeviceType} left connected ({safety}). Click Off on its row to warm up.");
                                break;
                        }
                        if (outcome != EquipmentActions.OrphanDisconnectOutcome.NotApplicable)
                        {
                            appState.NeedsRedraw = true;
                        }
                    }
                }
            });

            bus.Subscribe<AssignManualCoverSignal>(async _ =>
            {
                // The manual light panel is not discoverable, so it never appears in the device list.
                // Assign its canonical URI straight to the active Cover slot (mirrors the URI-based tail
                // of AssignDeviceSignal). It then flows through the ordinary calibrator flat path.
                if (eqState.ActiveAssignment is not { } target || appState.ActiveProfile is not { } profile)
                {
                    return;
                }
                if (target.ExpectedDeviceType != DeviceType.CoverCalibrator)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Select an OTA's Cover slot first, then add the Manual Light Panel");
                    return;
                }

                var manual = new TianWen.Lib.Devices.ManualCoverDevice();
                var data = profile.Data ?? ProfileData.Empty;
                var newData = EquipmentActions.ApplyAssignment(data, target, DeviceType.CoverCalibrator, manual.DeviceUri);
                var updated = profile.WithData(newData);
                appState.ActiveProfile = updated;
                appState.NeedsRedraw = true;
                await updated.SaveAsync(external, cts.Token);
                appState.AppendNotification(_timeProvider.GetUtcNow(),
                    NotificationSeverity.Info, "Manual Light Panel assigned - switch it on before capturing flats");
            });

            bus.Subscribe<ConnectAllDevicesSignal>(_ =>
            {
                if (appState.ActiveProfile?.Data is not { } pdata) return;
                if (appState.DeviceHub is not { } hub) return;

                // Fan out to per-device ConnectDeviceSignal so each connect goes through
                // the same in-flight gate, notification, and safety paths as a manual
                // click. Skip URIs that the hub already considers connected — connecting
                // an already-connected URI just churns PendingTransitions without effect.
                foreach (var uri in pdata.AssignedDeviceUris)
                {
                    if (hub.IsConnected(uri)) continue;
                    bus.Post(new ConnectDeviceSignal(uri));
                }
            });

            bus.Subscribe<ConnectDeviceSignal>(async sig =>
            {
                if (appState.DeviceHub is not { } hub)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Device hub unavailable");
                    return;
                }

                if (!eqState.PendingTransitions.TryAdd(sig.DeviceUri, 0))
                {
                    return; // transition already in flight
                }
                appState.NeedsRedraw = true;

                try
                {
                    // Prefer the resolved device (carries query-param config); fall back
                    // to a freshly-discovered match by URI equality.
                    var device = EquipmentActions.ResolveDeviceForConnect(hub, eqState.DiscoveredDevices, sig.DeviceUri);

                    if (device is null)
                    {
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Warning, "Cannot resolve device URI for connect");
                        return;
                    }

                    await RunDeviceOpOffRenderThreadAsync(() => hub.ConnectAsync(device, cts.Token).AsTask(), cts.Token);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Info, $"Connected: {device.DisplayName}");

                    // Mount connect → reconcile site between mount hardware and profile,
                    // per SiteTieBreaker. Updates ProfileState + PlannerState; persists
                    // profile if adopted; pushes to mount if profile wins.
                    if (device.DeviceType == DeviceType.Mount
                        && appState.ActiveProfile is { } currentProfile
                        && currentProfile.Data is { } pdata
                        && DeviceBase.SameDevice(pdata.Mount, sig.DeviceUri)
                        && hub.TryGetConnectedDriver<IMountDriver>(sig.DeviceUri, out var mount)
                        && mount is not null)
                    {
                        var outcome = await EquipmentActions.ReconcileSiteOnMountConnectAsync(
                            pdata, mount, logger, cts.Token);
                        if (outcome.ProfileChanged)
                        {
                            var updated = currentProfile.WithData(outcome.Data);
                            await updated.SaveAsync(_external, cts.Token);
                            appState.ActiveProfile = updated;
                        }
                        if (outcome.Data.SiteLatitude is { } rlat && outcome.Data.SiteLongitude is { } rlon)
                        {
                            plannerState.SiteLatitude = rlat;
                            plannerState.SiteLongitude = rlon;
                            plannerState.NeedsRedraw = true;

                            // If the catalog hasn't loaded yet because we previously
                            // had no site, fire InitializePlannerAsync now.
                            if (plannerState.ObjectDb is null
                                && TransformFactory.FromProfile(appState.ActiveProfile!, _timeProvider, out _) is { } rTransform)
                            {
                                _tracker.Run(() => InitializePlannerAsync(rTransform, cts.Token),
                                    "Load catalog after site reconcile");
                            }
                        }
                    }

                    // Camera connect -> capture sensor geometry into the matching OTA so the planner
                    // can compute the sensor FOV (smart framing groups) offline afterwards. Idempotent:
                    // CaptureSensorSpecs returns null (no save) once the specs are stored and unchanged.
                    // ActiveProfile is re-read fresh here; a rare ConnectAll multi-camera race can drop
                    // one OTA's first capture, which self-heals on the next connect.
                    if (device.DeviceType == DeviceType.Camera
                        && appState.ActiveProfile is { } camProfile
                        && camProfile.Data is { } camData
                        && hub.TryGetConnectedDriver<ICameraDriver>(sig.DeviceUri, out var cam)
                        && cam is not null
                        && EquipmentActions.CaptureSensorSpecs(camData, sig.DeviceUri, cam) is { } capturedData)
                    {
                        var updated = camProfile.WithData(capturedData);
                        await updated.SaveAsync(_external, cts.Token);
                        appState.ActiveProfile = updated;
                        // The FOV is now known -> push it to the planner and (re)compute framing groups.
                        RefreshSensorFovAndFraming();
                        logger.LogInformation("Captured sensor geometry for {Uri} into profile", sig.DeviceUri);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Connect failed for {Uri}", sig.DeviceUri);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Error, $"Connect failed: {ex.Message}");
                }
                finally
                {
                    eqState.PendingTransitions.TryRemove(sig.DeviceUri, out _);
                    appState.NeedsRedraw = true;
                }
            });

            bus.Subscribe<DisconnectDeviceSignal>(async sig =>
            {
                if (appState.DeviceHub is not { } hub)
                {
                    return;
                }

                // Pre-flight safety check. If the device is a cooled/busy camera, don't
                // disconnect — set the per-row confirmation state so the UI shows the
                // [Warm & Off] [Force Off] [Cancel] strip instead of executing.
                var safety = await EquipmentActions.GetDisconnectSafetyAsync(hub, sig.DeviceUri, cts.Token);
                if (safety != EquipmentActions.DisconnectSafety.Safe)
                {
                    eqState.PendingDisconnectConfirm = sig.DeviceUri;
                    eqState.PendingDisconnectSafety = safety;
                    eqState.PendingForceConfirm = null;
                    appState.NeedsRedraw = true;
                    return;
                }

                if (!eqState.PendingTransitions.TryAdd(sig.DeviceUri, 0))
                {
                    return;
                }
                appState.NeedsRedraw = true;

                try
                {
                    await RunDeviceOpOffRenderThreadAsync(() => hub.DisconnectAsync(sig.DeviceUri, cts.Token).AsTask(), cts.Token);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Info, "Device disconnected");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Disconnect failed for {Uri}", sig.DeviceUri);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Error, $"Disconnect failed: {ex.Message}");
                }
                finally
                {
                    eqState.PendingTransitions.TryRemove(sig.DeviceUri, out _);
                    appState.NeedsRedraw = true;
                }
            });

            bus.Subscribe<ForceDisconnectDeviceSignal>(async sig =>
            {
                if (appState.DeviceHub is not { } hub)
                {
                    return;
                }

                // Bypass the safety pre-check. Caller already passed two-stage confirmation.
                eqState.PendingDisconnectConfirm = null;
                eqState.PendingForceConfirm = null;
                if (!eqState.PendingTransitions.TryAdd(sig.DeviceUri, 0))
                {
                    return;
                }
                appState.NeedsRedraw = true;

                try
                {
                    await RunDeviceOpOffRenderThreadAsync(() => hub.DisconnectAsync(sig.DeviceUri, cts.Token).AsTask(), cts.Token);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Info, "Device force-disconnected (no warm-up)");
                    logger.LogWarning("Force-disconnect of {Uri} (bypassed safety check)", sig.DeviceUri);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Force-disconnect failed for {Uri}", sig.DeviceUri);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Error, $"Force-disconnect failed: {ex.Message}");
                }
                finally
                {
                    eqState.PendingTransitions.TryRemove(sig.DeviceUri, out _);
                    appState.NeedsRedraw = true;
                }
            });

            bus.Subscribe<WarmAndDisconnectDeviceSignal>(async sig =>
            {
                if (appState.DeviceHub is not { } hub)
                {
                    return;
                }

                eqState.PendingDisconnectConfirm = null;
                eqState.PendingForceConfirm = null;
                if (!eqState.PendingTransitions.TryAdd(sig.DeviceUri, 0))
                {
                    return;
                }
                appState.NeedsRedraw = true;

                try
                {
                    await RunDeviceOpOffRenderThreadAsync(() => EquipmentActions.WarmAndDisconnectAsync(hub, sig.DeviceUri, _logger, cts.Token).AsTask(), cts.Token);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Info, "Camera warmed and disconnected");
                }
                catch (OperationCanceledException)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Warm-up cancelled");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Warm-and-disconnect failed for {Uri}", sig.DeviceUri);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Error, $"Warm-up failed: {ex.Message}");
                }
                finally
                {
                    eqState.PendingTransitions.TryRemove(sig.DeviceUri, out _);
                    appState.NeedsRedraw = true;
                }
            });

            bus.Subscribe<SetCoolerSetpointSignal>(async sig =>
            {
                if (appState.DeviceHub is not { } hub) return;
                if (!hub.TryGetConnectedDriver<TianWen.Lib.Devices.ICameraDriver>(sig.DeviceUri, out var camera))
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Camera not connected");
                    return;
                }
                try
                {
                    await EquipmentActions.SetCoolerSetpointAsync(camera, sig.SetpointC, cts.Token);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Info, $"Cooling to {sig.SetpointC:F1}\u00b0C");
                    appState.NeedsRedraw = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SetCoolerSetpoint failed for {Uri}", sig.DeviceUri);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Error, $"Cooler setpoint failed: {ex.Message}");
                }
            });

            bus.Subscribe<WarmAndCoolerOffSignal>(async sig =>
            {
                if (appState.DeviceHub is not { } hub) return;
                eqState.PendingCoolerOffConfirm = null;
                eqState.PendingCoolerOffForceConfirm = null;
                appState.NeedsRedraw = true;

                try
                {
                    await EquipmentActions.WarmAndCoolerOffAsync(hub, sig.DeviceUri, _logger, cts.Token);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Info, "Camera warmed; cooler off");
                }
                catch (OperationCanceledException)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Warm-up cancelled");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Warm-and-cooler-off failed for {Uri}", sig.DeviceUri);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Error, $"Warm-up failed: {ex.Message}");
                }
                finally { appState.NeedsRedraw = true; }
            });

            bus.Subscribe<SetCoolerOffSignal>(async sig =>
            {
                if (appState.DeviceHub is not { } hub) return;
                if (!hub.TryGetConnectedDriver<TianWen.Lib.Devices.ICameraDriver>(sig.DeviceUri, out var camera))
                {
                    return;
                }
                try
                {
                    await EquipmentActions.SetCoolerOffAsync(camera, cts.Token);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Info, "Cooler off");
                    appState.NeedsRedraw = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SetCoolerOff failed for {Uri}", sig.DeviceUri);
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Error, $"Cooler off failed: {ex.Message}");
                }
            });

            bus.Subscribe<UpdateProfileSignal>(async sig =>
            {
                if (appState.ActiveProfile is { } profile)
                {
                    var previousWeather = profile.Data?.Weather;
                    var updated = profile.WithData(sig.Data);
                    appState.ActiveProfile = updated;
                    appState.NeedsRedraw = true;
                    await updated.SaveAsync(external, cts.Token);

                    // Camera / focuser / filter-wheel assignments may have changed; rebuild the per-OTA
                    // settings so gain modes (DSLR ISO vs ZWO numeric) and cooling capability come from
                    // the new device's driver instead of being cached from the old one.
                    sessionState.InitializeFromProfile(updated, appState.DeviceHub);
                    sessionState.NeedsRedraw = true;

                    // Refetch weather when the weather device URI changes (e.g. API key entered)
                    if (sig.Data.Weather != previousWeather)
                    {
                        await FetchWeatherForecastAsync(cts.Token);
                        appState.NeedsRedraw = true;
                    }
                }
            });

            bus.Subscribe<SavePlannerSessionSignal>(async _ =>
            {
                if (!plannerState.IsDirty || appState.ActiveProfile is not { } profile)
                {
                    return;
                }
                plannerState.IsDirty = false;
                await PlannerPersistence.SaveAsync(plannerState, profile, external, _timeProvider, cts.Token);
            });

            bus.Subscribe<SaveSessionConfigSignal>(async _ =>
            {
                if (!sessionState.IsDirty || appState.ActiveProfile is not { } profile)
                {
                    return;
                }
                sessionState.IsDirty = false;
                await SessionPersistence.SaveAsync(sessionState, profile, external, cts.Token);
            });

            // Wire signal bus into state objects for auto-posting on dirty
            plannerState.Bus = bus;
            sessionState.Bus = bus;

            // Refresh per-OTA camera capabilities when a driver connects or disconnects via the hub —
            // gain modes / cooling info may only become known after the driver is actually instantiated.
            if (appState.DeviceHub is { } hub)
            {
                hub.DeviceStateChanged += (_, _) =>
                {
                    if (appState.ActiveProfile is { } profile)
                    {
                        sessionState.InitializeFromProfile(profile, hub);
                        sessionState.NeedsRedraw = true;
                        appState.NeedsRedraw = true;
                    }
                };
            }

            // ---------------------------------------------------------------
            // Schedule building (shared between planner preview and session start)
            // ---------------------------------------------------------------
            bus.Subscribe<BuildScheduleSignal>(signal =>
            {
                if (appState.ActiveProfile is not { } profile) return;
                var transform = TransformFactory.FromProfile(profile, _timeProvider, out _);
                if (transform is null) return;

                var profileData = profile.Data ?? ProfileData.Empty;
                var (filters, design) = GetFirstOtaFilterConfig(profileData);
                // Use the SAME effective defaults the session-start path uses so the planner
                // PREVIEW matches what the session will actually capture (it previously hardcoded
                // gain=120/offset=10/sub=120s here, diverging from both the config and the
                // f-ratio exposure shown on the target rows). Gain/offset stay null so the per-OTA
                // camera settings drive them, exactly as StartSession does.
                PlannerActions.BuildSchedule(plannerState, sessionState, transform,
                    defaultGain: null, defaultOffset: null,
                    defaultSubExposure: SessionContent.EffectiveDefaultSubExposure(sessionState),
                    defaultObservationTime: TimeSpan.FromMinutes(60),
                    availableFilters: filters,
                    opticalDesign: design);
            });

            // ---------------------------------------------------------------
            // Live session signals
            // ---------------------------------------------------------------

            bus.Subscribe<StartSessionSignal>(async _ =>
            {
                if (liveSessionState.IsRunning)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Session already running");
                    return;
                }

                if (appState.ActiveProfile is not { } profile)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "No profile selected");
                    return;
                }

                if (plannerState.Proposals is not { Length: > 0 })
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "No targets \u2014 pin targets in the Planner first");
                    return;
                }

                // Everything past the preconditions -- schedule build, config injection,
                // session create, event wiring, tracked RunAsync -- lives in
                // SessionBootstrapper so this lambda routes only.
                await SessionBootstrapper.BuildAndStartAsync(
                    sp.GetRequiredService<ISessionFactory>(),
                    appState, plannerState, sessionState, liveSessionState, profile,
                    tracker, external, _timeProvider, logger, cts.Token);
            });

            bus.Subscribe<ConfirmAbortSessionSignal>(_ =>
            {
                liveSessionState.SessionCts?.Cancel();
                liveSessionState.ShowAbortConfirm = false;
                liveSessionState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            // ---------------------------------------------------------------
            // Polar alignment signals
            // ---------------------------------------------------------------

            bus.Subscribe<StartPolarAlignmentSignal>(sig =>
            {
                if (liveSessionState.IsRunning)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Session is running \u2014 polar alignment unavailable");
                    return;
                }
                if (liveSessionState.PolarAlignmentCts is not null)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Polar alignment already running");
                    return;
                }
                if (appState.ActiveProfile?.Data is not { } profileData || profileData.OTAs.Length == 0)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "No profile / OTA configured");
                    return;
                }
                if (appState.DeviceHub is not { } hub)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Device hub not available");
                    return;
                }
                if (!hub.TryGetConnectedDriver<IMountDriver>(profileData.Mount, out var mount) || mount is null)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Mount not connected \u2014 connect a mount first");
                    return;
                }
                if (profileData.SiteLatitude is not { } lat || profileData.SiteLongitude is not { } lon)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Site location not configured for this profile");
                    return;
                }
                var solverFactory = sp.GetRequiredService<IPlateSolverFactory>();

                // Build the capture source (guider or main-camera path) + site; the
                // device-resolution + capture-source wiring lives in PolarAlignmentActions
                // so this lambda routes only.
                var built = PolarAlignmentActions.BuildCaptureSource(
                    sig, profileData, hub, mount, liveSessionState,
                    external, sp.GetRequiredService<ICelestialObjectDB>(), _timeProvider, logger);
                if (built.Error is { } buildError)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, buildError);
                    return;
                }
                var source = built.Source!;
                var activeGuider = built.ActiveGuider;

                var site = PolarAlignmentActions.BuildSite(profileData, hub, lat, lon);

                // Setup-panel path supplies the full configuration. Toolbar /
                // legacy callers leave Configuration null and pin only
                // DeltaRaDeg; fall back to Default for everything else.
                var config = sig.Configuration ?? (PolarAlignmentConfiguration.Default with { RotationDeg = sig.DeltaRaDeg });

                var polarCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                liveSessionState.PolarAlignmentCts = polarCts;
                liveSessionState.Mode = LiveSessionMode.PolarAlign;
                liveSessionState.PolarPhase = PolarAlignmentPhase.Idle;
                liveSessionState.PolarStatusMessage = "Starting polar alignment\u2026";
                liveSessionState.PolarPhaseAResult = null;
                liveSessionState.LastPolarSolve = null;
                liveSessionState.NeedsRedraw = true;
                appState.NeedsRedraw = true;

                tracker.Run(async () =>
                {
                    var session = new PolarAlignmentSession(
                        external, mount, source, solverFactory,
                        _timeProvider, logger, site, config);
                    try
                    {
                        // If the guider was looping/calibrating/guiding from a prior session,
                        // stop it cleanly first. PHD2's LoopAsync (used inside GuiderCaptureSource)
                        // will refuse if the app is mid-calibration. Best-effort — failure here
                        // is logged but doesn't fail the routine; the orchestrator's first frame
                        // will surface the real problem with a useful message.
                        if (activeGuider is not null)
                        {
                            try
                            {
                                await activeGuider.StopCaptureAsync(TimeSpan.FromSeconds(10), polarCts.Token);
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "PolarAlignment: guider StopCaptureAsync before run failed");
                            }
                        }

                        await PolarAlignmentActions.RunAsync(session, liveSessionState, logger, polarCts.Token);
                    }
                    catch (OperationCanceledException ex)
                    {
                        logger.LogInformation(ex, "Polar alignment routine cancelled");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Polar alignment routine failed");
                        liveSessionState.PolarPhase = PolarAlignmentPhase.Failed;
                        liveSessionState.PolarStatusMessage = $"Polar alignment error: {ex.Message}";
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Error, $"Polar alignment failed: {ex.Message}");
                    }
                    finally
                    {
                        // ReverseAxisBack / Park / LeaveInPlace runs here.
                        liveSessionState.PolarPhase = PolarAlignmentPhase.RestoringMount;
                        liveSessionState.NeedsRedraw = true;
                        try { await session.DisposeAsync(); }
                        catch (Exception ex) { logger.LogWarning(ex, "PolarAlignmentSession dispose failed"); }
                        liveSessionState.PolarAlignmentCts = null;
                        polarCts.Dispose();
                        // Drop back into preview mode unless the user already swapped tabs.
                        if (liveSessionState.Mode == LiveSessionMode.PolarAlign)
                        {
                            liveSessionState.Mode = LiveSessionMode.Preview;
                        }
                        liveSessionState.PolarPhase = PolarAlignmentPhase.Idle;
                        liveSessionState.NeedsRedraw = true;
                        appState.NeedsRedraw = true;
                    }
                }, "PolarAlignment");
            });

            bus.Subscribe<CancelPolarAlignmentSignal>(_ =>
            {
                // The Cancel button itself flips to an amber "Cancelling..." state
                // while PolarAlignmentCts.IsCancellationRequested is true, and the
                // phase pill carries the technical "RESTORING" badge as the mount
                // reverses, so a third copy on the status line would be redundant.
                liveSessionState.PolarAlignmentCts?.Cancel();
                liveSessionState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<NudgeFakeMountMisalignmentSignal>(sig =>
            {
                // Test path: nudges the simulated misalignment on a fake
                // Skywatcher mount and shows the new value in the status line.
                // For any real (or non-Skywatcher fake) mount, the keys can't
                // turn physical knobs, so we surface a one-line hint instead
                // of silently swallowing the input -- otherwise pressing arrows
                // on a real-mount setup looks broken.
                var profile = appState.ActiveProfile?.Data;
                if (profile?.Mount is not { } mountUri || appState.DeviceHub is not { } hub)
                {
                    return;
                }
                if (!hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var mount) || mount is null)
                {
                    return;
                }
                if (mount is TianWen.Lib.Devices.Fake.FakeSkywatcherMountDriver fake)
                {
                    fake.NudgeMisalignment(sig.DeltaAzArcmin, sig.DeltaAltArcmin);
                    var (az, alt) = fake.CurrentMisalignment;
                    liveSessionState.PolarStatusMessage =
                        $"Sim misalignment: Az {az:+0.0;-0.0;0}', Alt {alt:+0.0;-0.0;0}'";
                }
                else
                {
                    liveSessionState.PolarStatusMessage = "Adjust the mount's alt/az knobs to refine";
                }
                liveSessionState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<DonePolarAlignmentSignal>(_ =>
            {
                // Done is the same exit path as Cancel: stop the refine loop, let the
                // session's DisposeAsync apply the configured OnDone behaviour
                // (ReverseAxisBack by default).
                liveSessionState.PolarAlignmentCts?.Cancel();
                liveSessionState.PolarStatusMessage = "Restoring mount\u2026";
                liveSessionState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            // ---------------------------------------------------------------
            // Flats signals (on-demand flat capture -- LiveSessionMode.Flats)
            // ---------------------------------------------------------------

            bus.Subscribe<StartFlatsSignal>(async sig =>
            {
                if (liveSessionState.IsRunning)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Session is running \u2014 flats unavailable");
                    return;
                }
                if (liveSessionState.FlatsCts is not null)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Flat run already running");
                    return;
                }
                if (appState.ActiveProfile is not { Data: { } profileData } profile || profileData.OTAs.Length == 0)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "No profile / OTA configured");
                    return;
                }

                // Site drives the mount sync + denorm stamp + sky-flat solar-altitude gate; NaN falls back
                // to the mount's own site inside ConnectForFlatsAsync (matches the CLI path).
                var siteLat = profileData.SiteLatitude ?? double.NaN;
                var siteLon = profileData.SiteLongitude ?? double.NaN;

                // Everything past the preconditions -- factory init, config injection, session create,
                // event wiring, tracked RunFlatsOnlyAsync -- lives in FlatsBootstrapper so this lambda
                // routes only (see CLAUDE.md "Signal Handler Pattern").
                await FlatsBootstrapper.BuildAndStartAsync(
                    sp.GetRequiredService<ISessionFactory>(),
                    appState, sessionState, liveSessionState, profile,
                    sig.Source, sig.FlatsPerFilter, siteLat, siteLon,
                    tracker, _timeProvider, logger, cts.Token);
            });

            bus.Subscribe<CancelFlatsSignal>(_ =>
            {
                // The finaliser (close covers, warm, disconnect) still runs on cancel via RunFlatsOnlyAsync's
                // finally block; the panel's Cancel button shows the amber "Cancelling..." state meanwhile.
                liveSessionState.FlatsCts?.Cancel();
                liveSessionState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<RespondSessionPromptSignal>(sig =>
            {
                // Forward the user's Continue/Cancel to the session's awaiting prompt and drop the overlay.
                if (liveSessionState.PendingPrompt is { } prompt)
                {
                    prompt.Respond(sig.Proceed);
                    liveSessionState.PendingPrompt = null;
                    liveSessionState.NeedsRedraw = true;
                    appState.NeedsRedraw = true;
                }
            });

            // ---------------------------------------------------------------
            // Preview mode signals (camera preview, snapshot save, plate solve)
            // ---------------------------------------------------------------

            bus.Subscribe<TakePreviewSignal>(sig =>
            {
                if (liveSessionState.IsRunning)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Session is running \u2014 preview unavailable");
                    return;
                }
                if (appState.ActiveProfile?.Data is not { } previewData || sig.OtaIndex >= previewData.OTAs.Length)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Invalid OTA index");
                    return;
                }
                if (appState.DeviceHub is not { } hub) return;

                var ota = previewData.OTAs[sig.OtaIndex];
                if (!hub.TryGetConnectedDriver<ICameraDriver>(ota.Camera, out var camera) || camera is null)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Camera not connected");
                    return;
                }

                // Resolve the OTA's other devices for per-capture FITS denorm. Mount is
                // optional (preview can fire without one) but unlocks Target stamping
                // and FakeCameraDriver synthetic-catalog rendering when present.
                var (previewFocuser, previewFilterWheel, previewMount) =
                    EquipmentActions.ResolveOtaCaptureDevices(hub, previewData, sig.OtaIndex);

                // Mark capturing
                if (sig.OtaIndex < liveSessionState.PreviewCapturing.Length)
                {
                    liveSessionState.PreviewCapturing[sig.OtaIndex] = true;
                    liveSessionState.PreviewCaptureStart[sig.OtaIndex] = _timeProvider.GetUtcNow();
                    liveSessionState.PreviewExposureDuration[sig.OtaIndex] = TimeSpan.FromSeconds(sig.ExposureSeconds);
                }
                appState.NeedsRedraw = true;

                tracker.Run(async () =>
                {
                    try
                    {
                        // Stamp denorm fields before exposing -- shared with Session.Imaging
                        // and polar alignment so the live preview's FITS headers (and the
                        // FakeCameraDriver's synthetic-catalog rendering) match the other
                        // capture paths exactly.
                        await CameraExposureActions.StampDenormAsync(
                            camera,
                            ota.Name,
                            ota.FocalLength,
                            ota.Aperture,
                            previewFocuser,
                            previewFilterWheel,
                            previewMount,
                            targetName: previewMount is not null ? "Preview" : null,
                            catalogDb: sp.GetRequiredService<ICelestialObjectDB>(),
                            logger: logger,
                            ct: cts.Token).ConfigureAwait(false);

                        var image = await LiveSessionActions.CaptureCameraPreviewAsync(
                            camera,
                            TimeSpan.FromSeconds(sig.ExposureSeconds),
                            sig.Gain is { } g ? (short)g : null,
                            sig.Binning,
                            _timeProvider,
                            cts.Token);

                        if (image is not null
                            && sig.OtaIndex < liveSessionState.LastCapturedImages.Length)
                        {
                            // Release the previous slot's image before replacing -- otherwise
                            // its ChannelBuffer ref never drops and the camera can't recycle
                            // (mirrors the polar-refine onFrameCaptured leak fix).
                            liveSessionState.LastCapturedImages[sig.OtaIndex]?.Release();
                            liveSessionState.LastCapturedImages[sig.OtaIndex] = image;
                            appState.AppendNotification(_timeProvider.GetUtcNow(),
                                NotificationSeverity.Info, $"Preview captured: OTA {sig.OtaIndex + 1}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Warning, "Preview cancelled");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Preview capture failed for OTA {Index}", sig.OtaIndex);
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Error, $"Preview failed: {ex.Message}");
                    }
                    finally
                    {
                        if (sig.OtaIndex < liveSessionState.PreviewCapturing.Length)
                        {
                            liveSessionState.PreviewCapturing[sig.OtaIndex] = false;
                        }
                        appState.NeedsRedraw = true;
                    }
                }, $"PreviewCapture OTA{sig.OtaIndex}");
            });

            bus.Subscribe<SaveSnapshotSignal>(sig =>
            {
                if (liveSessionState.IsRunning) return;
                if (sig.OtaIndex >= liveSessionState.LastCapturedImages.Length) return;
                if (liveSessionState.LastCapturedImages[sig.OtaIndex] is not { } image)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "No preview image to save");
                    return;
                }

                tracker.Run(async () =>
                {
                    try
                    {
                        var fileName = await LiveSessionActions.SaveSnapshotAsync(
                            image, sig.OtaIndex, external, _timeProvider);
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Info, $"Snapshot saved: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Snapshot save failed");
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Error, $"Snapshot failed: {ex.Message}");
                    }
                    finally
                    {
                        appState.NeedsRedraw = true;
                    }
                }, "SaveSnapshot");
            });

            bus.Subscribe<PlateSolvePreviewSignal>(sig =>
            {
                if (liveSessionState.IsRunning) return;
                if (sig.OtaIndex >= liveSessionState.LastCapturedImages.Length) return;
                if (liveSessionState.LastCapturedImages[sig.OtaIndex] is not { } image)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "No preview image to solve");
                    return;
                }
                // Drop duplicate clicks: if a solve is already running for this OTA,
                // ignore. The button is rendered as "Solving…" with no click handler,
                // but a stray hit before the redraw could still fire the signal.
                if (sig.OtaIndex < liveSessionState.PreviewPlateSolving.Length
                    && liveSessionState.PreviewPlateSolving[sig.OtaIndex])
                {
                    return;
                }

                if (sig.OtaIndex < liveSessionState.PreviewPlateSolving.Length)
                {
                    liveSessionState.PreviewPlateSolving[sig.OtaIndex] = true;
                }
                liveSessionState.NeedsRedraw = true;
                appState.NeedsRedraw = true;

                tracker.Run(async () =>
                {
                    try
                    {
                        appState.StatusMessage = "Plate solving\u2026";
                        appState.NeedsRedraw = true;

                        // Solve orchestration (search-origin derivation + result-to-message
                        // mapping) lives in LiveSessionActions so this lambda routes only.
                        var (result, message, solved) = await LiveSessionActions.SolvePreviewFrameAsync(
                            sp.GetRequiredService<IPlateSolverFactory>(), image, cts.Token);
                        liveSessionState.PreviewPlateSolveResult = result;
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            solved ? NotificationSeverity.Info : NotificationSeverity.Warning, message);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Preview plate solve failed");
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Error, $"Plate solve error: {ex.Message}");
                    }
                    finally
                    {
                        if (sig.OtaIndex < liveSessionState.PreviewPlateSolving.Length)
                        {
                            liveSessionState.PreviewPlateSolving[sig.OtaIndex] = false;
                        }
                        liveSessionState.NeedsRedraw = true;
                        appState.NeedsRedraw = true;
                    }
                }, "PreviewPlateSolve");
            });

            bus.Subscribe<JogFocuserSignal>(sig =>
            {
                if (liveSessionState.IsRunning) return;
                if (appState.ActiveProfile?.Data is not { OTAs: var otas } || sig.OtaIndex >= otas.Length) return;
                if (appState.DeviceHub is not { } hub) return;

                var ota = otas[sig.OtaIndex];
                if (ota.Focuser is not { } focUri) return;
                if (!hub.TryGetConnectedDriver<IFocuserDriver>(focUri, out var focuser) || focuser is null) return;

                tracker.Run(async () =>
                {
                    try
                    {
                        var targetPos = await LiveSessionActions.JogFocuserAsync(focuser, sig.Steps, cts.Token);
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Info, $"Focuser \u2192 {targetPos}");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Focuser jog failed for OTA {Index}", sig.OtaIndex);
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Error, $"Focuser jog failed: {ex.Message}");
                    }
                    finally
                    {
                        appState.NeedsRedraw = true;
                    }
                }, $"JogFocuser OTA{sig.OtaIndex}");
            });

            // Live planetary capture: route Start/Stop to the shared PlanetaryCaptureController (the capture
            // loop + rolling-window stack live there; this only resolves the camera + configures the ROI).
            var planetaryCapture = sp.GetRequiredService<PlanetaryCaptureController>();

            bus.Subscribe<StartVideoCaptureSignal>(sig =>
            {
                if (liveSessionState.IsRunning) return;
                if (appState.ActiveProfile?.Data is not { OTAs: var otas } || sig.OtaIndex >= otas.Length) return;
                if (appState.DeviceHub is not { } hub) return;

                var ota = otas[sig.OtaIndex];
                if (!hub.TryGetConnectedDriver<ICameraDriver>(ota.Camera, out var camera) || camera is null)
                {
                    appState.AppendNotification(_timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Connect a camera to start a planetary capture");
                    appState.NeedsRedraw = true;
                    return;
                }

                var (roiW, roiH) = PlanetaryCaptureActions.ConfigureRoi(camera, sig.RoiWidth, sig.RoiHeight);

                // Wire the coupled mount + OTA pixel scale for the COM recenter loop's coarse mount-jog
                // fallback (Phase C). No-op when no mount is connected or the scale is unknown (NaN) -- the
                // recenter loop then stays ROI-only.
                IMountDriver? recenterMount = null;
                if (appState.ActiveProfile?.Data is { } capturePdata
                    && capturePdata.Mount is { Scheme: not "none" } mountUri
                    && hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var rcMount) && rcMount is not null)
                {
                    recenterMount = rcMount;
                }
                planetaryCapture.AttachMount(
                    recenterMount, CoordinateUtils.PixelScaleArcsec(camera.PixelSizeX, ota.FocalLength));

                // Bind the capture's lifetime to the app shutdown token: quitting cancels it (its loops poll
                // the token), so the camera is released without an imperative Stop() in the quit path.
                planetaryCapture.Start(camera,
                    new VideoCaptureOptions(TimeSpan.FromMilliseconds(sig.ExposureMs), sig.Gain), shutdownToken);
                // Planetary capture is now a Live Session mode (not a standalone tab): show it there.
                liveSessionState.Mode = LiveSessionMode.Planetary;
                appState.ActiveTab = GuiTab.LiveSession;
                appState.AppendNotification(_timeProvider.GetUtcNow(),
                    NotificationSeverity.Info, $"Planetary capture started ({roiW}x{roiH}, {sig.ExposureMs:F0} ms)");
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<StopVideoCaptureSignal>(_ =>
            {
                planetaryCapture.Stop();
                appState.AppendNotification(_timeProvider.GetUtcNow(),
                    NotificationSeverity.Info, "Planetary capture stopped");
                appState.NeedsRedraw = true;
            });

            // Manual mount nudge (planetary panel coarse-recenter buttons) -> the same pulse-guide actuator the
            // COM recenter loop uses. Mirrors the focuser-jog route: resolve the connected mount, pulse on the
            // tracker. Gated on no running session + a pulse-guide-capable mount.
            bus.Subscribe<JogMountSignal>(sig =>
            {
                if (liveSessionState.IsRunning) return;
                if (appState.ActiveProfile?.Data is not { } pdata) return;
                if (pdata.Mount is not { Scheme: not "none" } mountUri) return;
                if (appState.DeviceHub is not { } hub) return;
                if (!hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var mount) || mount is null) return;

                tracker.Run(async () =>
                {
                    try
                    {
                        await MountActions.PulseGuideArcsecAsync(
                            mount, sig.Direction, sig.Arcsec, _timeProvider, logger: logger, cancellationToken: cts.Token);
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Info, $"Mount nudge {sig.Direction} {sig.Arcsec:F0} arcsec");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Mount jog failed ({Dir})", sig.Direction);
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Error, $"Mount jog failed: {ex.Message}");
                    }
                    finally
                    {
                        appState.NeedsRedraw = true;
                    }
                }, $"JogMount {sig.Direction}");
            });

            bus.Subscribe<GotoFocuserSignal>(sig =>
            {
                if (liveSessionState.IsRunning) return;
                if (appState.ActiveProfile?.Data is not { OTAs: var otas } || sig.OtaIndex >= otas.Length) return;
                if (appState.DeviceHub is not { } hub) return;

                var ota = otas[sig.OtaIndex];
                if (ota.Focuser is not { } focUri) return;
                if (!hub.TryGetConnectedDriver<IFocuserDriver>(focUri, out var focuser) || focuser is null) return;

                tracker.Run(async () =>
                {
                    try
                    {
                        await focuser.BeginMoveAsync(sig.TargetPosition, cts.Token);
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Info, $"Focuser \u2192 {sig.TargetPosition}");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Focuser goto failed for OTA {Index}", sig.OtaIndex);
                        appState.AppendNotification(_timeProvider.GetUtcNow(),
                            NotificationSeverity.Error, $"Focuser goto failed: {ex.Message}");
                    }
                    finally
                    {
                        appState.NeedsRedraw = true;
                    }
                }, $"GotoFocuser OTA{sig.OtaIndex}");
            });

            // ---------------------------------------------------------------
            // Local helpers captured by closures above
            // ---------------------------------------------------------------
            void CommitSuggestion(string suggestion)
            {
                plannerState.SearchInput.Text = suggestion;
                plannerState.SearchInput.CursorPos = suggestion.Length;
                plannerState.Suggestions.Clear();
                plannerState.SuggestionIndex = -1;
                plannerState.LastSuggestionQuery = suggestion;

                if (appState.ActiveProfile is not null)
                {
                    var transform = TransformFactory.FromProfile(appState.ActiveProfile, _timeProvider, out _);
                    if (transform is not null)
                    {
                        var db = sp.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>();
                        var resultIdx = PlannerActions.CommitSuggestion(plannerState, db, transform, suggestion, plannerState.Comets);
                        if (resultIdx >= 0)
                        {
                            plannerState.SelectedTargetIndex = resultIdx;
                            OnPlannerEnsureVisible?.Invoke(resultIdx);
                        }
                    }
                }
                appState.NeedsRedraw = true;
            }

            // Store autocomplete cache setter as a public action
            SetAutoCompleteCache = cache => autoCompleteCache = cache;
        }
    }
}
