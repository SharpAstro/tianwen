using System.Drawing;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Grades a planetary frame's sharpness -- the core of lucky imaging. Higher is sharper. Bad seeing
/// low-pass-filters a frame, so every estimator measures lost high-spatial-frequency content one way
/// or another. Two families are offered behind this interface (mirroring AutoStakkert's selectable
/// quality methods):
/// <list type="bullet">
///   <item><b>Spatial (default):</b> <see cref="LaplacianEnergyEstimator"/> (variance of the Laplacian --
///   the classic focus measure) and <see cref="GradientEnergyEstimator"/> (mean squared Sobel gradient).
///   One convolution pass; cheap enough for thousands of frames x per-AP.</item>
///   <item><b>Frequency (option, Phase 3):</b> an FFT high-band estimator, built once the 2D FFT lands
///   for phase-correlation alignment.</item>
/// </list>
/// <para>
/// <b>The noise trap:</b> sensor noise is broadband and inflates a naive high-frequency score (it rewards
/// noise, not detail). The mitigation that lives here is the <paramref name="region"/>: callers pass the
/// high-signal disk bounding box (see <see cref="PlanetaryDisk.BoundingBox"/>) so the noisy background is
/// never measured. Estimators additionally normalise for per-frame brightness so frames compare fairly.
/// </para>
/// </summary>
public interface IFrameQualityEstimator
{
    /// <summary>
    /// Scores <paramref name="frame"/>'s sharpness over <paramref name="region"/> (higher = sharper).
    /// Multi-channel frames (RGB, split-CFA) are scored on a per-pixel luminance proxy (the channel
    /// mean). Pass <see cref="Rectangle.Empty"/> to score the whole frame.
    /// </summary>
    float Score(Image frame, Rectangle region);
}
