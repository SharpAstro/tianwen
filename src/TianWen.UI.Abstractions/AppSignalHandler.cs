using System;
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
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

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
            var objectDb = _sp.GetRequiredService<ICelestialObjectDB>();
            var swInit = System.Diagnostics.Stopwatch.StartNew();
            await objectDb.InitDBAsync(cancellationToken);
            swInit.Stop();
            _logger.LogInformation("InitializePlanner: catalog ready in {Elapsed}ms; publishing to PlannerState.ObjectDb",
                swInit.ElapsedMilliseconds);
            _plannerState.ObjectDb = objectDb;
            _plannerState.NeedsRedraw = true;
            SetAutoCompleteCache(objectDb.CreateAutoCompleteList());
            await PlannerActions.ComputeTonightsBestAsync(
                _plannerState, objectDb, transform,
                _plannerState.MinHeightAboveHorizon, cancellationToken);
            if (_appState.ActiveProfile is { } profile)
            {
                await PlannerPersistence.TryLoadAsync(_plannerState, profile, _external, _logger, _timeProvider, cancellationToken);
            }
            await FetchWeatherForecastAsync(cancellationToken);
            _plannerState.SelectedTargetIndex = 0;
            _plannerState.NeedsRedraw = true;
            _logger.LogInformation("InitializePlanner: complete");
        }

        // Per-camera last-poll timestamps, keyed by URI path (host+path, no query).
        private readonly Dictionary<string, long> _telemetryLastSampleTicks = new();
        // In-flight poll task per camera path, prevents overlapping samples.
        private readonly HashSet<string> _telemetryInFlight = new();

        // Preview-mode poll state (mirrors _telemetryLastSampleTicks pattern)
        private readonly Dictionary<string, long> _previewTelemetryLastTicks = new();
        private readonly HashSet<string> _previewTelemetryInFlight = new();
        private long _previewMountLastTicks;
        private bool _previewMountInFlight;
        private bool _loggedFirstPreviewMountSample;


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

                if (_telemetryInFlight.Contains(key)) continue;
                if (_telemetryLastSampleTicks.TryGetValue(key, out var lastTicks)
                    && _timeProvider.GetElapsedTime(lastTicks, nowTicks) < sampleInterval)
                {
                    continue;
                }

                _telemetryLastSampleTicks[key] = nowTicks;
                _telemetryInFlight.Add(key);
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
                        _telemetryInFlight.Remove(key);
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
                _timeProvider.System.GetLocalNow(),
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

            // Preview polling drives the Live Session tab AND the Sky Map tab (for the
            // mount-position reticle overlay). Any tab that displays live mount / focuser
            // state should be added here rather than spinning up a parallel poll path —
            // two concurrent polls on the same serial mount would race the port.
            if (_appState.ActiveTab is not (GuiTab.LiveSession or GuiTab.SkyMap)) return;
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

                if (_previewTelemetryInFlight.Contains(key)) continue;

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
                _previewTelemetryInFlight.Add(key);

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
                        _previewTelemetryInFlight.Remove(key);
                    }
                }, $"PreviewTelemetry OTA{capturedIndex}");
            }

            // Mount polling — rate-adaptive on last-known slew/tracking state.
            // Steady sidereal tracking is ~15 arcsec/s, which is sub-pixel on any sky-map
            // FOV we support — 10s cadence is sensory-overkill and massively reduces the
            // baseline hardware load (6 serial reads per tick). Fast 500 ms cadence only
            // while slewing, so the reticle keeps up with visible motion.
            var prevMount = _liveSessionState.PreviewMountState;
            var mountInterval = prevMount.IsSlewing
                ? TimeSpan.FromMilliseconds(500)
                : prevMount.IsTracking
                    ? TimeSpan.FromSeconds(10)
                    : TimeSpan.FromSeconds(2);

            if (!_previewMountInFlight
                && _timeProvider.GetElapsedTime(_previewMountLastTicks, nowTicks) >= mountInterval
                && profileData.Mount is { Scheme: not "none" } mountUri)
            {
                _previewMountLastTicks = nowTicks;
                _previewMountInFlight = true;

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
                        _liveSessionState.PreviewMountState = ms;
                        if (displayName is not null)
                        {
                            _liveSessionState.PreviewMountDisplayName = displayName;
                        }
                        _appState.NeedsRedraw = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Preview mount poll failed");
                    }
                    finally
                    {
                        _previewMountInFlight = false;
                    }
                }, "PreviewMount");
            }
        }

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

            var ra = await _logger.CatchAsync(mount.GetRightAscensionAsync, ct);
            var dec = await _logger.CatchAsync(mount.GetDeclinationAsync, ct);
            var ha = await _logger.CatchAsync(mount.GetHourAngleAsync, ct);
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
                                _plannerState.MinHeightAboveHorizon, _cts.Token);
                            if (_appState.ActiveProfile is { } profile)
                            {
                                await PlannerPersistence.TryLoadAsync(_plannerState, profile, _external, _logger, _timeProvider, _cts.Token);
                            }
                            SetAutoCompleteCache(objectDb.CreateAutoCompleteList());
                        }
                        await FetchWeatherForecastAsync(_cts.Token);
                        _appState.StatusMessage = null;
                    }
                    else
                    {
                        _appState.StatusMessage = "Set site coordinates in Equipment tab";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Recompute failed");
                    _appState.StatusMessage = $"Recompute failed: {ex.InnerException?.Message ?? ex.Message}";
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
                        var resultIdx = PlannerActions.SearchTargets(plannerState, db, transform, text);
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
                SkyMapSearchActions.OpenSearch(skySearch, db);
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
                var viewingUtc = plannerState.PlanningDate?.ToUniversalTime() ?? _timeProvider.GetUtcNow();
                var site = SiteContext.Create(plannerState.SiteLatitude, plannerState.SiteLongitude, viewingUtc);
                SkyMapSearchActions.CommitResult(
                    skySearch, skyMapState, db,
                    plannerState.SiteLatitude, plannerState.SiteLongitude,
                    viewingUtc, site);
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
                        PlannerActions.CommitSuggestion(plannerState, db, transform, sig.Name);
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

            bus.Subscribe<SkyMapClickSelectSignal>(sig =>
            {
                var rect = skyMapState.LastContentRect;
                if (rect.Width <= 0 || rect.Height <= 0) return;

                var db = sp.GetRequiredService<ICelestialObjectDB>();
                var ppr = SkyMapProjection.PixelsPerRadian(rect.Height, skyMapState.FieldOfViewDeg);
                var cx = rect.X + rect.Width * 0.5f;
                var cy = rect.Y + rect.Height * 0.5f;
                var viewingUtc = plannerState.PlanningDate?.ToUniversalTime() ?? _timeProvider.GetUtcNow();
                var site = SiteContext.Create(plannerState.SiteLatitude, plannerState.SiteLongitude, viewingUtc);

                SkyMapSearchActions.SelectObjectByClick(
                    skySearch, skyMapState, db,
                    plannerState.SiteLatitude, plannerState.SiteLongitude,
                    viewingUtc, site,
                    sig.ScreenX, sig.ScreenY,
                    skyMapState.CurrentViewMatrix, ppr, cx, cy);

                skyMapState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
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

            // Site inputs share a commit: save site on Enter from any of the three fields
            Func<Task> saveSite = async () =>
            {
                if (appState.ActiveProfile is not { } siteProfile)
                {
                    return;
                }

                if (double.TryParse(eqState.LatitudeInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var sLat) &&
                    double.TryParse(eqState.LongitudeInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var sLon) &&
                    sLat is >= -90 and <= 90 && sLon is >= -180 and <= 180)
                {
                    double? sElev = double.TryParse(eqState.ElevationInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var e) ? e : null;
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

                    // If a mount is connected and the tie-breaker says Profile wins,
                    // push the new site to the mount hardware.
                    if (newSiteData.SiteTieBreaker == SiteTieBreaker.Profile
                        && appState.DeviceHub is { } hubForPush
                        && hubForPush.TryGetConnectedDriver<IMountDriver>(newSiteData.Mount, out var mountForPush)
                        && mountForPush is not null)
                    {
                        try
                        {
                            await mountForPush.SetSiteLatitudeAsync(sLat, cts.Token);
                            await mountForPush.SetSiteLongitudeAsync(sLon, cts.Token);
                            if (sElev is { } elevForPush)
                            {
                                await mountForPush.SetSiteElevationAsync(elevForPush, cts.Token);
                            }
                        }
                        catch (Exception pushEx)
                        {
                            logger.LogWarning(pushEx, "Failed to push site to connected mount.");
                        }
                    }

                    await updatedSite.SaveAsync(external, cts.Token);
                }
                else
                {
                    appState.StatusMessage = "Invalid coordinates (lat: -90..90, lon: -180..180)";
                }
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
            eqState.StringSettingInput.OnCommit = _ =>
            {
                if (eqState.EditingStringSettingKey is { } key && eqState.EditingDeviceUri is { } editUri)
                {
                    eqState.EditingDeviceUri = DeviceSettingHelper.WithQueryParam(editUri, key, eqState.StringSettingInput.Text);
                    eqState.EditingStringSettingKey = null;
                    // Auto-save: apply the updated URI to the profile
                    if (appState.ActiveProfile is { Data: { } data } && eqState.EditingDeviceUri is { } newUri
                        && eqState.SavedDeviceSettingsUri is { } savedUri)
                    {
                        var newData = EquipmentActions.UpdateDeviceUri(data, savedUri, newUri);
                        bus.Post(new UpdateProfileSignal(newData));
                        eqState.BeginEditingDeviceSettings(newUri);
                    }
                }
                return Task.CompletedTask;
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
                    appState.StatusMessage = "Discovery failed";
                }
                finally
                {
                    eqState.IsDiscovering = false;
                    appState.StatusMessage = eqState.DiscoveredDevices.Count > 0
                        ? $"Found {eqState.DiscoveredDevices.Count} devices"
                        : null;
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
                        appState.StatusMessage = $"Expected {target.ExpectedDeviceType}, got {device.DeviceType}";
                        return;
                    }

                    var data = profile.Data ?? ProfileData.Empty;

                    // Capture the URI previously assigned to THIS slot. If still connected
                    // via the hub, we'll auto-disconnect it after assignment iff safe
                    // (cooler off, idle). Cool/busy orphans are left connected and the
                    // user is told to disconnect manually so warm-up runs.
                    var prevSlotUri = EquipmentActions.GetAssignedDevice(data, target);

                    data = EquipmentActions.UnassignDevice(data, device.DeviceUri);

                    var newData = target switch
                    {
                        AssignTarget.ProfileLevel { Field: "Mount" } => EquipmentActions.AssignMount(data, device.DeviceUri),
                        AssignTarget.ProfileLevel { Field: "Guider" } => EquipmentActions.AssignGuider(data, device.DeviceUri),
                        AssignTarget.ProfileLevel { Field: "GuiderCamera" } => EquipmentActions.AssignGuiderCamera(data, device.DeviceUri),
                        AssignTarget.ProfileLevel { Field: "GuiderFocuser" } => EquipmentActions.AssignGuiderFocuser(data, device.DeviceUri),
                        AssignTarget.ProfileLevel { Field: "Weather" } => EquipmentActions.AssignWeather(data, device.DeviceUri),
                        AssignTarget.OTALevel otaTarget => EquipmentActions.AssignDeviceToOTA(data, otaTarget.OtaIndex,
                            device.DeviceType, device.DeviceUri),
                        _ => data
                    };

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

                    // Auto-disconnect the orphan if it's still connected and safe.
                    if (prevSlotUri is not null
                        && !DeviceBase.SameDevice(prevSlotUri, device.DeviceUri)
                        && appState.DeviceHub is { } hub
                        && hub.IsConnected(prevSlotUri))
                    {
                        var safety = await EquipmentActions.GetDisconnectSafetyAsync(hub, prevSlotUri, cts.Token);
                        if (safety == EquipmentActions.DisconnectSafety.Safe)
                        {
                            try
                            {
                                await hub.DisconnectAsync(prevSlotUri, cts.Token);
                                appState.StatusMessage = $"Previous {target.ExpectedDeviceType} disconnected";
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Auto-disconnect of orphan {Uri} failed", prevSlotUri);
                            }
                        }
                        else
                        {
                            // Don't yank a cold/busy camera silently — leave it for the user.
                            appState.StatusMessage = $"Previous {target.ExpectedDeviceType} left connected ({safety}). Click Off on its row to warm up.";
                        }
                        appState.NeedsRedraw = true;
                    }
                }
            });

            bus.Subscribe<ConnectDeviceSignal>(async sig =>
            {
                if (appState.DeviceHub is not { } hub)
                {
                    appState.StatusMessage = "Device hub unavailable";
                    return;
                }

                if (!eqState.PendingTransitions.Add(sig.DeviceUri))
                {
                    return; // transition already in flight
                }
                appState.NeedsRedraw = true;

                try
                {
                    // Prefer the resolved device (carries query-param config); fall back
                    // to a freshly-discovered match by URI equality.
                    DeviceBase? device = null;
                    if (hub.TryGetDeviceFromUri(sig.DeviceUri, out var resolved))
                    {
                        device = resolved;
                    }
                    else
                    {
                        foreach (var d in eqState.DiscoveredDevices)
                        {
                            if (DeviceBase.SameDevice(d.DeviceUri, sig.DeviceUri))
                            {
                                device = d;
                                break;
                            }
                        }
                    }

                    if (device is null)
                    {
                        appState.StatusMessage = "Cannot resolve device URI for connect";
                        return;
                    }

                    await hub.ConnectAsync(device, cts.Token);
                    appState.StatusMessage = $"Connected: {device.DisplayName}";

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
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Connect failed for {Uri}", sig.DeviceUri);
                    appState.StatusMessage = $"Connect failed: {ex.Message}";
                }
                finally
                {
                    eqState.PendingTransitions.Remove(sig.DeviceUri);
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

                if (!eqState.PendingTransitions.Add(sig.DeviceUri))
                {
                    return;
                }
                appState.NeedsRedraw = true;

                try
                {
                    await hub.DisconnectAsync(sig.DeviceUri, cts.Token);
                    appState.StatusMessage = "Device disconnected";
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Disconnect failed for {Uri}", sig.DeviceUri);
                    appState.StatusMessage = $"Disconnect failed: {ex.Message}";
                }
                finally
                {
                    eqState.PendingTransitions.Remove(sig.DeviceUri);
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
                if (!eqState.PendingTransitions.Add(sig.DeviceUri))
                {
                    return;
                }
                appState.NeedsRedraw = true;

                try
                {
                    await hub.DisconnectAsync(sig.DeviceUri, cts.Token);
                    appState.StatusMessage = "Device force-disconnected (no warm-up)";
                    logger.LogWarning("Force-disconnect of {Uri} (bypassed safety check)", sig.DeviceUri);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Force-disconnect failed for {Uri}", sig.DeviceUri);
                    appState.StatusMessage = $"Force-disconnect failed: {ex.Message}";
                }
                finally
                {
                    eqState.PendingTransitions.Remove(sig.DeviceUri);
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
                if (!eqState.PendingTransitions.Add(sig.DeviceUri))
                {
                    return;
                }
                appState.NeedsRedraw = true;

                try
                {
                    await EquipmentActions.WarmAndDisconnectAsync(hub, sig.DeviceUri, _logger, cts.Token);
                    appState.StatusMessage = "Camera warmed and disconnected";
                }
                catch (OperationCanceledException)
                {
                    appState.StatusMessage = "Warm-up cancelled";
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Warm-and-disconnect failed for {Uri}", sig.DeviceUri);
                    appState.StatusMessage = $"Warm-up failed: {ex.Message}";
                }
                finally
                {
                    eqState.PendingTransitions.Remove(sig.DeviceUri);
                    appState.NeedsRedraw = true;
                }
            });

            bus.Subscribe<SetCoolerSetpointSignal>(async sig =>
            {
                if (appState.DeviceHub is not { } hub) return;
                if (!hub.TryGetConnectedDriver<TianWen.Lib.Devices.ICameraDriver>(sig.DeviceUri, out var camera))
                {
                    appState.StatusMessage = "Camera not connected";
                    return;
                }
                try
                {
                    await camera.SetSetCCDTemperatureAsync(sig.SetpointC, cts.Token);
                    if (camera.CanSetCoolerOn) await camera.SetCoolerOnAsync(true, cts.Token);
                    appState.StatusMessage = $"Cooling to {sig.SetpointC:F1}\u00b0C";
                    appState.NeedsRedraw = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SetCoolerSetpoint failed for {Uri}", sig.DeviceUri);
                    appState.StatusMessage = $"Cooler setpoint failed: {ex.Message}";
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
                    appState.StatusMessage = "Camera warmed; cooler off";
                }
                catch (OperationCanceledException) { appState.StatusMessage = "Warm-up cancelled"; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Warm-and-cooler-off failed for {Uri}", sig.DeviceUri);
                    appState.StatusMessage = $"Warm-up failed: {ex.Message}";
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
                    if (camera.CanSetCoolerOn) await camera.SetCoolerOnAsync(false, cts.Token);
                    appState.StatusMessage = "Cooler off";
                    appState.NeedsRedraw = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SetCoolerOff failed for {Uri}", sig.DeviceUri);
                    appState.StatusMessage = $"Cooler off failed: {ex.Message}";
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
                PlannerActions.BuildSchedule(plannerState, sessionState, transform,
                    defaultGain: 120, defaultOffset: 10,
                    defaultSubExposure: TimeSpan.FromSeconds(120),
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
                    appState.StatusMessage = "Session already running";
                    return;
                }

                if (appState.ActiveProfile is not { } profile)
                {
                    appState.StatusMessage = "No profile selected";
                    return;
                }

                if (plannerState.Proposals is not { Length: > 0 })
                {
                    appState.StatusMessage = "No targets — pin targets in the Planner first";
                    return;
                }

                try
                {
                    // Switch to live session tab immediately so user sees progress
                    // Set IsRunning immediately to prevent double-start from rapid clicks
                    liveSessionState.IsRunning = true;
                    liveSessionState.Phase = SessionPhase.NotStarted;
                    liveSessionState.ShowAbortConfirm = false;
                    liveSessionState.ExposureLogScrollOffset = 0;
                    liveSessionState.FocusHistoryScrollOffset = 0;
                    appState.ActiveTab = GuiTab.LiveSession;
                    appState.StatusMessage = "Building schedule\u2026";
                    appState.NeedsRedraw = true;

                    var factory = sp.GetRequiredService<ISessionFactory>();
                    var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    liveSessionState.SessionCts = sessionCts;

                    // Build schedule from proposals using planner's window allocation
                    var profileData = profile.Data ?? ProfileData.Empty;
                    var transform = TransformFactory.FromProfile(profile, _timeProvider, out var transformError);
                    if (transform is null)
                    {
                        appState.StatusMessage = "Cannot determine site location from profile";
                        liveSessionState.IsRunning = false;
                        return;
                    }

                    var (filters, design) = GetFirstOtaFilterConfig(profileData);

                    var subExposure = sessionState.Configuration.DefaultSubExposure ?? TimeSpan.FromSeconds(120);
                    _logger.LogInformation("BuildSchedule: DefaultSubExposure={SubExposure} (config={Config})",
                        subExposure, sessionState.Configuration.DefaultSubExposure);
                    PlannerActions.BuildSchedule(plannerState, sessionState, transform,
                        defaultGain: null, defaultOffset: null,
                        defaultSubExposure: subExposure,
                        defaultObservationTime: TimeSpan.FromMinutes(60),
                        availableFilters: filters,
                        opticalDesign: design);

                    if (sessionState.Schedule is not { Count: > 0 } schedule)
                    {
                        appState.StatusMessage = "Failed to build schedule from proposals";
                        liveSessionState.IsRunning = false;
                        return;
                    }

                    logger.LogDebug("StartSession: schedule built with {Count} observations, initialising factory", schedule.Count);
                    appState.StatusMessage = "Initialising session...";
                    appState.NeedsRedraw = true;
                    await factory.InitializeAsync(sessionCts.Token);

                    // Create session from pre-built schedule with proper time windows
                    var observations = new ScheduledObservation[schedule.Count];
                    for (var i = 0; i < schedule.Count; i++)
                    {
                        observations[i] = schedule[i];
                    }
                    // Inject site coordinates and per-OTA setpoint into session configuration
                    var setpointTempC = sessionState.CameraSettings is { Count: > 0 }
                        ? sessionState.CameraSettings[0].SetpointTempC
                        : sessionState.Configuration.SetpointCCDTemperature.TempC;
                    var config = sessionState.Configuration with
                    {
                        SiteLatitude = plannerState.SiteLatitude,
                        SiteLongitude = plannerState.SiteLongitude,
                        SetpointCCDTemperature = new SetpointTemp(setpointTempC, SetpointTempKind.Normal)
                    };
                    logger.LogDebug("StartSession: site lat={Lat}, lon={Lon}, setpoint={Setpoint}°C",
                        config.SiteLatitude, config.SiteLongitude, setpointTempC);

                    var session = factory.Create(
                        profile.ProfileId,
                        config,
                        observations.AsSpan());
                    logger.LogDebug("StartSession: session created with {OtaCount} OTAs, launching RunAsync",
                        session.Setup.Telescopes.Length);

                    liveSessionState.ActiveSession = session;
                    liveSessionState.SiteTimeZone = plannerState.SiteTimeZone;
                    appState.StatusMessage = "Session started";
                    appState.NeedsRedraw = true;

                    // RunAsync includes Finalise — run as tracked background task so:
                    // 1. UI stays responsive (signal handler returns immediately)
                    // 2. DrainAsync at shutdown waits for Finalise to complete
                    tracker.Run(async () =>
                    {
                        try
                        {
                            await session.RunAsync(sessionCts.Token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogError(ex, "Session run failed");
                        }
                        finally
                        {
                            liveSessionState.IsRunning = false;
                            liveSessionState.NeedsRedraw = true;
                            appState.StatusMessage = liveSessionState.Phase switch
                            {
                                SessionPhase.Complete => "Session complete",
                                SessionPhase.Aborted => "Session aborted",
                                SessionPhase.Failed => "Session failed",
                                _ => null
                            };
                            appState.NeedsRedraw = true;
                        }
                    }, "Session run");
                }
                catch (OperationCanceledException)
                {
                    appState.StatusMessage = "Session cancelled";
                    liveSessionState.IsRunning = false;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to start session");
                    appState.StatusMessage = $"Session failed: {ex.Message}";
                    liveSessionState.IsRunning = false;
                }
            });

            bus.Subscribe<ConfirmAbortSessionSignal>(_ =>
            {
                liveSessionState.SessionCts?.Cancel();
                liveSessionState.ShowAbortConfirm = false;
                liveSessionState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            // ---------------------------------------------------------------
            // Preview mode signals (camera preview, snapshot save, plate solve)
            // ---------------------------------------------------------------

            bus.Subscribe<TakePreviewSignal>(sig =>
            {
                if (liveSessionState.IsRunning)
                {
                    appState.StatusMessage = "Session is running \u2014 preview unavailable";
                    return;
                }
                if (appState.ActiveProfile?.Data is not { OTAs: var otas } || sig.OtaIndex >= otas.Length)
                {
                    appState.StatusMessage = "Invalid OTA index";
                    return;
                }
                if (appState.DeviceHub is not { } hub) return;

                var ota = otas[sig.OtaIndex];
                if (!hub.TryGetConnectedDriver<ICameraDriver>(ota.Camera, out var camera) || camera is null)
                {
                    appState.StatusMessage = "Camera not connected";
                    return;
                }

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
                            liveSessionState.LastCapturedImages[sig.OtaIndex] = image;
                            appState.StatusMessage = $"Preview captured: OTA {sig.OtaIndex + 1}";
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        appState.StatusMessage = "Preview cancelled";
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Preview capture failed for OTA {Index}", sig.OtaIndex);
                        appState.StatusMessage = $"Preview failed: {ex.Message}";
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
                    appState.StatusMessage = "No preview image to save";
                    return;
                }

                tracker.Run(async () =>
                {
                    try
                    {
                        var fileName = await LiveSessionActions.SaveSnapshotAsync(
                            image, sig.OtaIndex, external, _timeProvider);
                        appState.StatusMessage = $"Snapshot saved: {fileName}";
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Snapshot save failed");
                        appState.StatusMessage = $"Snapshot failed: {ex.Message}";
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
                    appState.StatusMessage = "No preview image to solve";
                    return;
                }

                tracker.Run(async () =>
                {
                    try
                    {
                        appState.StatusMessage = "Plate solving\u2026";
                        appState.NeedsRedraw = true;
                        var solver = sp.GetRequiredService<IPlateSolverFactory>();
                        var result = await solver.SolveImageAsync(image, cancellationToken: cts.Token);
                        liveSessionState.PreviewPlateSolveResult = result;
                        appState.StatusMessage = result.Solution is { } wcs
                            ? $"Solved: RA {wcs.CenterRA:F3}h Dec {wcs.CenterDec:F2}\u00B0"
                            : "Plate solve failed \u2014 no match";
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Preview plate solve failed");
                        appState.StatusMessage = $"Plate solve error: {ex.Message}";
                    }
                    finally
                    {
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
                        appState.StatusMessage = $"Focuser \u2192 {targetPos}";
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Focuser jog failed for OTA {Index}", sig.OtaIndex);
                        appState.StatusMessage = $"Focuser jog failed: {ex.Message}";
                    }
                    finally
                    {
                        appState.NeedsRedraw = true;
                    }
                }, $"JogFocuser OTA{sig.OtaIndex}");
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
                        var resultIdx = PlannerActions.CommitSuggestion(plannerState, db, transform, suggestion);
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
