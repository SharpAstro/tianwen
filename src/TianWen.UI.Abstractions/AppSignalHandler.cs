using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;
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
        private readonly ILogger _logger;
        private readonly ITimeProvider _timeProvider;

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
            var objectDb = _sp.GetRequiredService<ICelestialObjectDB>();
            await objectDb.InitDBAsync(cancellationToken);
            _plannerState.ObjectDb = objectDb;
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

                        if (_plannerState.TonightsBest.Count > 0 && !siteChanged)
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
                await SessionPersistence.TryLoadAsync(_sessionState, profile, _external, cancellationToken, _appState.DeviceUriRegistry);
            }
        }

        public AppSignalHandler(
            IServiceProvider sp,
            GuiAppState appState,
            PlannerState plannerState,
            SessionTabState sessionState,
            EquipmentTabState eqState,
            LiveSessionState liveSessionState,
            SignalBus bus,
            BackgroundTaskTracker tracker,
            CancellationTokenSource cts,
            IExternal external)
        {
            _sp = sp;
            _appState = appState;
            _plannerState = plannerState;
            _sessionState = sessionState;
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
                plannerState.SearchResults.Clear();
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
                    plannerState.NeedsRecompute = true;
                    appState.NeedsRedraw = true;
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
                    var dm = sp.GetRequiredService<ICombinedDeviceManager>();
                    await dm.CheckSupportAsync(cts.Token);
                    await dm.DiscoverAsync(cts.Token);
                    eqState.DiscoveredDevices = [.. dm.RegisteredDeviceTypes
                        .Where(t => t is not DeviceType.Profile and not DeviceType.None)
                        .SelectMany(dm.RegisteredDevices)
                        .Where(d => sig.IncludeFake || d is not TianWen.Lib.Devices.Fake.FakeDevice)
                        .OrderBy(d => d.DeviceType).ThenBy(d => d.DisplayName)];
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
                    var existingSite = EquipmentActions.GetSiteFromMount(pd.Mount);
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
                    eqState.ActiveAssignment = null;
                    appState.NeedsRedraw = true;
                    await updated.SaveAsync(external, cts.Token);

                    // Fetch weather forecast immediately when a weather device is assigned
                    if (device.DeviceType is DeviceType.Weather)
                    {
                        await FetchWeatherForecastAsync(cts.Token);
                        appState.NeedsRedraw = true;
                    }
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

                if (plannerState.Proposals is not { Count: > 0 })
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
