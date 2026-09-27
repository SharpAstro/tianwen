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
using TianWen.Hosting.Dto;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions
{
    // AppSignalHandler.Equipment.cs -- equipment text inputs + device action signals.
    // One partial per concern (see the class doc in AppSignalHandler.cs); handler bodies
    // moved verbatim from the single-file ctor in the Phase-5 by-area split.
    public partial class AppSignalHandler
    {
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
                if (text.Length == 0 || LocalNodeOrSay() is not { } node)
                {
                    return;
                }

                eqState.IsCreatingProfile = false;
                eqState.ProfileNameInput.Clear();
                bus.Post(new DeactivateTextInputSignal());

                var created = await node.Client.CreateProfileAsync(text, cts.Token);
                if (created is not { IsSuccess: true, Value: { } profile })
                {
                    Notify(NotificationSeverity.Error, $"Could not create the profile: {created.Error}");
                    return;
                }
                // The node applies the switch rule itself (a device connected, a run going on) and says why it refuses.
                var set = await node.Client.SetActiveProfileAsync(profile.ProfileId, cts.Token);
                if (!set.IsSuccess)
                {
                    Notify(NotificationSeverity.Warning, $"Created '{profile.Name}', but it could not be made the active profile: {set.Error}");
                }
                await RefreshProfileListAsync(node, cts.Token);
                await RefreshLocalProfileNowAsync(node, cts.Token);
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
                eqState.IsEditingSite = false;
                bus.Post(new DeactivateTextInputSignal());
                // This computer's site: the planner's only while its own view is on show; a rig's view plans at the rig's.
                if (_contexts.Active.IsLocal)
                {
                    plannerState.SiteLatitude = sLat;
                    plannerState.SiteLongitude = sLon;
                }
                plannerState.NeedsRecompute = true;
                appState.NeedsRedraw = true;

                // If the catalog was blocked on a missing site, load it now.
                if (plannerState.ObjectDb is null
                    && TransformFactory.FromProfile(updatedSite, _timeProvider, out _) is { } siteTransform)
                {
                    StartPlanner(siteTransform, "Load catalog after site edit");
                }

                // The node gives a connected mount the new site when the profile wins the tie (AfterProfileEditAsync).
                await WriteLocalProfileAsync(siteProfile, newSiteData, name: null, cts.Token);
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
                    await WriteLocalProfileAsync(profile, pd with { GuiderFocalLength = guiderFl }, name: null, cts.Token);
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
                if (LocalNodeOrSay() is not { } node)
                {
                    return;
                }

                // One rule on the node for the GUI and every other client (DeviceSettingHelper.Commit): a masked setting
                // goes into the credential store, never onto the URI; any other onto the device's URI in the profile.
                var committed = await node.Client.SetDeviceSettingAsync(editUri, key, value, appState.ActiveProfile?.ProfileId, cts.Token);
                if (committed is not { IsSuccess: true, Value: { } setting })
                {
                    Notify(NotificationSeverity.Error, $"Could not save {key}: {committed.Error}");
                    return;
                }
                if (setting.Secret)
                {
                    // The profile is unchanged, so no weather URI change refetches: the key may have become available now.
                    if (EquipmentActions.TryDeviceFromUri(editUri)?.DeviceType is DeviceType.Weather)
                    {
                        await FetchWeatherForecastAsync(cts.Token);
                    }
                    appState.NeedsRedraw = true;
                    return;
                }

                if (Uri.TryCreate(setting.DeviceUri, UriKind.Absolute, out var newUri))
                {
                    eqState.EditingDeviceUri = newUri;
                    await RefreshLocalProfileNowAsync(node, cts.Token);
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
                if (eqState.IsDiscovering || LocalNodeOrSay() is not { } node) return;

                eqState.IsDiscovering = true;
                appState.StatusMessage = sig.IncludeFake ? "Discovering devices (+ fake)..." : "Discovering devices...";
                appState.NeedsRedraw = true;
                try
                {
                    // The node discovers on its own token, then reconciles every profile with what it found (a COM port
                    // moved, a new DHCP address, a site still on the mount's URI), writing each that changed and pushing it.
                    if (await RunNodeJobAsync(node, node.Client.StartDiscoveryAsync(cts.Token), "Discovery", cts.Token) is null)
                    {
                        return;
                    }
                    if (await node.RefreshListingAsync(cts.Token) is { } failure)
                    {
                        Notify(NotificationSeverity.Error, $"Could not list the devices: {failure}");
                        return;
                    }
                    eqState.DiscoveredDevices = EquipmentActions.ForTheDeviceList(node.Listed, sig.IncludeFake);
                    await RefreshProfileListAsync(node, cts.Token);
                    // What each camera is (its named gains, whether it cools) comes with the listing.
                    sessionState.InitializeFromProfile(appState.ActiveProfile, appState.CameraCapabilitiesOf);
                    sessionState.NeedsRedraw = true;
                    await RefreshLocalProfileNowAsync(node, cts.Token);
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
                await WriteLocalProfileAsync(p, EquipmentActions.AddOTA(data, newOta), name: null, cts.Token);
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

            bus.Subscribe<SwitchProfileSignal>(async sig =>
            {
                var target = eqState.AllProfiles.FirstOrDefault(p => p.ProfileId == sig.ProfileId);
                if (target is null || target.ProfileId == appState.ActiveProfile?.ProfileId || LocalNodeOrSay() is not { } node) return;

                // Single-profile-context invariant: never swap out from under connected hardware or a running run
                // (ProfileSwitchGate's doc comment has the why), which the NODE applies, over its own hub and runs, and
                // answers with the gate's own words. LOCAL profiles only: selecting a rig is a view-context overlay
                // (SelectRemoteRigSignal, deliberately ungated).
                var set = await node.Client.SetActiveProfileAsync(target.ProfileId, cts.Token);
                if (!set.IsSuccess)
                {
                    eqState.ProfileSwitchBlocked = set.Error;
                    Notify(NotificationSeverity.Warning, $"Cannot switch to '{target.DisplayName}': {set.Error}");
                    appState.NeedsRedraw = true;
                    return;
                }
                await RefreshLocalProfileNowAsync(node, cts.Token);
            });

            bus.Subscribe<SelectRemoteRigSignal>(sig =>
            {
                // Deliberately ungated: this is a view-context overlay, so the local session keeps
                // running underneath with its hardware untouched. ProfileSwitchGate must never apply.
                // The tab switches here, on the UI thread, not after the bind: the tab renders the rig
                // as soon as its mirror has anything, and a continuation must not write UI state.
                if (sig.OpenTab is { } openTab)
                {
                    appState.ActiveTab = openTab;
                }
                RunTracked($"BindRig {sig.DisplayName}", "Could not connect to the rig", async ct =>
                {
                    var outcome = await RemoteRigActions.SelectAsync(
                        sig.DisplayName, _rigs, _contexts, appState, external, _timeProvider, logger, ct);

                    Notify(outcome.Severity, outcome.Message);
                    appState.NeedsRedraw = true;
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<SelectLocalContextSignal>(sig =>
            {
                _contexts.Activate(_contexts.Local);
                if (sig.OpenTab is { } openTab)
                {
                    appState.ActiveTab = openTab;
                }
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

                    // Keep the slot active so the user can swap the assigned device by
                    // clicking another row immediately, without re-clicking the slot.
                    // Click the slot itself again to deactivate.
                    await WriteLocalProfileAsync(profile, newData, name: null, cts.Token);

                    // Fetch weather forecast immediately when a weather device is assigned
                    if (device.DeviceType is DeviceType.Weather)
                    {
                        await FetchWeatherForecastAsync(cts.Token);
                        appState.NeedsRedraw = true;
                    }

                    // The device the slot had: disconnected when still connected and safe, else left and said so.
                    if (appState.LocalNode is { } node)
                    {
                        await DisconnectOrphanAsync(node, prevSlotUri, device.DeviceUri, target.ExpectedDeviceType, cts.Token);
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
                await WriteLocalProfileAsync(profile, newData, name: null, cts.Token);
                Notify(NotificationSeverity.Info, "Manual Light Panel assigned - switch it on before capturing flats");
            });

            bus.Subscribe<ConnectAllDevicesSignal>(_ =>
            {
                if (appState.ActiveProfile?.Data is not { } pdata || LocalNodeOrSay() is not { } node) return;

                // Fan out to per-device ConnectDeviceSignal so each connect goes through the same in-flight gate,
                // notification and safety paths as a manual click. Skips what the node already holds connected.
                foreach (var uri in pdata.AssignedDeviceUris)
                {
                    if (node.IsConnected(uri)) continue;
                    bus.Post(new ConnectDeviceSignal(uri));
                }
            });

            bus.Subscribe<ConnectDeviceSignal>(async sig =>
            {
                if (LocalNodeOrSay() is not { } node)
                {
                    return;
                }
                if (!eqState.PendingTransitions.TryAdd(sig.DeviceUri, 0))
                {
                    return; // transition already in flight
                }
                appState.NeedsRedraw = true;

                try
                {
                    // The node connects (on its own token, so a slow serial handshake never blocks this window) and writes
                    // into the active profile what a connect settles: a mount's site reconciled with the profile's, a
                    // camera's sensor recorded for the planner's framing (DeviceOperations.WriteConnectIntoProfileAsync).
                    var name = EquipmentActions.DeviceLabel(sig.DeviceUri, node);
                    if (await RunNodeJobAsync(node, node.Client.ConnectDeviceAsync(sig.DeviceUri, cts.Token), $"Connecting {name}", cts.Token) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? $"Connected: {name}");
                        await RefreshLocalProfileNowAsync(node, cts.Token);
                    }
                }
                finally
                {
                    eqState.PendingTransitions.TryRemove(sig.DeviceUri, out _);
                    appState.NeedsRedraw = true;
                }
            });

            bus.Subscribe<DisconnectDeviceSignal>(async sig =>
            {
                if (LocalNodeOrSay() is not { } node)
                {
                    return;
                }

                // What the node says of it first: a run holding the device is a different refusal from a cold camera, and
                // offering the [Warm & Off] [Force Off] strip for a device the session is driving would invite the user to
                // break their own run. The node refuses the disconnect itself either way.
                var check = await node.Client.GetDisconnectSafetyAsync(sig.DeviceUri, cts.Token);
                if (check is not { IsSuccess: true, Value: { } safety })
                {
                    Notify(NotificationSeverity.Warning, check.Error ?? "The device cannot be disconnected now");
                    return;
                }
                if (safety.LeaseOwner is { } owner)
                {
                    Notify(NotificationSeverity.Warning, new DeviceOwnershipVerdict(new DeviceLease(sig.DeviceUri, owner), DeviceAction.Disconnect).Describe());
                    return;
                }
                // A cooled or busy camera is not disconnected: the row shows the [Warm & Off] [Force Off] [Cancel] strip.
                if (safety.Safety != DisconnectSafety.Safe)
                {
                    eqState.PendingDisconnectConfirm = sig.DeviceUri;
                    eqState.PendingDisconnectSafety = safety.Safety;
                    eqState.PendingForceConfirm = null;
                    appState.NeedsRedraw = true;
                    return;
                }

                await RunDisconnectAsync(node, sig.DeviceUri, node.Client.DisconnectDeviceAsync(sig.DeviceUri, skipWarmUp: false, cts.Token),
                    "Device disconnected");
            });

            bus.Subscribe<ForceDisconnectDeviceSignal>(async sig =>
            {
                if (LocalNodeOrSay() is not { } node)
                {
                    return;
                }

                // Past the safety check: the caller already passed two-stage confirmation. "Force" means "skip the
                // warm-up", which is what the user confirmed; it does NOT take a device off a running run, which the node
                // still refuses in its own words: consenting to a cold disconnect is not consenting to kill the night.
                eqState.PendingDisconnectConfirm = null;
                eqState.PendingForceConfirm = null;
                await RunDisconnectAsync(node, sig.DeviceUri, node.Client.DisconnectDeviceAsync(sig.DeviceUri, skipWarmUp: true, cts.Token),
                    "Device force-disconnected (no warm-up)");
            });

            bus.Subscribe<WarmAndDisconnectDeviceSignal>(async sig =>
            {
                if (LocalNodeOrSay() is not { } node)
                {
                    return;
                }

                eqState.PendingDisconnectConfirm = null;
                eqState.PendingForceConfirm = null;
                // The ramp runs in the node, so it finishes whatever becomes of this window.
                await RunDisconnectAsync(node, sig.DeviceUri, node.Client.WarmAndDisconnectDeviceAsync(sig.DeviceUri, cts.Token),
                    "Camera warmed and disconnected");
            });

            bus.Subscribe<SetCoolerSetpointSignal>(async sig =>
            {
                if (LocalNodeOrSay() is not { } node) return;

                // Cooled through the session's own ramp (CameraCoolingRamp), on the node: the one answer to how fast a
                // sensor may be cooled, where the Equipment tab used to set the setpoint at once.
                var starting = node.Client.CoolCameraAsync(sig.DeviceUri, sig.SetpointC, rampMinutes: null, cts.Token);
                Notify(NotificationSeverity.Info, $"Cooling to {sig.SetpointC:F1}\u00b0C");
                if (await RunNodeJobAsync(node, starting, "Cooling", cts.Token) is { } cooled)
                {
                    Notify(NotificationSeverity.Info, cooled.Step ?? $"Cooled to {sig.SetpointC:F1}\u00b0C");
                }
            });

            bus.Subscribe<WarmAndCoolerOffSignal>(async sig =>
            {
                if (LocalNodeOrSay() is not { } node) return;
                eqState.PendingCoolerOffConfirm = null;
                eqState.PendingCoolerOffForceConfirm = null;
                appState.NeedsRedraw = true;

                if (await RunNodeJobAsync(node, node.Client.WarmCameraAsync(sig.DeviceUri, cts.Token), "Warm-up", cts.Token) is not null)
                {
                    Notify(NotificationSeverity.Info, "Camera warmed; cooler off");
                }
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<SetCoolerOffSignal>(async sig =>
            {
                if (LocalNodeOrSay() is not { } node) return;
                var off = await node.Client.CameraCoolerOffAsync(sig.DeviceUri, cts.Token);
                if (off.IsSuccess)
                {
                    Notify(NotificationSeverity.Info, "Cooler off");
                    await node.RefreshDevicesNowAsync(cts.Token);
                }
                else
                {
                    Notify(NotificationSeverity.Error, $"Cooler off failed: {off.Error}");
                }
            });

            bus.Subscribe<UpdateProfileSignal>(async sig =>
            {
                if (appState.ActiveProfile is { } profile)
                {
                    var previousWeather = profile.Data?.Weather;
                    await WriteLocalProfileAsync(profile, sig.Data, name: null, cts.Token);

                    // Camera / focuser / filter-wheel assignments may have changed; rebuild the per-OTA settings so gain
                    // modes (DSLR ISO vs ZWO numeric) and cooling come from the new camera, as the node listed it.
                    sessionState.InitializeFromProfile(appState.ActiveProfile, appState.CameraCapabilitiesOf);
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

        }

        /// <summary>
        /// Runs a disconnect job on <paramref name="deviceUri"/> with its row marked as changing, noting
        /// <paramref name="done"/> once the node has done it.
        /// </summary>
        private async Task RunDisconnectAsync(LocalNodeConnection node, Uri deviceUri, Task<NodeResult<JobDto>> starting, string done)
        {
            if (!_eqState.PendingTransitions.TryAdd(deviceUri, 0))
            {
                return;
            }
            _appState.NeedsRedraw = true;
            try
            {
                if (await RunNodeJobAsync(node, starting, $"Disconnecting {EquipmentActions.DeviceLabel(deviceUri, node)}", _cts.Token) is not null)
                {
                    Notify(NotificationSeverity.Info, done);
                }
            }
            finally
            {
                _eqState.PendingTransitions.TryRemove(deviceUri, out _);
                _appState.NeedsRedraw = true;
            }
        }
    }
}
