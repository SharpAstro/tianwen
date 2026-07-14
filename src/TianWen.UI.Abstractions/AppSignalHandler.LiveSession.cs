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
            var appState = _appState;
            var plannerState = _plannerState;
            var sessionState = _sessionState;
            var liveSessionState = _liveSessionState;
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
                liveSessionState.SessionCts?.Cancel();
                liveSessionState.ShowAbortConfirm = false;
                liveSessionState.NeedsRedraw = true;
                appState.NeedsRedraw = true;
            });
        }

        /// <summary>Wires the preview-mode signals (camera preview, snapshot save, plate solve, planetary capture).</summary>
        private void SubscribePreview(SignalBus bus, CancellationToken shutdownToken)
        {
            // Aliases over the injected fields keep the moved handler bodies verbatim
            // (the closures captured the ctor's parameters before the by-area split).
            var appState = _appState;
            var liveSessionState = _liveSessionState;
            var tracker = _tracker;
            var external = _external;
            var sp = _sp;
            var logger = _logger;

            // ---------------------------------------------------------------
            // Preview mode signals (camera preview, snapshot save, plate solve)
            // ---------------------------------------------------------------

            bus.Subscribe<TakePreviewSignal>(sig =>
            {
                if (!EnsureSessionIdle("Session is running \u2014 preview unavailable")) return;
                if (appState.ActiveProfile?.Data is not { } previewData || sig.OtaIndex >= previewData.OTAs.Length)
                {
                    Notify(NotificationSeverity.Warning, "Invalid OTA index");
                    return;
                }
                if (appState.DeviceHub is not { } hub) return;

                var ota = previewData.OTAs[sig.OtaIndex];
                if (!TryGetConnected<ICameraDriver>(hub, ota.Camera, "Camera", out var camera)) return;

                // Resolve the OTA's other devices for per-capture FITS denorm. Mount is
                // optional (preview can fire without one) but unlocks Target stamping
                // and FakeCameraDriver synthetic-catalog rendering when present.
                var (previewFocuser, previewFilterWheel, previewMount) =
                    EquipmentActions.ResolveOtaCaptureDevices(hub, previewData, sig.OtaIndex);

                // Mark capturing
                if (sig.OtaIndex < liveSessionState.PreviewCapturing.Length)
                {
                    liveSessionState.PreviewCapturing[sig.OtaIndex] = true;
                    liveSessionState.PreviewCaptureStart[sig.OtaIndex] = _timeProvider.GetUtcNow();
                    liveSessionState.PreviewExposureDuration[sig.OtaIndex] = TimeSpan.FromSeconds(sig.ExposureSeconds);
                }
                appState.NeedsRedraw = true;

                RunTracked($"PreviewCapture OTA{sig.OtaIndex}", "Preview failed", async ct =>
                {
                    // Stamp denorm fields before exposing -- shared with Session.Imaging
                    // and polar alignment so the live preview's FITS headers (and the
                    // FakeCameraDriver's synthetic-catalog rendering) match the other
                    // capture paths exactly.
                    await CameraExposureActions.StampDenormAsync(
                        camera,
                        ota.Name,
                        ota.FocalLength,
                        ota.Aperture,
                        previewFocuser,
                        previewFilterWheel,
                        previewMount,
                        targetName: previewMount is not null ? "Preview" : null,
                        catalogDb: sp.GetRequiredService<ICelestialObjectDB>(),
                        logger: logger,
                        ct: ct).ConfigureAwait(false);

                    var image = await LiveSessionActions.CaptureCameraPreviewAsync(
                        camera,
                        TimeSpan.FromSeconds(sig.ExposureSeconds),
                        sig.Gain is { } g ? (short)g : null,
                        sig.Binning,
                        _timeProvider,
                        ct);

                    if (image is not null
                        && sig.OtaIndex < liveSessionState.LastCapturedImages.Length)
                    {
                        // Release the previous slot's image before replacing -- otherwise
                        // its ChannelBuffer ref never drops and the camera can't recycle
                        // (mirrors the polar-refine onFrameCaptured leak fix).
                        liveSessionState.LastCapturedImages[sig.OtaIndex]?.Release();
                        liveSessionState.LastCapturedImages[sig.OtaIndex] = image;
                        Notify(NotificationSeverity.Info, $"Preview captured: OTA {sig.OtaIndex + 1}");
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
                if (liveSessionState.IsRunning) return;
                if (sig.OtaIndex >= liveSessionState.LastCapturedImages.Length) return;
                if (liveSessionState.LastCapturedImages[sig.OtaIndex] is not { } image)
                {
                    Notify(NotificationSeverity.Warning, "No preview image to save");
                    return;
                }

                RunTracked("SaveSnapshot", "Snapshot failed", async _ =>
                {
                    var fileName = await LiveSessionActions.SaveSnapshotAsync(
                        image, sig.OtaIndex, external, _timeProvider);
                    Notify(NotificationSeverity.Info, $"Snapshot saved: {fileName}");
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<PlateSolvePreviewSignal>(sig =>
            {
                if (liveSessionState.IsRunning) return;
                if (sig.OtaIndex >= liveSessionState.LastCapturedImages.Length) return;
                if (liveSessionState.LastCapturedImages[sig.OtaIndex] is not { } image)
                {
                    Notify(NotificationSeverity.Warning, "No preview image to solve");
                    return;
                }
                // Drop duplicate clicks: if a solve is already running for this OTA,
                // ignore. The button is rendered as "Solving…" with no click handler,
                // but a stray hit before the redraw could still fire the signal.
                if (sig.OtaIndex < liveSessionState.PreviewPlateSolving.Length
                    && liveSessionState.PreviewPlateSolving[sig.OtaIndex])
                {
                    return;
                }

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

                    // Solve orchestration (search-origin derivation + result-to-message
                    // mapping) lives in LiveSessionActions so this lambda routes only.
                    var (result, message, solved) = await LiveSessionActions.SolvePreviewFrameAsync(
                        sp.GetRequiredService<IPlateSolverFactory>(), image, ct);
                    liveSessionState.PreviewPlateSolveResult = result;
                    Notify(solved ? NotificationSeverity.Info : NotificationSeverity.Warning, message);
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
                if (!TryResolveIdleOtaFocuser(sig.OtaIndex, out var focuser)) return;

                RunTracked($"JogFocuser OTA{sig.OtaIndex}", "Focuser jog failed", async ct =>
                {
                    var targetPos = await LiveSessionActions.JogFocuserAsync(focuser, sig.Steps, ct);
                    Notify(NotificationSeverity.Info, $"Focuser \u2192 {targetPos}");
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            // Live planetary capture: route Start/Stop to the shared PlanetaryCaptureController (the capture
            // loop + rolling-window stack live there; this only resolves the camera + configures the ROI).
            var planetaryCapture = sp.GetRequiredService<PlanetaryCaptureController>();

            bus.Subscribe<StartVideoCaptureSignal>(sig =>
            {
                if (liveSessionState.IsRunning) return;
                if (appState.ActiveProfile?.Data is not { OTAs: var otas } || sig.OtaIndex >= otas.Length) return;
                if (appState.DeviceHub is not { } hub) return;

                var ota = otas[sig.OtaIndex];
                if (!TryGetConnected<ICameraDriver>(hub, ota.Camera, "Camera", out var camera, "Connect a camera to start a planetary capture")) return;

                var (roiW, roiH) = PlanetaryCaptureActions.ConfigureRoi(camera, sig.RoiWidth, sig.RoiHeight);

                // Wire the coupled mount + OTA pixel scale for the COM recenter loop's coarse mount-jog
                // fallback (Phase C). No-op when no mount is connected or the scale is unknown (NaN) -- the
                // recenter loop then stays ROI-only.
                IMountDriver? recenterMount = null;
                if (appState.ActiveProfile?.Data is { } capturePdata
                    && capturePdata.Mount is { Scheme: not "none" } mountUri
                    && hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var rcMount) && rcMount is not null)
                {
                    recenterMount = rcMount;
                }
                planetaryCapture.AttachMount(
                    recenterMount, CoordinateUtils.PixelScaleArcsec(camera.PixelSizeX, ota.FocalLength));

                // Bind the capture's lifetime to the app shutdown token: quitting cancels it (its loops poll
                // the token), so the camera is released without an imperative Stop() in the quit path.
                planetaryCapture.Start(camera,
                    new VideoCaptureOptions(TimeSpan.FromMilliseconds(sig.ExposureMs), sig.Gain), shutdownToken);
                // Planetary capture is now a Live Session mode (not a standalone tab): show it there.
                liveSessionState.Mode = LiveSessionMode.Planetary;
                appState.ActiveTab = GuiTab.LiveSession;
                Notify(NotificationSeverity.Info, $"Planetary capture started ({roiW}x{roiH}, {sig.ExposureMs:F0} ms)");
            });

            bus.Subscribe<StopVideoCaptureSignal>(_ =>
            {
                planetaryCapture.Stop();
                Notify(NotificationSeverity.Info, "Planetary capture stopped");
            });

            // Manual mount nudge (planetary panel coarse-recenter buttons) -> the same pulse-guide actuator the
            // COM recenter loop uses. Mirrors the focuser-jog route: resolve the connected mount, pulse on the
            // tracker. Gated on no running session + a pulse-guide-capable mount.
            bus.Subscribe<JogMountSignal>(sig =>
            {
                if (liveSessionState.IsRunning) return;
                if (appState.ActiveProfile?.Data is not { } pdata) return;
                if (pdata.Mount is not { Scheme: not "none" } mountUri) return;
                if (appState.DeviceHub is not { } hub) return;
                if (!hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var mount) || mount is null) return;

                RunTracked($"JogMount {sig.Direction}", "Mount jog failed", async ct =>
                {
                    await MountActions.PulseGuideArcsecAsync(
                        mount, sig.Direction, sig.Arcsec, _timeProvider, logger: logger, cancellationToken: ct);
                    Notify(NotificationSeverity.Info, $"Mount nudge {sig.Direction} {sig.Arcsec:F0} arcsec");
                }, onFinally: () => appState.NeedsRedraw = true);
            });

            bus.Subscribe<GotoFocuserSignal>(sig =>
            {
                if (!TryResolveIdleOtaFocuser(sig.OtaIndex, out var focuser)) return;

                RunTracked($"GotoFocuser OTA{sig.OtaIndex}", "Focuser goto failed", async ct =>
                {
                    await focuser.BeginMoveAsync(sig.TargetPosition, ct);
                    Notify(NotificationSeverity.Info, $"Focuser \u2192 {sig.TargetPosition}");
                }, onFinally: () => appState.NeedsRedraw = true);
            });
        }
    }
}
