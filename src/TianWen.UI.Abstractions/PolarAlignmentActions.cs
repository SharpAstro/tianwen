using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Sequencing.PolarAlignment;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Routes polar-alignment lifecycle from the signal handler into the
    /// orchestrator. Pure plumbing: takes a constructed
    /// <see cref="PolarAlignmentSession"/>, drives Phase A then Phase B,
    /// reflects the orchestrator's results back into <see cref="LiveSessionState"/>
    /// fields the live view's render thread reads each frame.
    ///
    /// All math + I/O lives in <see cref="PolarAlignmentSession"/>; this helper
    /// has no business logic. Mirror of <see cref="EquipmentActions"/> /
    /// <see cref="PlannerActions"/> for the polar-alignment surface.
    /// </summary>
    internal static class PolarAlignmentActions
    {
        /// <summary>
        /// Result of <see cref="BuildCaptureSource"/>: either a constructed
        /// <see cref="ICaptureSource"/> (plus the guider that must be stopped before the
        /// run, if the guider path was chosen), or a user-facing <paramref name="Error"/>
        /// explaining which device is missing.
        /// </summary>
        internal readonly record struct CaptureSourceResult(
            ICaptureSource? Source,
            IGuider? ActiveGuider,
            string? Error);

        /// <summary>
        /// Builds the polar-alignment capture source from the profile + connected devices.
        /// Two paths: <see cref="StartPolarAlignmentSignal.UseGuider"/> wraps the connected
        /// <see cref="IGuider"/> (needs guider camera pixel size + profile guider focal
        /// length); otherwise wraps the OTA's main camera. Frame-captured / frame-solved
        /// callbacks publish into <paramref name="liveSessionState"/> so the live mini viewer
        /// renders each probe frame + tracks the refine WCS. Device-resolution logic extracted
        /// from the StartPolarAlignmentSignal handler so the lambda routes only.
        /// </summary>
        internal static CaptureSourceResult BuildCaptureSource(
            StartPolarAlignmentSignal sig,
            ProfileData profileData,
            IDeviceHub hub,
            IMountDriver mount,
            LiveSessionState liveSessionState,
            IExternal external,
            ICelestialObjectDB catalogDb,
            ITimeProvider timeProvider,
            ILogger logger)
        {
            if (sig.UseGuider)
            {
                if (!hub.TryGetConnectedDriver<IGuider>(profileData.Guider, out var guider) || guider is null)
                {
                    return new CaptureSourceResult(null, null, "Guider not connected — connect a guider or untoggle Use Guider");
                }
                if (profileData.GuiderCamera is not { } guideCamUri
                    || !hub.TryGetConnectedDriver<ICameraDriver>(guideCamUri, out var guideCam)
                    || guideCam is null)
                {
                    return new CaptureSourceResult(null, null, "Guider camera not connected — cannot determine pixel scale");
                }
                if (profileData.GuiderFocalLength is not { } guiderFlMm || guiderFlMm <= 0)
                {
                    return new CaptureSourceResult(null, null, "Guider focal length not set in profile — required for plate scale");
                }

                // Aperture isn't strictly recorded for guide scopes; assume f/4 if absent
                // (a typical 50mm/200mm-ish mini guider). Used only for ranking heuristics
                // and a UI hint string.
                var guiderApertureMm = guiderFlMm / 4.0;
                var guiderMount = mount;
                Func<CancellationToken, ValueTask<(double RaHours, double DecDeg)?>> guiderSearchOrigin = async tok =>
                {
                    var ra = await guiderMount.GetRightAscensionAsync(tok).ConfigureAwait(false);
                    var dec = await guiderMount.GetDeclinationAsync(tok).ConfigureAwait(false);
                    return (ra, dec);
                };
                var guiderSource = new GuiderCaptureSource(
                    guider,
                    displayName: $"Guider — {guider.Name}",
                    focalLengthMm: guiderFlMm,
                    apertureMm: guiderApertureMm,
                    pixelSizeMicrons: guideCam.PixelSizeX,
                    external,
                    logger,
                    searchOriginAsync: guiderSearchOrigin);
                return new CaptureSourceResult(guiderSource, guider, null);
            }

            var otaIndex = sig.OtaIndex >= 0 && sig.OtaIndex < profileData.OTAs.Length
                ? sig.OtaIndex
                : 0;
            var ota = profileData.OTAs[otaIndex];
            if (!hub.TryGetConnectedDriver<ICameraDriver>(ota.Camera, out var camera) || camera is null)
            {
                return new CaptureSourceResult(null, null, $"OTA #{otaIndex + 1} camera not connected");
            }

            // Resolve focuser / filter wheel; the capture source's per-frame
            // CameraExposureActions.StampDenormAsync call handles all denorm
            // (telescope, focal length, aperture, site, focuser, filter, Target,
            // catalog DB) -- shared with Session.Imaging and the live preview
            // path so the three never drift.
            IFocuserDriver? otaFocuser = null;
            if (ota.Focuser is { } focuserUri
                && hub.TryGetConnectedDriver<IFocuserDriver>(focuserUri, out var focuserDriver))
            {
                otaFocuser = focuserDriver;
            }
            IFilterWheelDriver? otaFilterWheel = null;
            if (ota.FilterWheel is { } fwUri
                && hub.TryGetConnectedDriver<IFilterWheelDriver>(fwUri, out var fwDriver))
            {
                otaFilterWheel = fwDriver;
            }

            // Publish each captured probe frame into the shared
            // LastCapturedImages slot so the live mini viewer renders
            // the live frame during the multi-rung ramp -- without
            // this the user stares at a black panel for the entire
            // 5-30s solve and assumes the routine is hung. The
            // callback transfers ownership of the Image to the UI
            // (capture source skips Release), so we keep the previous
            // slot's image around until it gets replaced; nothing
            // else holds a strong reference to it after the next
            // assignment, so the buffer pool reclaims naturally.
            var publishOtaIndex = otaIndex;
            var mainSource = new MainCameraCaptureSource(
                camera,
                displayName: $"OTA #{otaIndex + 1} — {ota.Name}",
                focalLengthMm: ota.FocalLength,
                apertureMm: ota.Aperture ?? Math.Max(1, ota.FocalLength / 5),
                otaName: ota.Name,
                focuser: otaFocuser,
                filterWheel: otaFilterWheel,
                mount: mount,
                targetName: "Polar Align",
                catalogDb: catalogDb,
                timeProvider: timeProvider,
                imageReadyPollInterval: external.ImageReadyPollInterval,
                logger: logger,
                onFrameCaptured: img =>
                {
                    if (publishOtaIndex < liveSessionState.LastCapturedImages.Length)
                    {
                        // Release the previous frame before replacing it,
                        // otherwise its ChannelBuffer ref never drops and the
                        // camera can't recycle -- a 60MP polar refine at a
                        // few Hz leaks hundreds of MB / second. The UI render
                        // thread may briefly read recycled pixels during the
                        // swap (one frame of flicker, worst case); paying that
                        // for a bounded heap is the right trade.
                        liveSessionState.LastCapturedImages[publishOtaIndex]?.Release();
                        liveSessionState.LastCapturedImages[publishOtaIndex] = img;
                        liveSessionState.NeedsRedraw = true;
                    }
                },
                // Each rotation/probe solve refreshes the preview WCS so
                // the mini viewer's grid + WCS-anchored markers track
                // the live field as it sweeps past the pole. Failed
                // solves still publish (Solution=null) so a stale grid
                // doesn't linger on a frame the WCS no longer fits.
                onFrameSolved: result =>
                {
                    liveSessionState.PreviewPlateSolveResult = result;
                    liveSessionState.NeedsRedraw = true;
                });
            return new CaptureSourceResult(mainSource, null, null);
        }

        /// <summary>
        /// Builds the <see cref="PolarAlignmentSite"/> for the run: site coordinates from the
        /// profile, refraction inputs from a connected weather device else the standard
        /// atmosphere (the same two-tier <see cref="SiteConditions.Resolve"/> chain the session
        /// uses). Extracted from the StartPolarAlignmentSignal handler.
        /// </summary>
        internal static PolarAlignmentSite BuildSite(ProfileData profileData, IDeviceHub hub, double lat, double lon)
        {
            // Refraction inputs: a connected weather device supplies the live values, else the
            // standard atmosphere -- the same two-tier chain the session uses
            // (SiteConditions.Resolve). PolarAlignmentSite needs concrete numbers, so the
            // standard tier collapses to SiteConditions.StandardPressureHPa here rather than
            // the elevation auto-derive used where a Transform is the consumer.
            IWeatherDriver? weather = null;
            if (profileData.Weather is { } weatherUri)
            {
                hub.TryGetConnectedDriver<IWeatherDriver>(weatherUri, out weather);
            }
            var conditions = SiteConditions.Resolve(weather);

            return new PolarAlignmentSite(
                LatitudeDeg: lat,
                LongitudeDeg: lon,
                ElevationM: profileData.SiteElevation ?? 0,
                PressureHPa: conditions.PressureHPa,
                TemperatureC: conditions.TemperatureCelsius);
        }

        /// <summary>
        /// Run the full polar-alignment routine: Phase A (two-frame solve) then
        /// Phase B (live refinement) until the cancellation token fires.
        /// State is updated synchronously at each phase boundary and on each
        /// successful refinement tick. The reverse-axis restore happens via
        /// <see cref="PolarAlignmentSession.DisposeAsync"/> in the caller's
        /// finally block — this helper does not own session disposal.
        /// </summary>
        /// <remarks>
        /// Cancellation: callers pass a linked CTS so they can cancel via the
        /// <see cref="LiveSessionState.PolarAlignmentCts"/> stored alongside the
        /// session. The orchestrator's <see cref="PolarAlignmentSession.RefineAsync"/>
        /// loop honours the token, so a Cancel signal interrupts the loop and
        /// the caller's finally block disposes the session, triggering the
        /// reverse-axis restore.
        /// </remarks>
        internal static async Task RunAsync(
            PolarAlignmentSession session,
            LiveSessionState state,
            ILogger logger,
            CancellationToken ct)
        {
            // --- Phase A ---
            state.PolarPhase = PolarAlignmentPhase.ProbingExposure;
            state.PolarStatusMessage = "Probing exposure...";
            state.NeedsRedraw = true;

            // Per-rung progress: surface "Probing 200ms (rung 3/8)" as the ramp
            // walks so the user knows the routine is making forward motion during
            // the multi-second per-rung ASTAP solve attempts. Without this the
            // panel sits on "Probing exposure..." for ~50s and looks stuck.
            var progress = new Progress<ProbeProgress>(p =>
            {
                state.PolarStatusMessage = $"Probing {p.Exposure.TotalMilliseconds:F0}ms (rung {p.RungIndex + 1}/{p.RungCount})";
                state.NeedsRedraw = true;
            });

            // Phase-A sub-phase transitions: the ramp finishes in seconds but
            // the rotation + settle + frame-2 leg of Phase A takes another
            // 15-30s. Without these reports, the UI sits on the last "Probing
            // 200ms (4/8)" message for that whole time and the user can't tell
            // whether the routine is alive or hung. Each transition flips the
            // phase pill (PROBING -> ROTATING -> FRAME 2) and rewrites the
            // status line. Rotation re-emits ~4 times/sec with elapsed/total
            // so the user gets a live countdown.
            var phaseProgress = new Progress<PolarPhaseUpdate>(update =>
            {
                state.PolarPhase = update.Phase;
                state.PolarStatusMessage = update.Detail
                    ?? update.Phase switch
                    {
                        PolarAlignmentPhase.Rotating => "Rotating RA axis...",
                        PolarAlignmentPhase.Frame2 => "Capturing frame 2...",
                        _ => state.PolarStatusMessage,
                    };
                state.NeedsRedraw = true;
            });

            TwoFrameSolveResult phaseA;
            try
            {
                phaseA = await session.SolveAsync(ct, progress, phaseProgress);
            }
            catch (OperationCanceledException)
            {
                state.PolarPhase = PolarAlignmentPhase.Idle;
                state.PolarStatusMessage = "Cancelled before Phase A completed";
                state.NeedsRedraw = true;
                throw;
            }

            state.PolarPhaseAResult = phaseA;
            if (!phaseA.Success)
            {
                state.PolarPhase = PolarAlignmentPhase.Failed;
                state.PolarStatusMessage = phaseA.FailureReason ?? "Phase A failed";
                state.NeedsRedraw = true;
                logger.LogWarning("PolarAlignment Phase A failed: {Reason}", phaseA.FailureReason);
                return;
            }

            // --- Phase B ---
            state.PolarPhase = PolarAlignmentPhase.Refining;
            state.PolarStatusMessage = $"Refining at {phaseA.LockedExposure.TotalMilliseconds:F0}ms";
            state.NeedsRedraw = true;

            try
            {
                await foreach (var live in session.RefineAsync(ct))
                {
                    state.LastPolarSolve = live;
                    // Refresh PreviewPlateSolveResult with the live refine WCS so
                    // the GUI's mini-viewer.Wcs binding (in LiveSessionTab) tracks
                    // the *current* refine pose. Without this the WCS stays frozen
                    // at whatever Phase A's last source.CaptureAndSolveAsync
                    // callback published, and the polar overlay (rings, axis
                    // crosshair, cross meridians, correction arrow) projects
                    // through a stale WCS -- markers land in the wrong pixels or
                    // off-frame, so the user thinks the overlay never rendered.
                    if (live.Wcs is { } liveWcs)
                    {
                        state.PreviewPlateSolveResult = new PlateSolveResult(liveWcs, TimeSpan.Zero)
                        {
                            MatchedStars = live.StarsMatched,
                        };
                    }
                    if (live.IsAligned && live.IsSettled && state.PolarPhase != PolarAlignmentPhase.Aligned)
                    {
                        state.PolarPhase = PolarAlignmentPhase.Aligned;
                        state.PolarStatusMessage = "Aligned within target accuracy - click Done";
                    }
                    else if (!live.IsAligned && state.PolarPhase == PolarAlignmentPhase.Aligned)
                    {
                        // User bumped a knob and broke alignment — fall back to refining.
                        state.PolarPhase = PolarAlignmentPhase.Refining;
                        state.PolarStatusMessage = "Refining...";
                    }
                    state.NeedsRedraw = true;
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("PolarAlignment refinement cancelled");
                throw;
            }
        }
    }
}
