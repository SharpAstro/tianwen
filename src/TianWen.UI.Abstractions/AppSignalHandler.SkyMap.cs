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
    // AppSignalHandler.SkyMap.cs -- sky-map search, selection, and viewing-time helpers.
    // One partial per concern (see the class doc in AppSignalHandler.cs); handler bodies
    // moved verbatim from the single-file ctor in the Phase-5 by-area split.
    public partial class AppSignalHandler
    {
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

        /// <summary>Wires the sky-map F3 search and sky-map signals (view, selection, slew/sync, info panel).</summary>
        private void SubscribeSkyMap(SignalBus bus)
        {
            // Aliases over the injected fields keep the moved handler bodies verbatim
            // (the closures captured the ctor's parameters before the by-area split).
            var appState = _appState;
            var plannerState = _plannerState;
            var liveSessionState = _liveSessionState;
            var skyMapState = _skyMapState;
            var tracker = _tracker;
            var cts = _cts;
            var sp = _sp;
            var logger = _logger;

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
                if (!EnsureSessionIdle("Cannot slew manually while a session is running")) return;
                if (appState.ActiveProfile is not { Data: { } pdata } profile
                    || pdata.Mount is not { Scheme: not "none" } mountUri)
                {
                    Notify(NotificationSeverity.Warning, "No mount configured in the active profile");
                    return;
                }
                if (appState.DeviceHub is not { } hub
                    || !hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var mount)
                    || mount is null)
                {
                    Notify(NotificationSeverity.Warning, "Mount is not connected \u2014 connect it from the Equipment tab first");
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
                        Notify(severity, msg);
                        skyMapState.NeedsRedraw = true;

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
                            Notify(completionSeverity, completionMsg);
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
                        Notify(NotificationSeverity.Error, $"Slew failed: {ex.Message}");
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
                if (!EnsureSessionIdle("Cannot solve & sync while a session is running")) return;
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
                    Notify(NotificationSeverity.Warning, "No mount configured in the active profile");
                    return;
                }
                if (appState.DeviceHub is not { } hub
                    || !hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var mount)
                    || mount is null)
                {
                    Notify(NotificationSeverity.Warning, "Mount is not connected \u2014 connect it from the Equipment tab first");
                    return;
                }
                if (pdata.OTAs is not { Length: > 0 } otas || sig.OtaIndex >= otas.Length)
                {
                    Notify(NotificationSeverity.Warning, "No OTA configured in the active profile");
                    return;
                }
                var ota = otas[sig.OtaIndex];
                if (!TryGetConnected<ICameraDriver>(hub, ota.Camera, "Camera", out var camera)) return;

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
                        Notify(severity, outcome.StatusMessage);
                    }
                    catch (OperationCanceledException oce)
                    {
                        // Shutdown / explicit cancel - log so a mid-solve abort stays traceable.
                        logger.LogDebug(oce, "Solve & sync cancelled");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Solve & sync failed");
                        Notify(NotificationSeverity.Error, $"Solve & sync failed: {ex.Message}");
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
        }
    }
}
