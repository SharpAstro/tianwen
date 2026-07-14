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
    // AppSignalHandler.Polar.cs -- polar-alignment signals.
    // One partial per concern (see the class doc in AppSignalHandler.cs); handler bodies
    // moved verbatim from the single-file ctor in the Phase-5 by-area split.
    public partial class AppSignalHandler
    {
        /// <summary>Wires the polar-alignment signals (start/cancel/done + refine-loop state).</summary>
        private void SubscribePolarAlignment(SignalBus bus)
        {
            // Aliases over the injected fields keep the moved handler bodies verbatim
            // (the closures captured the ctor's parameters before the by-area split).
            var appState = _appState;
            var liveSessionState = _liveSessionState;
            var tracker = _tracker;
            var cts = _cts;
            var external = _external;
            var sp = _sp;
            var logger = _logger;

            // ---------------------------------------------------------------
            // Polar alignment signals
            // ---------------------------------------------------------------

            bus.Subscribe<StartPolarAlignmentSignal>(sig =>
            {
                if (!EnsureSessionIdle("Session is running \u2014 polar alignment unavailable")) return;
                if (liveSessionState.PolarAlignmentCts is not null)
                {
                    Notify(NotificationSeverity.Warning, "Polar alignment already running");
                    return;
                }
                if (appState.ActiveProfile?.Data is not { } profileData || profileData.OTAs.Length == 0)
                {
                    Notify(NotificationSeverity.Warning, "No profile / OTA configured");
                    return;
                }
                if (appState.DeviceHub is not { } hub)
                {
                    Notify(NotificationSeverity.Warning, "Device hub not available");
                    return;
                }
                if (!TryGetConnected<IMountDriver>(hub, profileData.Mount, "Mount", out var mount, "Mount not connected \u2014 connect a mount first")) return;
                if (profileData.SiteLatitude is not { } lat || profileData.SiteLongitude is not { } lon)
                {
                    Notify(NotificationSeverity.Warning, "Site location not configured for this profile");
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
                    Notify(NotificationSeverity.Warning, buildError);
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
                        Notify(NotificationSeverity.Error, $"Polar alignment failed: {ex.Message}");
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
        }
    }
}
