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
            var liveSessionState = LocalLiveSession;
            var skyMapState = _skyMapState;
            var tracker = _tracker;
            var cts = _cts;
            var sp = _sp;
            var logger = _logger;

            // ---------------------------------------------------------------
            // Wire sky-map F3 search
            // ---------------------------------------------------------------
            var skySearch = skyMapState.Search;

            // The search-box interaction (input wiring + Up/Down/Enter/Escape protocol + the result list) is
            // the shared DIR.Lib SearchInteraction; construct it with the desktop-flavoured dispatch. Commit
            // + close go through the signal bus so the per-invocation DI context (catalog / comets / viewing
            // time / site) resolves in the handlers below. Replaces the three SearchInput.On* assignments.
            skySearch.Interaction = new SkyMapSearchInteraction(
                skySearch,
                sp.GetRequiredService<ICelestialObjectDB>(),
                commit: () => bus.Post(new SkyMapSearchCommitSignal()),
                close: () => bus.Post(new CloseSkyMapSearchSignal()),
                requestRedraw: () =>
                {
                    skyMapState.NeedsRedraw = true;
                    appState.NeedsRedraw = true;
                });

            bus.Subscribe<OpenSkyMapSearchSignal>(_ =>
            {
                var db = sp.GetRequiredService<ICelestialObjectDB>();
                SkyMapSearchActions.OpenSearch(skySearch, db, appState.TextInputFocus, plannerState.Comets);
                skyMapState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<CloseSkyMapSearchSignal>(_ =>
            {
                // No DeactivateTextInputSignal beside it any more: CloseSearch releases the
                // keyboard through the owner, and posting a blur as well was the second answer to
                // "which field is live" -- the one TextInputFocus exists to remove.
                SkyMapSearchActions.CloseSearch(skySearch, appState.TextInputFocus);
                skyMapState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });

            bus.Subscribe<SkyMapSearchCommitSignal>(_ =>
            {
                // The interaction holds the highlighted row (keyboard Enter or a CommitAt mouse click set
                // SelectedIndex before posting this); resolve it and hand it to the commit helper.
                if (skySearch.Interaction is not { } interaction
                    || interaction.SelectedIndex < 0
                    || interaction.SelectedIndex >= interaction.Results.Length)
                {
                    return;
                }
                var db = sp.GetRequiredService<ICelestialObjectDB>();
                // Include the sky-map scrub offset so a planet commit resolves the SAME live position
                // the map is showing (and reads the render's planet cache without thrashing it).
                var viewingUtc = SkyMapViewingUtc();
                var site = SiteContext.Create(plannerState.SiteLatitude, plannerState.SiteLongitude, viewingUtc);
                SkyMapSearchActions.CommitResult(
                    skySearch, skyMapState, db,
                    interaction.Results[interaction.SelectedIndex],
                    appState.TextInputFocus,
                    plannerState.SiteLatitude, plannerState.SiteLongitude,
                    viewingUtc, site, plannerState.Comets);
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
                // Every object panel's Goto lands here, and it slews the mount of the rig on show: this computer's, or a
                // rig's this client controls.
                if (CommandTargetOrSay("A goto") is not { Node: var node } target) return;
                if (target.Profile is not { Data: { } pdata }
                    || pdata.Mount is not { Scheme: not "none" } mountUri)
                {
                    Notify(NotificationSeverity.Warning, "No mount configured in the active profile");
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

                // The node slews (a mount a run holds is refused, in the run's name; one not connected, saying so) and its
                // job ends when the mount lands or gives up, which is the note the status bar ends on.
                var request = new MountGotoRequestDto
                {
                    DeviceUri = mountUri.ToString(),
                    RaJ2000 = sig.RA,
                    DecJ2000 = sig.Dec,
                    Name = sig.Name,
                    Index = sig.Index,
                    MinAltitudeDegrees = System.Math.Max((int)plannerState.MinHeightAboveHorizon, 1),
                };
                var capturedSig = sig;
                RunTracked($"Goto {sig.Name}", "Slew failed", async ct =>
                {
                    var starting = node.Client.GotoAsync(request, ct);
                    // Surface the slew destination on the sky map (marker + ETA, the latter estimated in the render path
                    // from the reticle). The signal carries J2000 catalog coords, matching the overlay frame.
                    skyMapState.ActiveSlewTarget = new SlewTargetInfo(capturedSig.Name, capturedSig.RA, capturedSig.Dec);
                    skyMapState.SlewEtaSeconds = double.NaN;
                    Notify(NotificationSeverity.Info, $"Slewing to {capturedSig.Name}");
                    if (await RunNodeJobAsync(node, starting, $"Slew to {capturedSig.Name}", ct) is { } landed)
                    {
                        Notify(NotificationSeverity.Info, landed.Step ?? $"Reached {capturedSig.Name}");
                    }
                }, onFinally: () =>
                {
                    // Slew finished (reached / timed out / cancelled / failed): drop the
                    // destination marker so it doesn't linger after the mount settles.
                    skyMapState.ActiveSlewTarget = null;
                    skyMapState.SlewEtaSeconds = double.NaN;
                    skyMapState.NeedsRedraw = true;
                    appState.NeedsRedraw = true;
                });
            });

            bus.Subscribe<SkyMapClickSelectSignal>(sig =>
            {
                var db = sp.GetRequiredService<ICelestialObjectDB>();
                // Match the render's viewing instant: base date/now PLUS the sky-map scrub offset
                // (State.TimeOffset), via SkyMapViewingUtc. Planet positions are ephemeris-computed
                // and move with time, so hit-testing them (and the panel's alt/az) must use the same
                // instant the renderer drew -- otherwise a scrubbed planet dot is unclickable. The
                // projection + pinned-set boilerplate lives in the shared SelectAtScreenPoint so the
                // web Planner goes through the identical path.
                SkyMapSearchActions.SelectAtScreenPoint(
                    skyMapState, db,
                    plannerState.SiteLatitude, plannerState.SiteLongitude,
                    SkyMapViewingUtc(),
                    sig.ScreenX, sig.ScreenY, sig.Modifiers,
                    plannerState.Proposals, plannerState.Comets);

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
                // Its reticle is the Active mount's, and so are the solve and the sync: this computer's, or a rig's this client
                // controls.
                if (CommandTargetOrSay("Solve and sync") is not { } target) return;
                var (node, view) = (target.Node, target.View);
                // Re-entrancy guard: ignore a second click while a solve is already in
                // flight (the button also shows "Solving ..." and drops its handler, but
                // a queued signal could still arrive). UI-thread-only read here.
                if (skyMapState.SolveSyncInProgress)
                {
                    return;
                }
                if (target.Profile is not { Data: { } pdata }
                    || pdata.Mount is not { Scheme: not "none" })
                {
                    Notify(NotificationSeverity.Warning, "No mount configured in the active profile");
                    return;
                }
                if (pdata.OTAs is not { Length: > 0 } otas || sig.OtaIndex >= otas.Length)
                {
                    Notify(NotificationSeverity.Warning, "No OTA configured in the active profile");
                    return;
                }

                // Mirror the preview-capture progress UI while the solve frame exposes.
                if (sig.OtaIndex < view.PreviewCapturing.Length)
                {
                    view.PreviewCapturing[sig.OtaIndex] = true;
                    view.PreviewCaptureStart[sig.OtaIndex] = _timeProvider.GetUtcNow();
                    view.PreviewExposureDuration[sig.OtaIndex] = TimeSpan.FromSeconds(sig.ExposureSeconds);
                }
                appState.StatusMessage = "Solve & sync\u2026";
                skyMapState.SolveSyncInProgress = true; // drives the "Solving ..." button label
                appState.NeedsRedraw = true;

                // The node exposes, solves and syncs (solve and sync rewrites the mount's pointing, which a run holding
                // the mount refuses); its frame is the OTA's there, shown by the view's mirror, its solution the OTA's.
                var request = new PreviewExposureRequestDto { ExposureSeconds = sig.ExposureSeconds, Gain = sig.Gain is { } g ? (short)g : null, Binning = sig.Binning };
                var capturedSig = sig;
                RunTracked("SolveAndSync", "Solve & sync failed", async ct =>
                {
                    if (await RunNodeJobAsync(node, node.Client.StartSolveSyncAsync(capturedSig.OtaIndex, request, ct), "Solve & sync", ct) is { } synced)
                    {
                        Notify(NotificationSeverity.Info, synced.Step ?? "Synced");
                    }
                    await ShowSolutionAsync(node, view, capturedSig.OtaIndex, ct);
                }, onFinally: () =>
                {
                    if (capturedSig.OtaIndex < view.PreviewCapturing.Length)
                    {
                        view.PreviewCapturing[capturedSig.OtaIndex] = false;
                    }
                    skyMapState.SolveSyncInProgress = false; // re-enable the Solve & Sync button
                    skyMapState.NeedsRedraw = true;
                    view.NeedsRedraw = true;
                    appState.NeedsRedraw = true;
                });
            });
        }
    }
}
