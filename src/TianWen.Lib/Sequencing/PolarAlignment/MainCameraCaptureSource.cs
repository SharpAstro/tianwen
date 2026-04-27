using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
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
        private readonly ITimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly Func<CancellationToken, ValueTask<Target?>>? _refreshTargetAsync;
        private readonly TimeSpan _imageReadyPollInterval = TimeSpan.FromMilliseconds(50);

        public string DisplayName { get; }
        public double FocalLengthMm { get; }
        public double ApertureMm { get; }
        public double PixelSizeMicrons { get; }

        /// <summary>
        /// Construct from a connected camera driver plus the OTA optics that
        /// drive the auto-selection ranking. The optics aren't always
        /// driver-discoverable (the camera doesn't know the scope it's bolted
        /// to), so we accept them explicitly. Sourced from
        /// <c>OTA.FocalLength</c> / <c>OTA.Aperture</c> / camera pixel size at
        /// the call site.
        /// </summary>
        /// <param name="refreshTargetAsync">Optional callback invoked before each
        /// <see cref="ICameraDriver.StartExposureAsync"/> to refresh
        /// <see cref="ICameraDriver.Target"/> with the current pointing. Real
        /// cameras include the result in FITS headers; fake cameras use it to
        /// render real catalog stars (so plate solvers can match them) — without
        /// this, polar alignment on a FakeCameraDriver runs against a random
        /// synthetic field that no solver can match. Pass null on real-only
        /// setups to keep whatever Target the camera was previously assigned.</param>
        public MainCameraCaptureSource(
            ICameraDriver camera,
            string displayName,
            double focalLengthMm,
            double apertureMm,
            ITimeProvider timeProvider,
            ILogger logger,
            Func<CancellationToken, ValueTask<Target?>>? refreshTargetAsync = null)
        {
            _camera = camera;
            DisplayName = displayName;
            FocalLengthMm = focalLengthMm;
            ApertureMm = apertureMm;
            // Square-pixel cameras are universal in this domain; X is enough.
            PixelSizeMicrons = camera.PixelSizeX;
            _timeProvider = timeProvider;
            _logger = logger;
            _refreshTargetAsync = refreshTargetAsync;
        }

        public async ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(
            TimeSpan exposure,
            IPlateSolver solver,
            CancellationToken ct)
        {
            if (!_camera.Connected)
            {
                _logger.LogWarning("MainCameraCaptureSource: camera not connected");
                return new CaptureAndSolveResult(false, null, default, 0, exposure, null);
            }

            // Refresh the camera's Target to the current mount pointing before
            // exposing — drives the FakeCameraDriver synthetic-catalog render path
            // (Phase A frame 2 captures at a different RA after the rotation, so
            // a one-shot stamp before the routine isn't enough). On real cameras
            // this just feeds the FITS header.
            //
            // The refreshed pointing is also fed into the plate solver as a
            // search origin: the built-in CatalogPlateSolver requires a hint to
            // attempt a match (it's not a blind solver), and even ASTAP solves
            // dramatically faster with one. The mount already knows where it's
            // pointing — withholding that from the solver makes no sense.
            WCS? searchOrigin = null;
            if (_refreshTargetAsync is not null
                && await _refreshTargetAsync(ct).ConfigureAwait(false) is { } refreshed)
            {
                _camera.Target = refreshed;
                searchOrigin = new WCS(refreshed.RA, refreshed.Dec);
            }

            await _camera.StartExposureAsync(exposure, FrameType.Light, ct);

            // Poll for image-ready. Time budget is exposure + 5s for download/digest.
            // The driver may extend the actual exposure (e.g. CMOS rolling shutter),
            // so we pad rather than fail strictly at exposure end.
            var timeout = exposure + TimeSpan.FromSeconds(5);
            var deadline = _timeProvider.GetTimestamp() + (long)(timeout.TotalSeconds * _timeProvider.TimestampFrequency);
            while (_timeProvider.GetTimestamp() < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await _camera.GetImageReadyAsync(ct)) break;
                await _timeProvider.SleepAsync(_imageReadyPollInterval, ct);
            }

            var image = await _camera.GetImageAsync(ct);
            if (image is null)
            {
                _logger.LogWarning("MainCameraCaptureSource: GetImageAsync returned null after exposure {Exposure}ms", exposure.TotalMilliseconds);
                return new CaptureAndSolveResult(false, null, default, 0, exposure, null);
            }

            try
            {
                PlateSolveResult solveResult;
                try
                {
                    solveResult = await solver.SolveImageAsync(image, searchOrigin: searchOrigin, cancellationToken: ct);
                }
                catch (PlateSolverException ex)
                {
                    // Per-frame solve failure (no stars, ASTAP exit-1, etc.) is the expected
                    // outcome of an under-exposed first rung in the adaptive exposure ramp.
                    // Return Success=false so the ramp moves to the next rung instead of
                    // crashing the whole routine on the very first 100ms attempt.
                    _logger.LogDebug(ex, "MainCameraCaptureSource: plate solver threw at exposure {Exposure}ms — ramp will try next rung", exposure.TotalMilliseconds);
                    return new CaptureAndSolveResult(false, null, default, 0, exposure, null);
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
                image.Release();
                _camera.ReleaseImageData();
            }
        }
    }
}
