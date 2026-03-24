using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Host-agnostic signal handler. Wires all <see cref="SignalBus"/> subscriptions
    /// and <see cref="TextInputState"/> callbacks for planner search, equipment editing,
    /// and profile management. Shared between GPU and terminal hosts.
    /// </summary>
    public class AppSignalHandler
    {
        /// <summary>Set by the host after catalog load to enable autocomplete.</summary>
        public Action<string[]> SetAutoCompleteCache { get; }

        /// <summary>
        /// Called when a search commit or suggestion resolves a target index
        /// that should be scrolled into view. The host wires this to its
        /// list widget's scroll mechanism.
        /// </summary>
        public Action<int>? OnPlannerEnsureVisible { get; set; }

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
            var logger = external.AppLogger;

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
                    var transform = TransformFactory.FromProfile(appState.ActiveProfile, external.TimeProvider, out _);
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
                    double.TryParse(eqState.LongitudeInput.Text, System.Globalization.CultureInfo.InvariantCulture, out var sLon))
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
                    appState.StatusMessage = "Invalid latitude or longitude";
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
                        AssignTarget.OTALevel otaTarget => EquipmentActions.AssignDeviceToOTA(data, otaTarget.OtaIndex,
                            device.DeviceType, device.DeviceUri),
                        _ => data
                    };

                    var updated = profile.WithData(newData);
                    appState.ActiveProfile = updated;
                    eqState.ActiveAssignment = null;
                    appState.NeedsRedraw = true;
                    await updated.SaveAsync(external, cts.Token);
                }
            });

            bus.Subscribe<UpdateProfileSignal>(async sig =>
            {
                if (appState.ActiveProfile is { } profile)
                {
                    var updated = profile.WithData(sig.Data);
                    appState.ActiveProfile = updated;
                    appState.NeedsRedraw = true;
                    await updated.SaveAsync(external, cts.Token);
                }
            });

            bus.Subscribe<SavePlannerSessionSignal>(async _ =>
            {
                if (!plannerState.IsDirty || appState.ActiveProfile is not { } profile)
                {
                    return;
                }
                plannerState.IsDirty = false;
                await PlannerPersistence.SaveAsync(plannerState, profile, external, cts.Token);
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
                    var transform = TransformFactory.FromProfile(profile, external.TimeProvider, out var transformError);
                    if (transform is null)
                    {
                        appState.StatusMessage = "Cannot determine site location from profile";
                        return;
                    }

                    var availableFilters = profileData.OTAs.Length > 0
                        ? EquipmentActions.GetFilterConfig(profileData, 0)
                        : null;
                    var opticalDesign = profileData.OTAs.Length > 0
                        ? profileData.OTAs[0].OpticalDesign
                        : OpticalDesign.Unknown;

                    PlannerActions.BuildSchedule(plannerState, sessionState, transform,
                        defaultGain: null, defaultOffset: null,
                        defaultSubExposure: sessionState.Configuration.DefaultSubExposure ?? TimeSpan.FromSeconds(120),
                        defaultObservationTime: TimeSpan.FromMinutes(60),
                        availableFilters: availableFilters is { Count: > 0 } ? availableFilters : null,
                        opticalDesign: opticalDesign);

                    if (sessionState.Schedule is not { Count: > 0 } schedule)
                    {
                        appState.StatusMessage = "Failed to build schedule from proposals";
                        return;
                    }

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

                    var session = factory.Create(
                        profile.ProfileId,
                        config,
                        observations.AsSpan());

                    liveSessionState.ActiveSession = session;
                    liveSessionState.IsRunning = true;
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
                    var transform = TransformFactory.FromProfile(appState.ActiveProfile, external.TimeProvider, out _);
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
