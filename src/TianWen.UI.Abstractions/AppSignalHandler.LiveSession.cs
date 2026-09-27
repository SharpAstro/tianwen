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
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Sequencing.PolarAlignment;
using TianWen.Hosting.Dto;

namespace TianWen.UI.Abstractions
{
    // AppSignalHandler.LiveSession.cs -- live-session + preview-mode signals.
    // One partial per concern (see the class doc in AppSignalHandler.cs); handler bodies
    // moved verbatim from the single-file ctor in the Phase-5 by-area split.
    public partial class AppSignalHandler
    {
        /// <summary>Wires the live-session signals (session start/stop orchestration).</summary>
        private void SubscribeLiveSession(SignalBus bus)
        {
            // Aliases over the injected fields keep the moved handler bodies verbatim
            // (the closures captured the ctor's parameters before the by-area split).
            // Capturing the LOCAL session state here is safe -- the local context is created once and
            // never replaced, so the reference stays valid for the process lifetime. A handler that
            // ever routes to the on-screen context must NOT capture like this: it has to resolve
            // _contexts.Active at post time, since the user can switch contexts between subscribe and post.
            var appState = _appState;
            var plannerState = _plannerState;
            var sessionState = _sessionState;
            var liveSessionState = LocalLiveSession;
            var tracker = _tracker;
            var cts = _cts;
            var external = _external;
            var sp = _sp;
            var logger = _logger;

            // ---------------------------------------------------------------
            // Live session signals
            // ---------------------------------------------------------------

            bus.Subscribe<StartSessionSignal>(async _ =>
            {
                if (!EnsureLocalContext("A session")) return;
                if (!EnsureSessionIdle("Session already running")) return;

                if (appState.ActiveProfile is not { } profile)
                {
                    Notify(NotificationSeverity.Warning, "No profile selected");
                    return;
                }

                if (plannerState.Proposals is not { Length: > 0 })
                {
                    Notify(NotificationSeverity.Warning, "No targets \u2014 pin targets in the Planner first");
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
                // The ABORT of the rig on screen: this computer's session, or a rig's through its own node (P5b part 5).
                _contexts.Active.LiveSession.ShowAbortConfirm = false;
                StopActiveRun("Aborting the session", () => liveSessionState.SessionCts?.Cancel());
                _contexts.Active.LiveSession.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });
        }

        /// <summary>Wires the preview-mode signals (camera preview, snapshot save, plate solve, planetary capture).</summary>
        private void SubscribePreview(SignalBus bus, CancellationToken shutdownToken)
        {
            // Aliases over the injected fields keep the moved handler bodies verbatim
            // (the closures captured the ctor's parameters before the by-area split).
            var appState = _appState;
            var liveSessionState = LocalLiveSession;
            var tracker = _tracker;
            var external = _external;
            var sp = _sp;
            var logger = _logger;

            // ---------------------------------------------------------------
            // Preview mode signals (camera preview, snapshot save, plate solve)
            // ---------------------------------------------------------------

            bus.Subscribe<TakePreviewSignal>(sig =>
            {
                if (!EnsureLocalContext("A preview")) return;
                if (appState.ActiveProfile?.Data is not { } previewData || sig.OtaIndex >= previewData.OTAs.Length)
                {
                    Notify(NotificationSeverity.Warning, "Invalid OTA index");
                    return;
                }
                if (LocalNodeOrSay() is not { } node) return;

                // Mark capturing
                if (sig.OtaIndex < liveSessionState.PreviewCapturing.Length)
                {
                    liveSessionState.PreviewCapturing[sig.OtaIndex] = true;
                    liveSessionState.PreviewCaptureStart[sig.OtaIndex] = _timeProvider.GetUtcNow();
                    liveSessionState.PreviewExposureDuration[sig.OtaIndex] = TimeSpan.FromSeconds(sig.ExposureSeconds);
                }
                appState.NeedsRedraw = true;

                // The node exposes (it stamps the frame's headers as a session's are), keeps the frame as the OTA's, and
                // pushes FRAME-AVAILABLE, on which the view's mirror fetches it: the frame on show is the node's. The node
                // refuses a camera a run holds, in the run's name.
                var request = new PreviewExposureRequestDto { ExposureSeconds = sig.ExposureSeconds, Gain = sig.Gain is { } g ? (short)g : null, Binning = sig.Binning };
                RunTracked($"PreviewCapture OTA{sig.OtaIndex}", "Preview failed", async ct =>
                {
                    if (await RunNodeJobAsync(node, node.Client.StartPreviewExposureAsync(sig.OtaIndex, request, ct), "Preview", ct) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? $"Preview captured: OTA {sig.OtaIndex + 1}");
                    }
                }, onFinally: () =>
                {
                    if (sig.OtaIndex < liveSessionState.PreviewCapturing.Length)
                    {
                        liveSessionState.PreviewCapturing[sig.OtaIndex] = false;
                    }
                    appState.NeedsRedraw = true;
                }, cancelMessage: "Preview cancelled");
            });

            bus.Subscribe<SaveSnapshotSignal>(sig =>
            {
                if (!EnsureLocalContext("A snapshot")) return;
                if (LocalNodeOrSay() is not { } node) return;

                // Saved by the node, of the frame it shows: its own copy, where a session's subs are written.
                RunTracked("SaveSnapshot", "Snapshot failed", async ct =>
                {
                    var saved = await node.Client.SaveSnapshotAsync(sig.OtaIndex, ct);
                    Notify(saved.IsSuccess ? NotificationSeverity.Info : NotificationSeverity.Warning, saved.IsSuccess
                        ? $"Snapshot saved: {Path.GetFileName(saved.Value)}"
                        : saved.Error ?? "No preview image to save");
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<PlateSolvePreviewSignal>(sig =>
            {
                if (!EnsureLocalContext("A plate solve")) return;
                // Drop duplicate clicks: if a solve is already running for this OTA,
                // ignore. The button is rendered as "Solving…" with no click handler,
                // but a stray hit before the redraw could still fire the signal.
                if (sig.OtaIndex < liveSessionState.PreviewPlateSolving.Length
                    && liveSessionState.PreviewPlateSolving[sig.OtaIndex])
                {
                    return;
                }
                if (LocalNodeOrSay() is not { } node) return;

                if (sig.OtaIndex < liveSessionState.PreviewPlateSolving.Length)
                {
                    liveSessionState.PreviewPlateSolving[sig.OtaIndex] = true;
                }
                liveSessionState.NeedsRedraw = true;
                appState.NeedsRedraw = true;

                RunTracked("PreviewPlateSolve", "Plate solve error", async ct =>
                {
                    appState.StatusMessage = "Plate solving\u2026";
                    appState.NeedsRedraw = true;

                    // Solved by the node, of the frame it shows, with its solvers; the solution is the OTA's there.
                    if (await RunNodeJobAsync(node, node.Client.StartSolveAsync(sig.OtaIndex, ct), "Plate solve", ct) is not null)
                    {
                        await ShowSolutionAsync(node, sig.OtaIndex, ct);
                    }
                }, onFinally: () =>
                {
                    if (sig.OtaIndex < liveSessionState.PreviewPlateSolving.Length)
                    {
                        liveSessionState.PreviewPlateSolving[sig.OtaIndex] = false;
                    }
                    liveSessionState.NeedsRedraw = true;
                    appState.NeedsRedraw = true;
                });
            });

            bus.Subscribe<JogFocuserSignal>(sig =>
            {
                if (!TryResolveOtaFocuser(sig.OtaIndex, out var node, out var focuserUri)) return;

                RunTracked($"JogFocuser OTA{sig.OtaIndex}", "Focuser jog failed", async ct =>
                {
                    var move = new FocuserMoveRequestDto { DeviceUri = focuserUri.ToString(), Steps = sig.Steps };
                    if (await RunNodeJobAsync(node, node.Client.MoveFocuserAsync(move, ct), "Focuser jog", ct) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? "Focuser moved");
                    }
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            // Live planetary capture: route Start/Stop to the shared PlanetaryCaptureController, whose capture starts by
            // the one rule the node keeps too (PlanetaryCapture.TryStart: the camera, its claim, the ROI, the mount).
            var planetaryCapture = sp.GetRequiredService<PlanetaryCaptureController>();

            bus.Subscribe<StartVideoCaptureSignal>(sig =>
            {
                // The remote mode pill offers Planetary too, and this streams THIS computer's camera.
                if (!EnsureLocalContext("A planetary capture")) return;
                if (appState.ActiveProfile?.Data is not { } profileData) return;
                if (appState.DeviceHub is not { } hub) return;

                // Bound to the app shutdown token: quitting cancels the capture (its loops poll the token), so the camera
                // is released without an imperative Stop() in the quit path.
                var request = new PlanetaryCaptureRequest(sig.OtaIndex, TimeSpan.FromMilliseconds(sig.ExposureMs), sig.Gain, sig.RoiWidth, sig.RoiHeight);
                if (!planetaryCapture.TryStart(request, profileData, hub, shutdownToken, out var roi, out var refusal))
                {
                    Notify(NotificationSeverity.Warning, refusal);
                    return;
                }

                // Planetary capture is now a Live Session mode (not a standalone tab): show it there.
                liveSessionState.Mode = LiveSessionMode.Planetary;
                appState.ActiveTab = GuiTab.LiveSession;
                Notify(NotificationSeverity.Info, $"Planetary capture started ({roi.Width}x{roi.Height}, {sig.ExposureMs:F0} ms)");
            });

            bus.Subscribe<StopVideoCaptureSignal>(_ =>
            {
                planetaryCapture.Stop();
                Notify(NotificationSeverity.Info, "Planetary capture stopped");
            });

            // Manual mount nudge (planetary panel coarse-recenter buttons): one guide-rate pulse, as the node's job, which
            // ends when the mount reports the pulse done and is refused on a mount a run holds, in the run's name.
            bus.Subscribe<JogMountSignal>(sig =>
            {
                if (!EnsureLocalContext("A mount nudge")) return;
                if (appState.ActiveProfile?.Data is not { } pdata) return;
                if (pdata.Mount is not { Scheme: not "none" } mountUri) return;
                if (LocalNodeOrSay() is not { } node) return;

                RunTracked($"JogMount {sig.Direction}", "Mount jog failed", async ct =>
                {
                    if (await RunNodeJobAsync(node, node.Client.NudgeMountAsync(mountUri, sig.Direction, sig.Arcsec, ct), "Mount nudge", ct) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? $"Mount nudge {sig.Direction} {sig.Arcsec:F0} arcsec");
                    }
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<GotoFocuserSignal>(sig =>
            {
                if (!TryResolveOtaFocuser(sig.OtaIndex, out var node, out var focuserUri)) return;

                RunTracked($"GotoFocuser OTA{sig.OtaIndex}", "Focuser goto failed", async ct =>
                {
                    var move = new FocuserMoveRequestDto { DeviceUri = focuserUri.ToString(), Position = sig.TargetPosition };
                    if (await RunNodeJobAsync(node, node.Client.MoveFocuserAsync(move, ct), "Focuser goto", ct) is { } done)
                    {
                        Notify(NotificationSeverity.Info, done.Step ?? $"Focuser \u2192 {sig.TargetPosition}");
                    }
                }, onFinally: () => appState.NeedsRedraw = true);
            });
        }
    }
}
