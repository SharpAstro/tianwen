using System.Collections.Generic;
using System.Linq;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// The point sources a starless plate still holds: the faint stars its builder left under its detection threshold
/// (<see cref="StarlessPlateOptions.DetectionSigma"/>), and any it missed. A remover taught that every star goes takes them
/// too, and an eval that reads the plate as the truth then counts that as sky the remover moved
/// (docs/plans/star-remover-training.md, R2c's first check).
/// </summary>
public static class PlateSources
{
    /// <summary>The threshold a plate's sources are found at, in the matched filter's own noise: the builder's leftover
    /// threshold, under its detection threshold (5), so it reaches the stars the builder left. At 3 sigma a 256 px tile of
    /// pure noise gave about nine peaks, whose surroundings diluted a real leftover's sky; at 4 about one in five tiles
    /// gives one.</summary>
    public const float DefaultThresholdSigma = 4f;

    /// <summary>
    /// The point sources of a plate's <paramref name="luminance"/> at <paramref name="thresholdSigma"/>, found as the plate
    /// builder finds them (<see cref="PointSourceFinder"/>, a PSF of <paramref name="fwhm"/> pixels), brightest first.
    /// </summary>
    public static IReadOnlyList<(float X, float Y, float Significance)> Find(
        float[] luminance, int width, int height, BitMatrix? absent, double fwhm, float thresholdSigma = DefaultThresholdSigma)
    {
        var (sources, _) = PointSourceFinder.Find(luminance, width, height, absent, fwhm, thresholdSigma);
        return [.. sources.Select(static s => (s.X, s.Y, s.Significance))];
    }
}
