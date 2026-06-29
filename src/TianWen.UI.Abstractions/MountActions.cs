using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Pure-dependency helpers for user-initiated mount operations (ad-hoc goto from
    /// the sky-map info panel, etc). Wraps <see cref="IMountDriver"/> primitives so
    /// the signal-handler lambdas stay routing-only and the logic is directly
    /// unit-testable against <c>Substitute.For&lt;IMountDriver&gt;()</c>.
    /// </summary>
    public static class MountActions
    {
        /// <summary>
        /// Slew a connected mount to a J2000 RA/Dec catalog position. The flow:
        /// <list type="number">
        ///   <item>Connected / CanSlewAsync gates — bail cleanly if unsatisfiable.</item>
        ///   <item>Auto-unpark if <see cref="IMountDriver.CanUnpark"/> and
        ///   <see cref="IMountDriver.AtParkAsync"/>.</item>
        ///   <item>Build the J2000→native <see cref="TianWen.Lib.Astrometry.SOFA.Transform"/>
        ///   from <paramref name="profile"/> + <paramref name="timeProvider"/> (not from
        ///   the mount). This sidesteps the mount's <c>TryGetTransformAsync</c> path,
        ///   which depends on <c>UTCDate</c> being readable and can fail silently on
        ///   certain ASCOM drivers (e.g. GSServer). The Equipment-tab Connect flow
        ///   already validated site coordinates on the profile, so trusting it is safe.</item>
        ///   <item>Compute native coordinates via <see cref="IMountDriver.TryTransformJ2000ToMountNativeAsync"/>
        ///   using our transform (<c>updateTime: false</c> so the helper does not read
        ///   the mount clock).</item>
        ///   <item>Horizon check against <paramref name="minAboveHorizonDegrees"/>.</item>
        ///   <item>Destination-pier-side sanity check.</item>
        ///   <item>Tracking setup — Solar / Lunar / Sidereal depending on
        ///   <paramref name="index"/>, with graceful fallback to Sidereal on mounts
        ///   that don't advertise the preferred rate, and best-effort (swallow +
        ///   log) behaviour if the driver throws.</item>
        ///   <item><see cref="IMountDriver.BeginSlewRaDecAsync"/> — the actual commit.</item>
        /// </list>
        /// </summary>
        /// <param name="mount">A connected mount driver.</param>
        /// <param name="name">Display name for status messages.</param>
        /// <param name="raJ2000Hours">Right ascension in J2000 hours.</param>
        /// <param name="decJ2000Deg">Declination in J2000 degrees.</param>
        /// <param name="index">Catalog index, used to pick Sun/Moon tracking rate.</param>
        /// <param name="profile">Active profile (for site coordinates).</param>
        /// <param name="timeProvider">System time source (for the transform's UTC).</param>
        /// <param name="minAboveHorizonDegrees">Minimum altitude above horizon.
        /// Callers should pass <c>PlannerState.MinHeightAboveHorizon</c> (clamped ≥ 1).</param>
        /// <param name="logger">Optional logger for exceptions swallowed by the helper.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task<(SlewPostCondition Post, string StatusMessage)> SlewToJ2000Async(
            IMountDriver mount,
            string name,
            double raJ2000Hours,
            double decJ2000Deg,
            CatalogIndex? index,
            Profile profile,
            ITimeProvider timeProvider,
            int minAboveHorizonDegrees = 10,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            if (!mount.Connected)
            {
                return (SlewPostCondition.SlewNotPossible, "Mount is not connected");
            }

            if (!mount.CanSlewAsync)
            {
                return (SlewPostCondition.SlewNotPossible, "Mount does not support async slewing");
            }

            // Park handling — only meaningful when the mount supports programmatic Unpark.
            // Many ASCOM drivers throw on Tracking/Slew while parked, so we auto-unpark first.
            var wasParked = false;
            if (mount.CanUnpark)
            {
                try
                {
                    if (await mount.AtParkAsync(cancellationToken))
                    {
                        await mount.UnparkAsync(cancellationToken);
                        wasParked = true;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex,
                        "Auto-unpark failed on {Mount} during Goto {Target}", mount.Name, name);
                    return (SlewPostCondition.SlewNotPossible,
                        $"Mount is parked and auto-unpark failed: {ex.Message}");
                }
            }

            // Mounts with neither Park nor Unpark — surface as an informational suffix.
            var noParkSupportNote = (!mount.CanPark && !mount.CanUnpark)
                ? " (mount has no park/unpark support)"
                : "";

            // Build the transform from profile + wall clock. Bypasses any mount-side
            // UTCDate/Site reads that may be flaky on some drivers.
            var transform = TransformFactory.FromProfile(profile, timeProvider, out var transformError);
            if (transform is null)
            {
                logger?.LogWarning("Goto {Target}: profile-derived transform unavailable: {Reason}", name, transformError);
                return (SlewPostCondition.SlewNotPossible,
                    $"Cannot slew to {name} \u2014 {transformError ?? "profile site coordinates unavailable"}");
            }

            // Convert J2000 → mount native. updateTime: false so we don't round-trip UTC
            // through the mount; our profile-based transform already has the time set.
            if (await mount.TryTransformJ2000ToMountNativeAsync(
                    transform, raJ2000Hours, decJ2000Deg, updateTime: false, cancellationToken) is not { } native)
            {
                logger?.LogWarning(
                    "Goto {Target}: coordinate transform produced NaN. Mount={Mount} EquatorialSystem={System} SiteLat={Lat:F4} SiteLon={Lon:F4}",
                    name, mount.Name, mount.EquatorialSystem, transform.SiteLatitude, transform.SiteLongitude);
                return (SlewPostCondition.SlewNotPossible,
                    $"Cannot slew to {name} \u2014 coordinate transform failed (EquatorialSystem={mount.EquatorialSystem}){noParkSupportNote}");
            }

            // Horizon check — use the alt we just computed, not a second mount call.
            if (native.Alt < minAboveHorizonDegrees)
            {
                return (SlewPostCondition.TargetBelowHorizonLimit,
                    $"{name} is below the horizon limit ({minAboveHorizonDegrees}\u00B0){noParkSupportNote}");
            }

            // Destination-pier-side sanity (same check the session's BeginSlewToTargetAsync does).
            PointingState dsop;
            try
            {
                dsop = await mount.DestinationSideOfPierAsync(native.RaMount, native.DecMount, cancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "DestinationSideOfPier failed on {Mount} for Goto {Target}", mount.Name, name);
                return (SlewPostCondition.SlewNotPossible,
                    $"Cannot slew to {name} \u2014 mount rejected destination pier check: {ex.Message}{noParkSupportNote}");
            }
            if (dsop == PointingState.Unknown)
            {
                return (SlewPostCondition.SlewNotPossible,
                    $"Cannot slew to {name} \u2014 mount could not determine destination pier side{noParkSupportNote}");
            }

            // Tracking rate selection — Sun / Moon / Sidereal with graceful fallback.
            var preferred = index switch
            {
                CatalogIndex.Sol  => TrackingSpeed.Solar,
                CatalogIndex.Moon => TrackingSpeed.Lunar,
                _                 => TrackingSpeed.Sidereal
            };
            var actualSpeed = mount.TrackingSpeeds.Contains(preferred) ? preferred : TrackingSpeed.Sidereal;

            // Best-effort tracking setup. Tracking failures don't block pointing — the
            // subsequent BeginSlewRaDec call still works.
            var trackingFailed = false;
            try
            {
                await mount.EnsureTrackingAsync(actualSpeed, cancellationToken);
            }
            catch (Exception ex)
            {
                trackingFailed = true;
                logger?.LogWarning(ex,
                    "EnsureTracking({Speed}) failed on {Mount} during Goto {Target}; continuing with slew",
                    actualSpeed, mount.Name, name);
            }

            // Commit the slew.
            await mount.BeginSlewRaDecAsync(native.RaMount, native.DecMount, cancellationToken);

            var rateNote = trackingFailed
                ? " (tracking not set \u2014 check mount state)"
                : actualSpeed != preferred
                    ? $" (tracking at sidereal \u2014 mount does not support {preferred})"
                    : actualSpeed switch
                    {
                        TrackingSpeed.Solar => " (solar tracking)",
                        TrackingSpeed.Lunar => " (lunar tracking)",
                        _                   => ""
                    };
            var parkedNote = wasParked ? " (auto-unparked)" : "";

            return (SlewPostCondition.Slewing, $"Slewing to {name}{rateNote}{parkedNote}{noParkSupportNote}");
        }

        /// <summary>
        /// Polls <see cref="IMountDriver.IsSlewingAsync"/> until it returns false (slew complete),
        /// the timeout expires, or the token is cancelled. Returns a user-facing status string
        /// suitable for an <c>Info</c>-severity notification ("Reached M31") or a <c>Warning</c>
        /// for the timeout / fault paths. The caller is responsible for posting the notification.
        /// </summary>
        public enum SlewCompletion { Reached, TimedOut, PollFailed }

        public static async Task<(SlewCompletion Result, string StatusMessage)> AwaitSlewCompletionAsync(
            IMountDriver mount,
            string name,
            ITimeProvider timeProvider,
            TimeSpan? timeout = null,
            TimeSpan? pollInterval = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            var deadline = timeProvider.GetUtcNow() + (timeout ?? TimeSpan.FromMinutes(5));
            var interval = pollInterval ?? TimeSpan.FromMilliseconds(500);

            while (timeProvider.GetUtcNow() < deadline)
            {
                await timeProvider.SleepAsync(interval, cancellationToken);
                try
                {
                    if (!await mount.IsSlewingAsync(cancellationToken))
                    {
                        return (SlewCompletion.Reached, $"Reached {name}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Slew completion poll failed for {Target}", name);
                    return (SlewCompletion.PollFailed,
                        $"Slew status check failed for {name}: {ex.Message}");
                }
            }
            return (SlewCompletion.TimedOut,
                $"Slew to {name} did not complete within {(timeout ?? TimeSpan.FromMinutes(5)).TotalMinutes:F0} min");
        }

        /// <summary>Outcome category of <see cref="SolveAndSyncAsync"/>.</summary>
        public enum SolveSyncResult { Synced, NotPossible, CaptureFailed, SolveFailed, SyncFailed }

        /// <summary>
        /// Result of <see cref="SolveAndSyncAsync"/>. <paramref name="CapturedImage"/> is non-null
        /// once a frame was captured, regardless of the solve/sync outcome - ownership transfers
        /// to the caller, who must either retain it (e.g. stash into the preview slot, releasing
        /// the previous occupant) or <see cref="Image.Release"/> it; otherwise the camera buffer
        /// stays pinned. <paramref name="SolveResult"/> is non-null once a solve was attempted
        /// (so the UI can mirror it into <c>LiveSessionState.PreviewPlateSolveResult</c>).
        /// </summary>
        public sealed record SolveSyncOutcome(
            SolveSyncResult Result,
            string StatusMessage,
            Image? CapturedImage,
            PlateSolveResult? SolveResult);

        /// <summary>
        /// Capture a frame, plate-solve it, and sync the mount to the solved position - the
        /// "where am I ACTUALLY pointing" primitive for the sky map. The mount's marker then
        /// jumps to reality on the next telemetry poll and the user decides whether to re-slew
        /// (deliberately NOT automated here). Flow:
        /// <list type="number">
        ///   <item>Gates: mount connected + <see cref="IMountDriver.CanSync"/>; camera connected.
        ///   Note <see cref="IMountDriver.CanSlew"/> is NOT required - for slew-less trackers
        ///   (iOptron SkyGuider Pro) this is the only way the reported position can ever be
        ///   truthful: aim by hand, solve &amp; sync, check the marker, repeat.</item>
        ///   <item>Believed pointing via the profile-derived transform (same
        ///   <see cref="TransformFactory.FromProfile"/> route as <see cref="SlewToJ2000Async"/>,
        ///   no flaky mount-side UTC reads) - the solver search origin and the baseline the
        ///   revealed pointing error is measured from.</item>
        ///   <item><see cref="CameraExposureActions.StampDenormAsync"/> +
        ///   <see cref="LiveSessionActions.CaptureCameraPreviewAsync"/> - the same capture path
        ///   as the live-session preview, so FITS headers and fake-camera catalog rendering
        ///   behave identically.</item>
        ///   <item><see cref="IPlateSolver.SolveImageAsync"/> with the believed origin.</item>
        ///   <item>Solved J2000 → mount native via
        ///   <see cref="IMountDriver.TryTransformJ2000ToMountNativeAsync"/>, then
        ///   <see cref="IMountDriver.SyncRaDecAsync"/>.</item>
        /// </list>
        /// </summary>
        /// <param name="mount">A connected mount driver supporting sync.</param>
        /// <param name="camera">A connected camera to capture the solve frame with.</param>
        /// <param name="otaName">OTA display name for FITS denorm stamping.</param>
        /// <param name="focalLengthMm">OTA focal length for denorm + solver scale.</param>
        /// <param name="apertureMm">OTA aperture for denorm (null = unknown).</param>
        /// <param name="focuser">Optional focuser for denorm stamping.</param>
        /// <param name="filterWheel">Optional filter wheel for denorm stamping.</param>
        /// <param name="catalogDb">Catalog DB for denorm target resolution (null = skip).</param>
        /// <param name="solverFactory">Plate solver selection facade.</param>
        /// <param name="profile">Active profile (for site coordinates).</param>
        /// <param name="timeProvider">System time source (for the transform's UTC).</param>
        /// <param name="exposure">Solve-frame exposure duration.</param>
        /// <param name="gain">Optional camera gain override (null = camera default).</param>
        /// <param name="binning">Binning factor (1 = unbinned).</param>
        /// <param name="logger">Optional logger for swallowed faults.</param>
        /// <param name="cancellationToken">Cancellation token. On cancellation the captured
        /// image (if any) is released before the exception propagates.</param>
        public static async Task<SolveSyncOutcome> SolveAndSyncAsync(
            IMountDriver mount,
            ICameraDriver camera,
            string otaName,
            int focalLengthMm,
            int? apertureMm,
            IFocuserDriver? focuser,
            IFilterWheelDriver? filterWheel,
            ICelestialObjectDB? catalogDb,
            IPlateSolverFactory solverFactory,
            Profile profile,
            ITimeProvider timeProvider,
            TimeSpan exposure,
            short? gain = null,
            int binning = 1,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            if (!mount.Connected)
            {
                return new SolveSyncOutcome(SolveSyncResult.NotPossible, "Mount is not connected", null, null);
            }
            if (!mount.CanSync)
            {
                return new SolveSyncOutcome(SolveSyncResult.NotPossible, $"{mount.Name} does not support sync", null, null);
            }
            if (!camera.Connected)
            {
                return new SolveSyncOutcome(SolveSyncResult.NotPossible, "Camera is not connected", null, null);
            }

            var transform = TransformFactory.FromProfile(profile, timeProvider, out var transformError);
            if (transform is null)
            {
                logger?.LogWarning("Solve & sync: profile-derived transform unavailable: {Reason}", transformError);
                return new SolveSyncOutcome(SolveSyncResult.NotPossible,
                    $"Cannot solve & sync \u2014 {transformError ?? "profile site coordinates unavailable"}", null, null);
            }

            // Believed pointing: the solver's search origin AND the baseline the revealed
            // pointing error is measured from. The built-in CatalogPlateSolver is not a
            // blind solver, so a missing origin is a hard stop, not a degraded mode.
            if (await mount.GetRaDecJ2000Async(transform, updateTime: false, cancellationToken) is not { } believed)
            {
                return new SolveSyncOutcome(SolveSyncResult.NotPossible, "Mount pointing unavailable", null, null);
            }

            // Same stamp + capture path as the live-session preview (single source of truth):
            // headers match, and the fake camera renders its synthetic catalog field.
            await CameraExposureActions.StampDenormAsync(
                camera, otaName, focalLengthMm, apertureMm, focuser, filterWheel, mount,
                targetName: "SolveSync", catalogDb: catalogDb, logger: logger, ct: cancellationToken);

            var image = await LiveSessionActions.CaptureCameraPreviewAsync(
                camera, exposure, gain, binning, timeProvider, cancellationToken);
            if (image is null)
            {
                return new SolveSyncOutcome(SolveSyncResult.CaptureFailed, "Capture produced no frame", null, null);
            }

            PlateSolveResult solveResult;
            try
            {
                solveResult = await solverFactory.SolveImageAsync(
                    image, searchOrigin: new WCS(believed.RaJ2000, believed.DecJ2000), cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException oce)
            {
                logger?.LogInformation(oce, "Solve & sync cancelled during plate solve");
                image.Release();
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Solve & sync: plate solve threw");
                return new SolveSyncOutcome(SolveSyncResult.SolveFailed, $"Plate solve error: {ex.Message}", image, null);
            }

            if (solveResult.Solution is not { } wcs)
            {
                return new SolveSyncOutcome(SolveSyncResult.SolveFailed,
                    "Plate solve failed \u2014 no match", image, solveResult);
            }

            // The revealed pointing error (cone/polar/alignment) in arcminutes.
            var dRaHours = wcs.CenterRA - believed.RaJ2000;
            if (dRaHours > 12.0) dRaHours -= 24.0;
            else if (dRaHours < -12.0) dRaHours += 24.0;
            var cosDec = Math.Cos(believed.DecJ2000 * Math.PI / 180.0);
            var raErrArcmin = dRaHours * 15.0 * 60.0 * cosDec;
            var decErrArcmin = (wcs.CenterDec - believed.DecJ2000) * 60.0;
            var offsetArcmin = Math.Sqrt(raErrArcmin * raErrArcmin + decErrArcmin * decErrArcmin);

            // Solved J2000 -> mount native, same profile-transform route as the slew path.
            if (await mount.TryTransformJ2000ToMountNativeAsync(
                    transform, wcs.CenterRA, wcs.CenterDec, updateTime: false, cancellationToken) is not { } native)
            {
                logger?.LogWarning(
                    "Solve & sync: coordinate transform produced NaN. Mount={Mount} EquatorialSystem={System}",
                    mount.Name, mount.EquatorialSystem);
                return new SolveSyncOutcome(SolveSyncResult.SyncFailed,
                    $"Coordinate transform failed (EquatorialSystem={mount.EquatorialSystem})", image, solveResult);
            }

            try
            {
                await mount.SyncRaDecAsync(native.RaMount, native.DecMount, cancellationToken);
            }
            catch (OperationCanceledException oce)
            {
                logger?.LogInformation(oce, "Solve & sync cancelled during mount sync");
                image.Release();
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Solve & sync: SyncRaDec failed on {Mount}", mount.Name);
                return new SolveSyncOutcome(SolveSyncResult.SyncFailed, $"Mount sync failed: {ex.Message}", image, solveResult);
            }

            return new SolveSyncOutcome(SolveSyncResult.Synced,
                $"Synced: mount was {offsetArcmin:F1}' off \u2014 now at RA {wcs.CenterRA:F3}h Dec {wcs.CenterDec:F2}\u00B0",
                image, solveResult);
        }

        /// <summary>
        /// Pulse-guides the mount by an angular amount in a cardinal guide <paramref name="direction"/> -- the
        /// single mount-nudge actuator shared by the planetary COM recenter loop (the coarse, edge-blocked
        /// fallback) and the manual nudge buttons. The pulse duration is derived from the axis's guide rate
        /// (RA rate for East/West, Dec rate for North/South): <c>ms = arcsec / (rateDegPerSec * 3600) * 1000</c>,
        /// then clamped to [1 ms, <paramref name="maxPulse"/>] so a large requested move never issues an
        /// unbounded pulse. Best-effort: no-op when the mount is disconnected, can't pulse-guide, the request is
        /// non-positive, or the guide rate is unknown/zero (we can't size a pulse without it).
        /// </summary>
        /// <param name="mount">A connected mount supporting pulse-guide.</param>
        /// <param name="direction">Cardinal guide direction to pulse.</param>
        /// <param name="arcsec">Magnitude of the nudge in arcseconds (absolute value).</param>
        /// <param name="timeProvider">Time source (unused today; kept for symmetry + future settle waits).</param>
        /// <param name="maxPulse">Upper bound on the pulse duration (default 2 s).</param>
        /// <param name="logger">Optional logger for the sizing breadcrumb.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task PulseGuideArcsecAsync(
            IMountDriver mount,
            GuideDirection direction,
            double arcsec,
            ITimeProvider timeProvider,
            TimeSpan? maxPulse = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            _ = timeProvider;
            if (!mount.Connected || !mount.CanPulseGuide || !(arcsec > 0.0))
            {
                return;
            }

            var rateDegPerSec = direction is GuideDirection.East or GuideDirection.West
                ? await mount.GetGuideRateRightAscensionAsync(cancellationToken)
                : await mount.GetGuideRateDeclinationAsync(cancellationToken);

            var arcsecPerSec = rateDegPerSec * 3600.0;
            if (!(arcsecPerSec > 0.0))
            {
                logger?.LogDebug("Recenter mount pulse skipped: {Dir} guide rate is {Rate} deg/s.", direction, rateDegPerSec);
                return;
            }

            var ms = arcsec / arcsecPerSec * 1000.0;
            var cap = (maxPulse ?? TimeSpan.FromSeconds(2)).TotalMilliseconds;
            ms = Math.Clamp(ms, 1.0, cap);

            logger?.LogDebug(
                "Recenter mount pulse {Dir} {Arcsec:F1} arcsec -> {Ms:F0} ms (guide rate {Rate:F4} deg/s).",
                direction, arcsec, ms, rateDegPerSec);

            await mount.PulseGuideAsync(direction, TimeSpan.FromMilliseconds(ms), cancellationToken);
        }
    }
}
