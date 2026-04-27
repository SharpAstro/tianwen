using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;

namespace TianWen.Lib.Sequencing.PolarAlignment
{
    /// <summary>
    /// Unifies main-camera and guider-camera capture for the polar-alignment
    /// routine. The orchestrator drives the adaptive exposure ramp and Phase B
    /// refinement loop through this interface; concrete implementations wrap
    /// either an <see cref="Devices.ICameraDriver"/> or
    /// <see cref="Devices.Guider.IGuider"/>.
    /// </summary>
    /// <remarks>
    /// Optics properties (<see cref="FocalLengthMm"/>, <see cref="ApertureMm"/>,
    /// <see cref="PixelSizeMicrons"/>) feed
    /// <see cref="CaptureSourceRanker"/> when the user has more than one
    /// candidate source connected. They also feed the bring-it-back overlay
    /// computation in the GUI tab — pixel scale gates the WCS-based projection
    /// of the apparent pole onto the live frame.
    /// </remarks>
    internal interface ICaptureSource
    {
        /// <summary>Human-readable label shown in the GUI source dropdown.</summary>
        string DisplayName { get; }

        /// <summary>OTA focal length in millimetres (driver-reported, falls back to profile).</summary>
        double FocalLengthMm { get; }

        /// <summary>OTA aperture in millimetres.</summary>
        double ApertureMm { get; }

        /// <summary>Sensor pixel size in microns (square pixels assumed; Y == X for all supported sources).</summary>
        double PixelSizeMicrons { get; }

        /// <summary>Convenience: f-ratio = focal length / aperture. Smaller is faster.</summary>
        double FRatio => FocalLengthMm / System.Math.Max(ApertureMm, 1.0);

        /// <summary>Convenience: image scale in arcsec/pixel. 206.265 * pixelSize / focalLength.</summary>
        double PixelScaleArcsecPerPx => 206.265 * PixelSizeMicrons / System.Math.Max(FocalLengthMm, 1.0);

        /// <summary>
        /// Capture a single frame at the requested exposure and run the supplied
        /// plate solver against it. Returns whether the solve succeeded along
        /// with the WCS, matched-star count, and the actual exposure used (which
        /// may differ from <paramref name="exposure"/> if the source clamped it).
        /// </summary>
        ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(
            System.TimeSpan exposure,
            IPlateSolver solver,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Outcome of a single capture-and-solve attempt by an
    /// <see cref="ICaptureSource"/>. Drives the adaptive exposure ramp
    /// (<see cref="StarsMatched"/> gates whether to advance to the next
    /// rung), the Phase A solve geometry (<see cref="WcsCenter"/> gives v1 / v2),
    /// and the GUI status indicator (<see cref="ExposureUsed"/>,
    /// <see cref="StarsMatched"/> shown alongside the live frame).
    /// </summary>
    /// <param name="Success">True iff the plate solver returned a WCS solution.</param>
    /// <param name="Wcs">The WCS solution, or null on failure. Caller projects
    /// frame-centre pixel through this to get J2000 RA/Dec.</param>
    /// <param name="WcsCenter">Frame-centre J2000 unit vector convenience field;
    /// <see cref="Vec3.Length"/> is 0 on failure.</param>
    /// <param name="StarsMatched">Number of catalog/projected stars matched in
    /// the solve. Drives the ramp success gate.</param>
    /// <param name="ExposureUsed">The actual exposure the source took. May be
    /// shorter than requested if the source clamped against an internal max.</param>
    /// <param name="FitsPath">Path to the saved FITS frame, or null if the
    /// source did not save one (e.g. main-camera mode with frame-saving disabled).</param>
    /// <param name="FailureReason">Optional human-readable reason when
    /// <see cref="Success"/> is false. Lets sources surface configuration-class
    /// errors (e.g. PHD2 "Save Images" disabled) the orchestrator can show to the
    /// user instead of its generic "plate solve failed at every rung" message.</param>
    internal readonly record struct CaptureAndSolveResult(
        bool Success,
        WCS? Wcs,
        Vec3 WcsCenter,
        int StarsMatched,
        System.TimeSpan ExposureUsed,
        string? FitsPath,
        string? FailureReason = null);
}
