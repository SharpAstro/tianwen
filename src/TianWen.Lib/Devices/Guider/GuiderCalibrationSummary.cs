using System;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// What a guider's calibration measured, as a value a caller can show or put on the wire (#1915): the two axes' directions
/// on the guide frame and how fast a guide pulse moves the star along each, the non-orthogonality of the pair, and the L the
/// star walked. The built-in guider's own result type is internal; this is what it says to everyone else.
/// </summary>
/// <param name="RaAngleDeg">The direction a west (RA) pulse moves the star on the frame, degrees from +X towards +Y.</param>
/// <param name="DecAngleDeg">The direction a north (Dec) pulse moves it, in the same convention.</param>
/// <param name="RaRateArcsecPerSec">How fast the RA axis moves the star at the guide rate, arcseconds per second.</param>
/// <param name="DecRateArcsecPerSec">The Dec axis's.</param>
/// <param name="RaRatePixPerSec">The same rate in frame pixels per second.</param>
/// <param name="DecRatePixPerSec">The Dec axis's, in pixels.</param>
/// <param name="OrthogonalityErrorDeg">How far the two axes are from being at a right angle on the frame: cone error and camera tilt show here.</param>
/// <param name="PixelScaleArcsec">The guide camera's pixel scale the rates were converted with, 1 when it is not known.</param>
/// <param name="DurationSeconds">How long the calibration took.</param>
/// <param name="RaDisplacementPx">How far the star walked along RA during the measurement, in pixels.</param>
/// <param name="DecDisplacementPx">And along Dec.</param>
/// <param name="Overlay">The L the star walked, drawn over the guide frame.</param>
public sealed record GuiderCalibrationSummary(
    double RaAngleDeg,
    double DecAngleDeg,
    double RaRateArcsecPerSec,
    double DecRateArcsecPerSec,
    double RaRatePixPerSec,
    double DecRatePixPerSec,
    double OrthogonalityErrorDeg,
    double PixelScaleArcsec,
    double DurationSeconds,
    double RaDisplacementPx,
    double DecDisplacementPx,
    CalibrationOverlayData? Overlay)
{
    internal static GuiderCalibrationSummary From(GuiderCalibrationResult result, double pixelScaleArcsec)
    {
        var separation = GuiderCalibration.FoldAngleDiffDeg(Math.Abs(result.DecAngleDeg - result.CameraAngleDeg) % 360.0);
        return new GuiderCalibrationSummary(
            result.CameraAngleDeg,
            result.DecAngleDeg,
            result.RaRatePixPerSec * pixelScaleArcsec,
            result.DecRatePixPerSec * pixelScaleArcsec,
            result.RaRatePixPerSec,
            result.DecRatePixPerSec,
            Math.Abs(separation - 90.0),
            pixelScaleArcsec,
            result.TotalCalibrationTimeSec,
            result.RaDisplacementPx,
            result.DecDisplacementPx,
            result.Overlay is { } overlay
                ? overlay with
                {
                    PixelScaleArcsec = pixelScaleArcsec,
                    RaRateArcsecPerSec = result.RaRatePixPerSec * pixelScaleArcsec,
                    DecRateArcsecPerSec = result.DecRatePixPerSec * pixelScaleArcsec,
                }
                : null);
    }
}

/// <summary>
/// What a guider is asked to start from outside a session (#1915): the star the user chose on the looped frame, and the
/// exposure the loop ran at, so guiding carries on from what was on screen.
/// </summary>
/// <param name="Star">The chosen star's position in guide-frame pixels (0-based); null lets the guider choose.</param>
/// <param name="Exposure">The exposure of each guide frame; null keeps the guider's own.</param>
public readonly record struct GuideStartOptions(
    (double X, double Y)? Star = null,
    TimeSpan? Exposure = null);
