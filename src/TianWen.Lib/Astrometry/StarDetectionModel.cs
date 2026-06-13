using System;

namespace TianWen.Lib.Astrometry
{
    /// <summary>
    /// Shared, pure star-detectability model: given an aperture, exposure, and detector noise,
    /// the faintest visual magnitude that clears a given SNR floor. Used by the synthetic fake
    /// camera (to decide which catalog stars to render) and by the session's first-scout
    /// obstruction oracle (to estimate an expected star count for a field). Keeping it in one
    /// place means the renderer and the oracle cannot drift apart.
    /// <para>
    /// Pure SNR-vs-read-noise model with no sky-background term (moonlight / light pollution lower
    /// real detection counts; callers that need a one-sided floor absorb that in their own margin).
    /// </para>
    /// </summary>
    public static class StarDetectionModel
    {
        /// <summary>
        /// Faintest visual magnitude that clears the detection floor for the given optics + exposure.
        /// </summary>
        /// <param name="apertureScaleFactor">Relative light-grasp, <c>(apertureMm / 50)^2</c> (a 50 mm
        /// aperture = 1.0). Larger optics push the cutoff fainter.</param>
        /// <param name="exposureSeconds">Exposure time in seconds (clamped to a tiny positive floor).</param>
        /// <param name="fwhmPixels">Star PSF FWHM in pixels (in-focus value).</param>
        /// <param name="readNoise">Read-noise sigma in ADU.</param>
        /// <param name="snrThreshold">SNR floor matching the detector. Default 5 matches
        /// <c>FindStarsAsync</c>'s <c>snrMin</c>; the scout oracle passes 10 to match its own scout.</param>
        /// <returns>Faintest visual magnitude that clears the detection floor.</returns>
        public static double DetectabilityMagCutoff(
            double apertureScaleFactor,
            double exposureSeconds,
            double fwhmPixels = 2.0,
            double readNoise = 5.0,
            double snrThreshold = 5.0)
        {
            var sigma = fwhmPixels / 2.3548;
            var peakFactor = 1.0 / (Math.PI * 2.0 * sigma * sigma); // peak ADU per unit total flux
            var fluxNormalizer = 10000.0 * Math.Max(exposureSeconds, 1e-3) * Math.Max(apertureScaleFactor, 1e-6);
            var minPeak = snrThreshold * readNoise;
            var minFlux = minPeak / peakFactor;
            return 5.0 - 2.5 * Math.Log10(minFlux / fluxNormalizer);
        }
    }
}
