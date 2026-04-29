using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing.PolarAlignment
{
    /// <summary>
    /// Capture source backed by an <see cref="ICameraDriver"/>: starts a
    /// single exposure, polls until the frame is ready, hands the resulting
    /// <see cref="Image"/> to the plate solver, and projects the WCS centre
    /// to a J2000 unit vector.
    ///
    /// Uses <see cref="IPlateSolver.SolveImageAsync"/> (which writes a temp
    /// FITS internally) — no separate FITS-write step here. If
    /// <see cref="PolarAlignmentConfiguration.SaveFrames"/> is enabled, the
    /// caller is responsible for copying the temp file out before the solver
    /// deletes it. Phase 2 keeps frame archiving deferred (mirrors
    /// <c>SaveScoutFrames</c> deferral pattern).
    /// </summary>
    internal sealed class MainCameraCaptureSource : ICaptureSource
    {
        private readonly ICameraDriver _camera;
        private readonly string _otaName;
        private readonly int _otaFocalLengthMm;
        private readonly int? _otaApertureMm;
        private readonly IFocuserDriver? _focuser;
        private readonly IFilterWheelDriver? _filterWheel;
        private readonly IMountDriver? _mount;
        private readonly string _targetName;
        private readonly ICelestialObjectDB? _catalogDb;
        private readonly ITimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly Action<Image>? _onFrameCaptured;
        private readonly Action<PlateSolveResult>? _onFrameSolved;
        private readonly TimeSpan _imageReadyPollInterval = TimeSpan.FromMilliseconds(50);

        public string DisplayName { get; }
        public double FocalLengthMm { get; }
        public double ApertureMm { get; }
        public double PixelSizeMicrons { get; }

        /// <summary>
        /// Construct from a connected camera driver plus OTA optics + the
        /// devices the camera frame needs to reference at exposure time.
        /// All denorm stamping (telescope name, focal length, aperture, site,
        /// focuser position, filter, mount-current Target, catalog DB) is
        /// delegated to <see cref="CameraExposureActions.StampDenormAsync"/>
        /// so this source matches the imaging session and live preview path
        /// exactly -- no per-call-site drift.
        /// </summary>
        /// <param name="camera">Connected camera.</param>
        /// <param name="displayName">Label shown in UI (e.g. "OTA #1 — Askar 71F").</param>
        /// <param name="focalLengthMm">OTA focal length, used both for FITS
        ///   denorm and for the auto-selection ranking.</param>
        /// <param name="apertureMm">OTA aperture in mm, same.</param>
        /// <param name="otaName">OTA / telescope name, FITS: TELESCOP.</param>
        /// <param name="focuser">OTA focuser if connected; null skips the stamp.</param>
        /// <param name="filterWheel">OTA filter wheel if connected; null skips the stamp.</param>
        /// <param name="mount">Connected mount. Required for per-capture Target
        ///   refresh -- frame 2 fires after a Phase A rotation, so a one-shot
        ///   stamp won't do; the mount provides current pointing per frame.
        ///   Also feeds the search-origin hint into the plate solver.</param>
        /// <param name="targetName">Name written to Target alongside RA/Dec
        ///   (e.g. "Polar Align"). FITS: OBJECT.</param>
        /// <param name="catalogDb">Catalog DB for FakeCameraDriver synthetic
        ///   star rendering. Without this, fake cameras produce a random star
        ///   field that no plate solver can match. Pass null on real-only setups.</param>
        /// <param name="onFrameCaptured">Optional callback fired after each
        ///   exposure with the captured <see cref="Image"/>, before the
        ///   plate-solve attempt. The polar-align UI wires this to publish into
        ///   <c>LiveSessionState.LastCapturedImages</c> so the mini viewer can
        ///   render the live frame during the multi-rung probing ramp -- without
        ///   it the user sees a black frame for the entire 5-30s solve work and
        ///   thinks the routine is hung. Receiving this callback also opts out
        ///   of the source's automatic <see cref="Image.Release"/>: ownership
        ///   moves to the consumer, which holds the image until the next frame
        ///   replaces it. The plate solver still runs against the same image
        ///   reference, so the consumer must not mutate it -- treat the
        ///   reference as read-only for the lifetime of the polar routine.
        /// <param name="onFrameSolved">Optional callback fired after each
        ///   plate solve, regardless of success/failure. UIs wire this to
        ///   refresh <c>PreviewPlateSolveResult</c> so the WCS-anchored mini
        ///   viewer chrome (grid overlay, sky markers) tracks the live frame.
        ///   Skipped when the solver throws -- those frames are reported via
        ///   <see cref="PlateSolveResult.Solution"/> = null on the next
        ///   successful capture instead.</param>
        public MainCameraCaptureSource(
            ICameraDriver camera,
            string displayName,
            double focalLengthMm,
            double apertureMm,
            string otaName,
            IFocuserDriver? focuser,
            IFilterWheelDriver? filterWheel,
            IMountDriver? mount,
            string targetName,
            ICelestialObjectDB? catalogDb,
            ITimeProvider timeProvider,
            ILogger logger,
            Action<Image>? onFrameCaptured = null,
            Action<PlateSolveResult>? onFrameSolved = null)
        {
            _camera = camera;
            DisplayName = displayName;
            FocalLengthMm = focalLengthMm;
            ApertureMm = apertureMm;
            _otaName = otaName;
            _otaFocalLengthMm = (int)Math.Round(focalLengthMm);
            _otaApertureMm = apertureMm > 0 ? (int)Math.Round(apertureMm) : null;
            _focuser = focuser;
            _filterWheel = filterWheel;
            _mount = mount;
            _targetName = targetName;
            _catalogDb = catalogDb;
            // Square-pixel cameras are universal in this domain; X is enough.
            PixelSizeMicrons = camera.PixelSizeX;
            _timeProvider = timeProvider;
            _logger = logger;
            _onFrameCaptured = onFrameCaptured;
            _onFrameSolved = onFrameSolved;
        }

        public async ValueTask<CaptureResult> CaptureAsync(
            TimeSpan exposure,
            CancellationToken ct = default)
        {
            if (!_camera.Connected)
            {
                _logger.LogWarning("MainCameraCaptureSource: camera not connected");
                return new CaptureResult(false, null, false, null, exposure, null);
            }

            // Per-stage timing -- the polar-refine RefineAsync log shows the total
            // capture time but not where the 4-5x exposure-time overhead comes
            // from. Stamp / poll-wait / get-image / callback cover the whole hot
            // path; whichever is largest is the next perf target.
            var stampStart = System.Diagnostics.Stopwatch.GetTimestamp();

            // Stamp denorm fields (telescope, focal length, aperture, site,
            // focuser, filter, Target from current mount pointing, catalog DB)
            // before each exposure -- the per-frame Target refresh is what
            // lets fake cameras render real catalog stars at the rotated
            // coordinates after a Phase A nudge, and what feeds OBJCTRA /
            // OBJCTDEC into the FITS header.
            await CameraExposureActions.StampDenormAsync(
                _camera,
                _otaName,
                _otaFocalLengthMm,
                _otaApertureMm,
                _focuser,
                _filterWheel,
                _mount,
                _targetName,
                _catalogDb,
                _logger,
                ct).ConfigureAwait(false);
            var stampElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(stampStart);

            // Search-origin hint for the plate solver. Built-in
            // CatalogPlateSolver isn't a blind solver and ASTAP is
            // dramatically faster with a hint -- the mount knows where
            // it's pointing, withholding that from the solver makes no
            // sense.
            WCS? searchOrigin = _camera.Target is { } tgt ? new WCS(tgt.RA, tgt.Dec) : null;

            await _camera.StartExposureAsync(exposure, FrameType.Light, ct);

            // Poll for image-ready. Time budget is exposure + 5s for download/digest.
            // The driver may extend the actual exposure (e.g. CMOS rolling shutter),
            // so we pad rather than fail strictly at exposure end.
            var pollStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var timeout = exposure + TimeSpan.FromSeconds(5);
            var deadline = _timeProvider.GetTimestamp() + (long)(timeout.TotalSeconds * _timeProvider.TimestampFrequency);
            while (_timeProvider.GetTimestamp() < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await _camera.GetImageReadyAsync(ct)) break;
                await _timeProvider.SleepAsync(_imageReadyPollInterval, ct);
            }
            var pollElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(pollStart);

            var getImageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var image = await _camera.GetImageAsync(ct);
            var getImageElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(getImageStart);
            if (image is null)
            {
                _logger.LogWarning("MainCameraCaptureSource: GetImageAsync returned null after exposure {Exposure}ms", exposure.TotalMilliseconds);
                return new CaptureResult(false, null, false, searchOrigin, exposure, null);
            }

            // Publish the frame to the UI BEFORE the orchestrator solves it so
            // the live preview updates as each rung fires -- a solver call can
            // take 5-30s on an underexposed early rung, and a stale black
            // frame in the mini viewer makes the routine feel hung. With the
            // callback wired, ownership of the image transfers to the
            // consumer; the caller of CaptureAsync MUST NOT release it.
            var cbStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var transferredOwnership = false;
            if (_onFrameCaptured is { } cb)
            {
                try
                {
                    cb(image);
                    transferredOwnership = true;
                }
                catch (Exception ex)
                {
                    // Don't let a UI bug abort the polar routine -- log and let
                    // the caller take the regular release-after-use path.
                    _logger.LogWarning(ex, "MainCameraCaptureSource: onFrameCaptured callback threw -- continuing without preview publish");
                }
            }
            var cbElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(cbStart);

            _logger.LogInformation(
                "MainCameraCaptureSource: exposure={ExposureMs:F0}ms stamp={StampMs:F0}ms poll={PollMs:F0}ms getImage={GetImageMs:F0}ms callback={CbMs:F0}ms",
                exposure.TotalMilliseconds, stampElapsed.TotalMilliseconds,
                pollElapsed.TotalMilliseconds, getImageElapsed.TotalMilliseconds, cbElapsed.TotalMilliseconds);

            return new CaptureResult(
                Success: true,
                Image: image,
                OwnershipTransferredToUi: transferredOwnership,
                SearchOrigin: searchOrigin,
                ExposureUsed: exposure,
                FitsPath: null);
        }

        public async ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(
            TimeSpan exposure,
            IPlateSolver solver,
            CancellationToken ct = default)
        {
            var captureStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var capture = await CaptureAsync(exposure, ct).ConfigureAwait(false);
            var captureElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(captureStart);
            if (!capture.Success || capture.Image is not { } image)
            {
                _logger.LogInformation(
                    "MainCameraCaptureSource.CaptureAndSolve: exposure={ExposureMs:F0}ms capture={CaptureMs:F0}ms (capture failed, no solve attempted)",
                    exposure.TotalMilliseconds, captureElapsed.TotalMilliseconds);
                return new CaptureAndSolveResult(false, null, default, 0, exposure, null, capture.FailureReason);
            }

            try
            {
                PlateSolveResult solveResult;
                var solveStart = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    solveResult = await solver.SolveImageAsync(image, searchOrigin: capture.SearchOrigin, cancellationToken: ct);
                }
                catch (PlateSolverException ex)
                {
                    var solveElapsedFail = System.Diagnostics.Stopwatch.GetElapsedTime(solveStart);
                    // Per-frame solve failure (no stars, ASTAP exit-1, etc.) is the expected
                    // outcome of an under-exposed first rung in the adaptive exposure ramp.
                    // Return Success=false so the ramp moves to the next rung instead of
                    // crashing the whole routine on the very first 100ms attempt.
                    _logger.LogInformation(
                        "MainCameraCaptureSource.CaptureAndSolve: exposure={ExposureMs:F0}ms capture={CaptureMs:F0}ms solve={SolveMs:F0}ms (PlateSolverException -- ramp will try next rung)",
                        exposure.TotalMilliseconds, captureElapsed.TotalMilliseconds, solveElapsedFail.TotalMilliseconds);
                    _logger.LogDebug(ex, "MainCameraCaptureSource: plate solver threw at exposure {Exposure}ms", exposure.TotalMilliseconds);
                    return new CaptureAndSolveResult(false, null, default, 0, exposure, null);
                }
                var solveElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(solveStart);
                _logger.LogInformation(
                    "MainCameraCaptureSource.CaptureAndSolve: exposure={ExposureMs:F0}ms capture={CaptureMs:F0}ms solve={SolveMs:F0}ms solved={Solved} matched={Matched}",
                    exposure.TotalMilliseconds, captureElapsed.TotalMilliseconds, solveElapsed.TotalMilliseconds,
                    solveResult.Solution is not null, solveResult.MatchedStars);

                // Publish the solve result whether successful or not so the
                // UI's WCS-anchored chrome (grid, sky markers) tracks the live
                // frame on every iteration, not just the rare ones that match
                // -- the consumer decides what to do with a null Solution.
                if (_onFrameSolved is { } onSolved)
                {
                    try { onSolved(solveResult); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "MainCameraCaptureSource: onFrameSolved callback threw");
                    }
                }

                if (solveResult.Solution is { } wcs)
                {
                    var centre = PolarAxisSolver.RaDecToUnitVec(wcs.CenterRA, wcs.CenterDec);
                    return new CaptureAndSolveResult(
                        Success: true,
                        Wcs: wcs,
                        WcsCenter: centre,
                        StarsMatched: solveResult.MatchedStars,
                        ExposureUsed: exposure,
                        FitsPath: null);
                }
                return new CaptureAndSolveResult(false, null, default, 0, exposure, null);
            }
            finally
            {
                if (!capture.OwnershipTransferredToUi)
                {
                    image.Release();
                    _camera.ReleaseImageData();
                }
            }
        }
    }
}
