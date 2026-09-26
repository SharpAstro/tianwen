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
using TianWen.Lib;

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
            var liveSessionState = LocalLiveSession;
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
                if (!EnsureLocalContext("Polar alignment")) return;
                if (!EnsureSessionIdle("Session is running: polar alignment unavailable")) return;
                if (liveSessionState.PolarAlignmentCts is not null)
                {
                    Notify(NotificationSeverity.Warning, "Polar alignment already running");
                    return;
                }
                if (appState.ActiveProfile?.Data is not { } profileData)
                {
                    Notify(NotificationSeverity.Warning, "No profile / OTA configured");
                    return;
                }
                if (appState.DeviceHub is not { } hub)
                {
                    Notify(NotificationSeverity.Warning, "Device hub not available");
                    return;
                }

                // The setup panel supplies the full configuration; the toolbar and the TUI pin only the rotation.
                var request = new PolarAlignmentRequest(sig.OtaIndex, sig.UseGuider,
                    sig.Configuration ?? (PolarAlignmentConfiguration.Default with { RotationDeg = sig.DeltaRaDeg }));
                // The devices, the capture source and the claim on them are the run's (PolarAlignmentRun, which the node
                // runs too); what stays here is where its state and its frames are shown.
                if (!PolarAlignmentRun.TryCreate(request, profileData, hub, external, sp.GetRequiredService<ICelestialObjectDB>(),
                    sp.GetRequiredService<IPlateSolverFactory>(), _timeProvider, logger,
                    onFrameCaptured: (otaIndex, image) => ShowPolarFrame(liveSessionState, otaIndex, image),
                    onFrameSolved: result =>
                    {
                        liveSessionState.PreviewPlateSolveResult = result;
                        liveSessionState.NeedsRedraw = true;
                    },
                    out var run, out var refusal))
                {
                    Notify(NotificationSeverity.Warning, refusal);
                    return;
                }

                var polarCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                liveSessionState.PolarAlignmentCts = polarCts;
                liveSessionState.Mode = LiveSessionMode.PolarAlign;
                ShowPolarState(liveSessionState, run.State);
                run.StateChanged += state => ShowPolarState(liveSessionState, state);
                appState.NeedsRedraw = true;

                // Completed last of all, whatever the run does: a RigShutdown waits on it before it touches the mount or
                // the camera polar was driving, and the run has given both back by then.
                var ended = liveSessionState.BeginPolarRun();
                tracker.Run(async () =>
                {
                    try
                    {
                        await run.RunAsync(polarCts.Token);
                    }
                    catch (Exception ex)
                    {
                        // The run's own state says so already, on the status line.
                        Notify(NotificationSeverity.Error, $"Polar alignment failed: {ex.Message}");
                    }
                    finally
                    {
                        liveSessionState.PolarAlignmentCts = null;
                        polarCts.Dispose();
                        // Drop back into preview mode unless the user already swapped tabs.
                        if (liveSessionState.Mode == LiveSessionMode.PolarAlign)
                        {
                            liveSessionState.Mode = LiveSessionMode.Preview;
                        }
                        liveSessionState.NeedsRedraw = true;
                        appState.NeedsRedraw = true;
                        ended.TrySetResult();
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

        /// <summary>Reflects a polar run's state into the live view, which the render thread reads each frame.</summary>
        private static void ShowPolarState(LiveSessionState live, PolarRunState state)
        {
            live.PolarPhase = state.Phase;
            live.PolarStatusMessage = state.StatusMessage;
            live.PolarPhaseAResult = state.PhaseA;
            live.LastPolarSolve = state.LastSolve;
            live.NeedsRedraw = true;
        }

        /// <summary>
        /// Shows a polar probe or refinement frame in its OTA's slot, which it now OWNS: the previous frame there is released,
        /// or its camera buffer never goes back (a 60 MP refine at a few Hz leaks hundreds of MB a second). The render thread
        /// may briefly read recycled pixels during the swap, one frame of flicker at worst, which a bounded heap is worth.
        /// </summary>
        private static void ShowPolarFrame(LiveSessionState live, int otaIndex, Image image)
        {
            if (otaIndex >= live.LastCapturedImages.Length)
            {
                image.Release();
                return;
            }
            live.LastCapturedImages[otaIndex]?.Release();
            live.LastCapturedImages[otaIndex] = image;
            live.NeedsRedraw = true;
        }
    }
}
