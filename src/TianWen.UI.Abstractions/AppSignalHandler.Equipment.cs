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
    // AppSignalHandler.Equipment.cs -- equipment text inputs + device action signals.
    // One partial per concern (see the class doc in AppSignalHandler.cs); handler bodies
    // moved verbatim from the single-file ctor in the Phase-5 by-area split.
    public partial class AppSignalHandler
    {
        /// <summary>
        /// Runs a device connect/disconnect on a thread-pool thread so its <b>synchronous prefix
        /// never executes on the render thread</b>. <see cref="SignalBus.ProcessPending"/> invokes
        /// async signal handlers inline on the render thread, running each handler up to its first
        /// yielding <c>await</c> before the returned task is handed to the tracker. Several drivers
        /// block synchronously <i>before</i> that first await; most notably ASCOM COM drivers whose
        /// <c>Connected = true/false</c> setter busy-spins <c>Application.DoEvents()</c> for ~1&#160;s
        /// (Gemini FlatPanel Lite, iOptron, …). Left inline that freezes the GUI (Not Responding), and
        /// on a host with no message pump it can crash the process. Offloading the call moves that
        /// blocking prefix off the render loop. The fake devices connect instantly precisely because
        /// they have no such blocking prefix.
        /// </summary>
        private static Task RunDeviceOpOffRenderThreadAsync(Func<Task> deviceOp, CancellationToken ct)
            => Task.Run(deviceOp, ct);

        /// <summary>Wires the equipment tab's text-input commit callbacks (site, profile, OTA, device settings).</summary>
        private void SubscribeEquipmentTextInputs(SignalBus bus)
        {
            // Aliases over the injected fields keep the moved handler bodies verbatim
            // (the closures captured the ctor's parameters before the by-area split).
            var appState = _appState;
            var plannerState = _plannerState;
            var eqState = _eqState;
            var cts = _cts;
            var external = _external;
            var sp = _sp;
            var logger = _logger;

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
                    Notify(NotificationSeverity.Warning, "Invalid coordinates (lat: -90..90, lon: -180..180)");
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

            // Cancel ends the edit exactly the way commit does, by POSTING the signal. This path once
            // cleared the focus pointer by hand and so skipped SDL StopTextInput, leaving the IME /
            // on-screen keyboard up with nothing to type into; deactivating the three inputs first was
            // what made the posted signal a no-op (the bus is DEFERRED, and the old handler was gated on
            // the input still being active), which is how the direct assignment came to look necessary.
            //
            // Both halves of that are now structurally impossible: TextInputFocus owns the transition and
            // gates on its OWN record rather than the field's flag, and appState.ActiveTextInput is
            // read-only, so the shortcut this comment warns about will not compile. Only one field can be
            // focused at a time, so the signal covers whichever of the three it is.
            Action cancelSite = () =>
            {
                eqState.IsEditingSite = false;
                bus.Post(new DeactivateTextInputSignal());
            };

            eqState.LatitudeInput.OnCommit = _ => saveSite();
            eqState.LongitudeInput.OnCommit = _ => saveSite();
            eqState.ElevationInput.OnCommit = _ => saveSite();
            eqState.LatitudeInput.OnCancel = cancelSite;
            eqState.LongitudeInput.OnCancel = cancelSite;
            eqState.ElevationInput.OnCancel = cancelSite;

            // Mount safety limits (docs/plans/mount-safety-limits.md, P1): the same shape as the site above.
            // Parse in EquipmentActions, replace through UpdateProfileSignal (one save path), reflect into UI
            // state -- nothing else. The switch and the two responses ride along as pending state (the panel
            // cycles them the way a device setting is cycled), so Cancel drops them together with the numbers.
            Func<Task> saveLimits = () =>
            {
                if (appState.ActiveProfile is not { } limitsProfile)
                {
                    return Task.CompletedTask;
                }
                var limitsData = limitsProfile.Data ?? ProfileData.Empty;
                var current = (limitsData.MountLimits ?? new MountLimitConfiguration()) with
                {
                    Enabled = eqState.LimitEnabledPending,
                    MeridianResponse = eqState.LimitMeridianResponsePending,
                    HorizonResponse = eqState.LimitHorizonResponsePending,
                };
                if (!EquipmentActions.TryParseMountLimits(
                        eqState.LimitMeridianWarnInput.Text, eqState.LimitMeridianExtraInput.Text,
                        eqState.LimitHorizonActionInput.Text, eqState.LimitHorizonExtraInput.Text,
                        current, out var parsedLimits))
                {
                    Notify(NotificationSeverity.Warning, "Invalid mount limits (meridian 0..360 min, horizon 0..60 deg)");
                    return Task.CompletedTask;
                }
                eqState.IsEditingMountLimits = false;
                bus.Post(new DeactivateTextInputSignal());
                bus.Post(new UpdateProfileSignal(EquipmentActions.SetMountLimits(limitsData, parsedLimits)));
                return Task.CompletedTask;
            };
            Action cancelLimits = () =>
            {
                eqState.IsEditingMountLimits = false;
                bus.Post(new DeactivateTextInputSignal());
            };
            foreach (var limitInput in new[] { eqState.LimitMeridianWarnInput, eqState.LimitMeridianExtraInput, eqState.LimitHorizonActionInput, eqState.LimitHorizonExtraInput })
            {
                limitInput.OnCommit = _ => saveLimits();
                limitInput.OnCancel = cancelLimits;
            }

            // Guide scope focal length: commit on Enter
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

            // OTA name / focal length / aperture: commit on Enter saves the OTA edit
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

            // Device string settings (API keys, ports, etc.); commit on Enter saves the setting
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
        }

        /// <summary>Wires the DI-dependent equipment action signals (discover, connect/disconnect, cooler, assignments).</summary>
        private void SubscribeEquipmentActions(SignalBus bus)
        {
            // Aliases over the injected fields keep the moved handler bodies verbatim
            // (the closures captured the ctor's parameters before the by-area split).
            var appState = _appState;
            var plannerState = _plannerState;
            var sessionState = _sessionState;
            var eqState = _eqState;
            var cts = _cts;
            var external = _external;
            var sp = _sp;
            var logger = _logger;

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

                    // Profile-switcher dropdown / no-profile picker source (docs/plans/remote-profile.md):
                    // DiscoverAsync above already populated DeviceType.Profile, so this is free -- no
                    // separate DiscoverOnlyDeviceType round-trip.
                    eqState.AllProfiles = [.. dm.RegisteredDevices(DeviceType.Profile).OfType<Profile>()
                        .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)];

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
                    Notify(NotificationSeverity.Error, "Discovery failed");
                }
                finally
                {
                    eqState.IsDiscovering = false;
                    if (eqState.DiscoveredDevices.Count > 0)
                    {
                        Notify(NotificationSeverity.Info, $"Found {eqState.DiscoveredDevices.Count} devices");
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

            bus.Subscribe<EditMountLimitsSignal>(_ =>
            {
                eqState.IsEditingMountLimits = true;
                var l = appState.ActiveProfile?.Data?.MountLimits ?? new MountLimitConfiguration();
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                eqState.LimitMeridianWarnInput.Text = l.MeridianWarnMinutes.ToString("0.##", inv);
                eqState.LimitMeridianExtraInput.Text = l.MeridianActionExtraMinutes.ToString("0.##", inv);
                eqState.LimitHorizonActionInput.Text = l.HorizonActionDeg.ToString("0.##", inv);
                eqState.LimitHorizonExtraInput.Text = l.HorizonWarnExtraDeg.ToString("0.##", inv);
                eqState.LimitEnabledPending = l.Enabled;
                eqState.LimitMeridianResponsePending = l.MeridianResponse;
                eqState.LimitHorizonResponsePending = l.HorizonResponse;
                appState.NeedsRedraw = true;
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

            bus.Subscribe<SwitchProfileSignal>(sig =>
            {
                var target = eqState.AllProfiles.FirstOrDefault(p => p.ProfileId == sig.ProfileId);
                if (target is null || target.ProfileId == appState.ActiveProfile?.ProfileId) return;

                // Single-profile-context invariant: never swap out from under connected hardware or a
                // running session (ProfileSwitchGate's doc comment has the why). Gated HERE rather than
                // in the dropdown so no poster of this signal can bypass it.
                //
                // LOCAL profiles only -- this signal never carries a remote rig. Selecting a rig is a
                // view-context overlay (SelectRemoteRigSignal, deliberately ungated): the local session
                // keeps running underneath and its notifications keep bubbling up. Correspondingly the
                // gate reads the LOCAL context, not the on-screen one -- rebinding this node's profile
                // is refused while its own hardware is busy, whichever context you happen to be watching.
                var verdict = ProfileSwitchGate.Evaluate(appState.DeviceHub, LocalLiveSession.HasActiveRun);
                if (!verdict.Allowed)
                {
                    var reason = verdict.Describe();
                    eqState.ProfileSwitchBlocked = reason;
                    Notify(NotificationSeverity.Warning, $"Cannot switch to '{target.DisplayName}': {reason}");
                    appState.NeedsRedraw = true;
                    return;
                }

                appState.ActiveProfile = target;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<SelectRemoteRigSignal>(sig =>
            {
                // Deliberately ungated: this is a view-context overlay, so the local session keeps
                // running underneath with its hardware untouched. ProfileSwitchGate must never apply.
                RunTracked($"BindRig {sig.DisplayName}", "Could not connect to the rig", async ct =>
                {
                    var outcome = await RemoteRigActions.SelectAsync(
                        sig.DisplayName, _rigs, _contexts, appState, external, _timeProvider, logger, ct);

                    Notify(outcome.Severity, outcome.Message);
                    appState.NeedsRedraw = true;
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<SelectLocalContextSignal>(_ =>
            {
                _contexts.Activate(_contexts.Local);
                appState.NeedsRedraw = true;
            });

            // A display preference: no rig is touched, so there is nothing to persist, gate or notify about.
            bus.Subscribe<SetHomeBoardViewSignal>(sig =>
            {
                appState.HomeBoardView = sig.View;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<ForgetRemoteRigSignal>(sig =>
            {
                // Detach on the UI thread (so nothing renders a torn-down mirror) and dispose off it.
                var connection = _rigs.Remove(sig.BindingId);
                RemoteRigPersistence.Delete(sig.BindingId, external, logger);
                _contexts.Activate(_contexts.Local);
                appState.NeedsRedraw = true;

                if (connection is not null)
                {
                    RunTracked("ForgetRig", "Disconnecting the rig failed", async _ =>
                        await connection.DisposeAsync());
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
                        Notify(NotificationSeverity.Warning, $"Expected {target.ExpectedDeviceType}, got {device.DeviceType}");
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
                                Notify(NotificationSeverity.Info, $"Previous {target.ExpectedDeviceType} disconnected");
                                break;
                            case EquipmentActions.OrphanDisconnectOutcome.LeftConnected:
                                Notify(NotificationSeverity.Warning, $"Previous {target.ExpectedDeviceType} left connected ({safety}). Click Off on its row to warm up.");
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
                    Notify(NotificationSeverity.Warning, "Select an OTA's Cover slot first, then add the Manual Light Panel");
                    return;
                }

                var manual = new TianWen.Lib.Devices.ManualCoverDevice();
                var data = profile.Data ?? ProfileData.Empty;
                var newData = EquipmentActions.ApplyAssignment(data, target, DeviceType.CoverCalibrator, manual.DeviceUri);
                var updated = profile.WithData(newData);
                appState.ActiveProfile = updated;
                appState.NeedsRedraw = true;
                await updated.SaveAsync(external, cts.Token);
                Notify(NotificationSeverity.Info, "Manual Light Panel assigned - switch it on before capturing flats");
            });

            bus.Subscribe<ConnectAllDevicesSignal>(_ =>
            {
                if (appState.ActiveProfile?.Data is not { } pdata) return;
                if (appState.DeviceHub is not { } hub) return;

                // Fan out to per-device ConnectDeviceSignal so each connect goes through
                // the same in-flight gate, notification, and safety paths as a manual
                // click. Skip URIs that the hub already considers connected; connecting
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
                    Notify(NotificationSeverity.Warning, "Device hub unavailable");
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
                        Notify(NotificationSeverity.Warning, "Cannot resolve device URI for connect");
                        return;
                    }

                    await RunDeviceOpOffRenderThreadAsync(() => hub.ConnectAsync(device, cts.Token).AsTask(), cts.Token);
                    Notify(NotificationSeverity.Info, $"Connected: {device.DisplayName}");

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
                    Notify(NotificationSeverity.Error, $"Connect failed: {ex.Message}");
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

                // Ownership first, and BEFORE the hardware-safety check: "a run is using this" is a
                // different refusal from "the cooler is on", and offering the [Warm & Off] [Force Off]
                // strip for a device the session is driving would invite the user to break their own run.
                // GetDisconnectSafetyAsync cannot cover this -- it returns Safe for anything that is not
                // a camera, so the mount was previously disconnectable mid-session with no warning at all.
                var ownership = DeviceOwnershipGate.Evaluate(hub, sig.DeviceUri, DeviceAction.Disconnect);
                if (!ownership.Allowed)
                {
                    Notify(NotificationSeverity.Warning, ownership.Describe());
                    return;
                }

                // Pre-flight safety check. If the device is a cooled/busy camera, don't
                // disconnect: set the per-row confirmation state so the UI shows the
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
                    await RunDeviceOpOffRenderThreadAsync(() => hub.DisconnectAsync(sig.DeviceUri, cancellationToken: cts.Token).AsTask(), cts.Token);
                    Notify(NotificationSeverity.Info, "Device disconnected");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Disconnect failed for {Uri}", sig.DeviceUri);
                    Notify(NotificationSeverity.Error, $"Disconnect failed: {ex.Message}");
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
                //
                // "Force" here means "skip the warm-up", which is what the user actually confirmed. It
                // deliberately does NOT mean "take the device off a running session": consenting to a cold
                // disconnect is not consenting to kill the night. Ownership is still checked, and the only
                // way past it stays the explicit one -- stop the run.
                var forceOwnership = DeviceOwnershipGate.Evaluate(hub, sig.DeviceUri, DeviceAction.Disconnect);
                if (!forceOwnership.Allowed)
                {
                    Notify(NotificationSeverity.Warning, forceOwnership.Describe());
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
                    await RunDeviceOpOffRenderThreadAsync(() => hub.DisconnectAsync(sig.DeviceUri, cancellationToken: cts.Token).AsTask(), cts.Token);
                    Notify(NotificationSeverity.Info, "Device force-disconnected (no warm-up)");
                    logger.LogWarning("Force-disconnect of {Uri} (bypassed safety check)", sig.DeviceUri);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Force-disconnect failed for {Uri}", sig.DeviceUri);
                    Notify(NotificationSeverity.Error, $"Force-disconnect failed: {ex.Message}");
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
                    await RunDeviceOpOffRenderThreadAsync(() => EquipmentActions.WarmAndDisconnectAsync(hub, sig.DeviceUri, _logger, force: false, cts.Token).AsTask(), cts.Token);
                    Notify(NotificationSeverity.Info, "Camera warmed and disconnected");
                }
                catch (OperationCanceledException)
                {
                    Notify(NotificationSeverity.Warning, "Warm-up cancelled");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Warm-and-disconnect failed for {Uri}", sig.DeviceUri);
                    Notify(NotificationSeverity.Error, $"Warm-up failed: {ex.Message}");
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
                if (!TryGetConnected<ICameraDriver>(hub, sig.DeviceUri, "Camera", out var camera)) return;
                try
                {
                    await EquipmentActions.SetCoolerSetpointAsync(camera, sig.SetpointC, cts.Token);
                    Notify(NotificationSeverity.Info, $"Cooling to {sig.SetpointC:F1}\u00b0C");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SetCoolerSetpoint failed for {Uri}", sig.DeviceUri);
                    Notify(NotificationSeverity.Error, $"Cooler setpoint failed: {ex.Message}");
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
                    Notify(NotificationSeverity.Info, "Camera warmed; cooler off");
                }
                catch (OperationCanceledException)
                {
                    Notify(NotificationSeverity.Warning, "Warm-up cancelled");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Warm-and-cooler-off failed for {Uri}", sig.DeviceUri);
                    Notify(NotificationSeverity.Error, $"Warm-up failed: {ex.Message}");
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
                    Notify(NotificationSeverity.Info, "Cooler off");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SetCoolerOff failed for {Uri}", sig.DeviceUri);
                    Notify(NotificationSeverity.Error, $"Cooler off failed: {ex.Message}");
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
                plannerState.MarkSaved();
                await PlannerPersistence.SaveAsync(plannerState, profile, external, _timeProvider, ActiveRemoteBindingId, cts.Token);
            });

            bus.Subscribe<SaveSessionConfigSignal>(async _ =>
            {
                if (!sessionState.IsDirty || appState.ActiveProfile is not { } profile)
                {
                    return;
                }
                sessionState.MarkSaved();
                await SessionPersistence.SaveAsync(sessionState, profile, external, cts.Token);
            });

            // Wire signal bus into state objects for auto-posting on dirty
            plannerState.Bus = bus;
            sessionState.Bus = bus;

            // Refresh per-OTA camera capabilities when a driver connects or disconnects via the hub; 
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
        }
    }
}
