using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;

namespace TianWen.Lib.Sequencing.PolarAlignment
{
    /// <summary>
    /// Capture source backed by an <see cref="IGuider"/> (built-in or PHD2).
    /// Drives one frame via <see cref="IGuider.LoopAsync"/> + saves it via
    /// <see cref="IGuider.SaveImageAsync"/>, then plate-solves the on-disk
    /// FITS with <see cref="IPlateSolver.SolveFileAsync"/>.
    ///
    /// PHD2 prerequisite: <i>Save Images</i> must be enabled in the PHD2
    /// profile, otherwise <see cref="IGuider.SaveImageAsync"/> returns null.
    /// This is the existing <c>IGuider</c> contract; the polar-alignment
    /// routine surfaces "Enable Save Images in PHD2" as the failure reason
    /// when it returns null.
    /// </summary>
    internal sealed class GuiderCaptureSource : ICaptureSource
    {
        private readonly IGuider _guider;
        private readonly IExternal _external;
        private readonly ILogger _logger;
        private readonly string _frameFolder;

        public string DisplayName { get; }
        public double FocalLengthMm { get; }
        public double ApertureMm { get; }
        public double PixelSizeMicrons { get; }

        /// <summary>
        /// Construct from a connected guider plus its OTA optics. Frame folder
        /// is the directory where <see cref="IGuider.SaveImageAsync"/> writes
        /// each captured frame; pass an <see cref="IExternal"/>-resolved per-run
        /// scratch directory.
        /// </summary>
        public GuiderCaptureSource(
            IGuider guider,
            string displayName,
            double focalLengthMm,
            double apertureMm,
            double pixelSizeMicrons,
            IExternal external,
            ILogger logger)
        {
            _guider = guider;
            DisplayName = displayName;
            FocalLengthMm = focalLengthMm;
            ApertureMm = apertureMm;
            PixelSizeMicrons = pixelSizeMicrons;
            _external = external;
            _logger = logger;
            _frameFolder = Path.Combine(Path.GetTempPath(), "TianWen", "PolarAlignment", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(_frameFolder);
        }

        public async ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(
            TimeSpan exposure,
            IPlateSolver solver,
            CancellationToken ct)
        {
            if (!_guider.Connected)
            {
                _logger.LogWarning("GuiderCaptureSource: guider not connected");
                return new CaptureAndSolveResult(false, null, default, 0, exposure, null);
            }

            // LoopAsync drives the guider through one capture cycle. Timeout matches
            // the exposure plus a generous margin for PHD2 RPC + save-to-disk latency.
            var loopTimeout = exposure + TimeSpan.FromSeconds(3);
            if (!await _guider.LoopAsync(loopTimeout, ct))
            {
                _logger.LogWarning("GuiderCaptureSource: LoopAsync did not complete within {Timeout}", loopTimeout);
                return new CaptureAndSolveResult(false, null, default, 0, exposure, null,
                    FailureReason: $"Guider did not produce a frame within {loopTimeout.TotalSeconds:F0}s.");
            }

            string? fitsPath = null;
            try
            {
                fitsPath = await _guider.SaveImageAsync(_frameFolder, ct);
            }
            catch (GuiderException ex)
            {
                _logger.LogWarning(ex, "GuiderCaptureSource: SaveImageAsync threw — PHD2 'Save Images' may be disabled");
                return new CaptureAndSolveResult(false, null, default, 0, exposure, null,
                    FailureReason: "Guider rejected save-image request \u2014 enable 'Save Images' in the PHD2 profile.");
            }
            if (string.IsNullOrEmpty(fitsPath))
            {
                _logger.LogWarning("GuiderCaptureSource: SaveImageAsync returned no path — enable 'Save Images' in PHD2 profile");
                return new CaptureAndSolveResult(false, null, default, 0, exposure, null,
                    FailureReason: "Guider produced no frame on disk \u2014 enable 'Save Images' in the PHD2 profile.");
            }

            try
            {
                var solveResult = await solver.SolveFileAsync(fitsPath, cancellationToken: ct);
                if (solveResult.Solution is { } wcs)
                {
                    var centre = PolarAxisSolver.RaDecToUnitVec(wcs.CenterRA, wcs.CenterDec);
                    return new CaptureAndSolveResult(
                        Success: true,
                        Wcs: wcs,
                        WcsCenter: centre,
                        StarsMatched: solveResult.MatchedStars,
                        ExposureUsed: exposure,
                        FitsPath: fitsPath);
                }
                return new CaptureAndSolveResult(false, null, default, 0, exposure, fitsPath);
            }
            finally
            {
                // Clean up unless the orchestrator explicitly opts in to keeping frames
                // (a future SaveFrames toggle moves them to a permanent folder before this).
                if (File.Exists(fitsPath))
                {
                    try { File.Delete(fitsPath); }
                    catch (IOException ex) { _logger.LogDebug(ex, "Failed to delete temp guider frame {Path}", fitsPath); }
                }
            }
        }
    }
}
