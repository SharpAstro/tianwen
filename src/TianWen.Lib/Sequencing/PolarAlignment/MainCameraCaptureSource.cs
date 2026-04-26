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
        public MainCameraCaptureSource(
            ICameraDriver camera,
            string displayName,
            double focalLengthMm,
            double apertureMm,
            ITimeProvider timeProvider,
            ILogger logger)
        {
            _camera = camera;
            DisplayName = displayName;
            FocalLengthMm = focalLengthMm;
            ApertureMm = apertureMm;
            // Square-pixel cameras are universal in this domain; X is enough.
            PixelSizeMicrons = camera.PixelSizeX;
            _timeProvider = timeProvider;
            _logger = logger;
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
                var solveResult = await solver.SolveImageAsync(image, cancellationToken: ct);
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
