using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// The single implementation of the star-quad registration primitives, shared by
/// <see cref="StackingPipeline"/> and the dataset builder's <c>SessionRegistrar</c>.
///
/// <para><b>Why this exists.</b> Those two were parallel implementations of one algorithm
/// (measure -> gate -> pick reference -> quad-match -> refine -> integrate), and they drifted in
/// BOTH directions, each drift costing real damage before anyone noticed:</para>
/// <list type="bullet">
/// <item>The dataset path moved detection onto the pre-debayer luminance and capped the quad set at
/// the bright end, deliberately leaving stacking alone. Measured on a real session, at quad-forming
/// depths the debayered-channel-0 route could not produce even 20 mutual matches between
/// consecutive subs while the pre-debayer route reproduced at 92%.</item>
/// <item>Stacking learned to mask hot pixels from the dark after they survived visibly into a
/// master. The dataset path never got it, and wrote hot-pixel clusters into 45 of 64 masters of the
/// AI training set, where they are far harder to notice and are learned by the model.</item>
/// <item><c>TryMatchAsync</c>, the tolerance ladder, <c>MinStarsForMatch</c> and the reference score
/// were four verbatim copies, three of them documented as copies rather than shared.</item>
/// </list>
///
/// <para>The asymmetry is the point: a drift on the stacking path shows up in one master and gets
/// noticed, while a drift on the dataset path silently poisons every model trained afterwards. So
/// the primitives live here, once, and a change to registration behaviour is a change to both
/// consumers by construction.</para>
///
/// <para>What legitimately stays per-consumer: how frames are GROUPED (session identity vs light
/// group), what is done with the result (tiles + half-masters vs plate-solve + preview), and two
/// policy knobs (the quality gate's keep-floor, and whether a matched dark is demanded before
/// drizzling). Everything else belongs here.</para>
/// </summary>
public static class FrameRegistration
{
    /// <summary>
    /// Minimum detected stars for a stable quad-invariant fit. Matches the matcher's internal
    /// <c>minStars/4 = 6</c> quad-correspondence floor with headroom.
    /// </summary>
    public const int MinStarsForMatch = 24;

    /// <summary>
    /// Top-K brightest stars that form quad fingerprints.
    /// <para>100, not the 500 the stacker used to pass, and the reason is combinatorial rather than
    /// a matter of taste. A quad matches only when the same four stars form it in both frames, so
    /// with a fraction p of the top-K detections real, at most about p^4 of quads can match, and p
    /// falls with depth. Measured on Helix 2025-08-09: 68% at top-50, 59% at top-100, 41% at
    /// top-200, 32% over all 601 detections. At 500 the faint tail dominates the fingerprint set and
    /// the session registered 0 of 313 subs; at 100 it registered 314 of 314.</para>
    /// </summary>
    public const int DefaultQuadStars = 100;

    /// <summary>
    /// Quad-match tolerance ladder, tight first, first match wins.
    /// <para>The lower rungs suit a dense all-stars quad set where small drift only nudges the
    /// invariants fractionally; the top-K path has ~20x fewer quads and a much sparser signature
    /// space, so cross-flip frames typically match at 0.1-0.2. The 0.5 ceiling is a runaway guard,
    /// and false-positive cross-object pairs are still rejected downstream by the affine validator
    /// plus RANSAC min-inlier=4 even there.</para>
    /// </summary>
    public static readonly ImmutableArray<float> QuadTolerances = [0.008f, 0.02f, 0.05f, 0.1f, 0.2f, 0.5f];

    /// <summary>
    /// Composite reference-frame quality score: rewards sharp, round and many simultaneously.
    /// <para>Picking by star count alone (which this replaced) let low-altitude bloated early frames
    /// win on dense fields even when their PSF was 30% broader, which is a bad reference for the
    /// rest of the session to register against. A frame with 10000 stars loses to one with 5000
    /// whenever the HFD difference exceeds 2x, and elongation is penalised regardless of count
    /// (factor 5 at ecc 1.0, factor 3 at ecc 0.5).</para>
    /// </summary>
    public static float ReferenceScore(in FrameMetrics metrics)
        => metrics.StarCount / (MathF.Max(metrics.MedianHfd, 1f) * (1f + 4f * metrics.MedianEllipticity));

    /// <summary>Per-frame PSF medians from an already-detected star list. One pass, no re-detection.</summary>
    public static FrameMetrics MetricsFrom(StarList stars) => new(
        MedianHfd: stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median),
        MedianFwhm: stars.MapReduceStarProperty(SampleKind.FWHM, AggregationMethod.Median),
        MedianEllipticity: stars.MapReduceStarProperty(SampleKind.Ellipticity, AggregationMethod.Median),
        StarCount: stars.Count);

    /// <summary>
    /// Detects registration stars on a calibrated frame, and returns the debayered frame alongside
    /// for warping. This is the ONE detect site; both pipelines route through it.
    ///
    /// <para><b>Detection runs on the PRE-DEBAYER image</b>, so <see cref="Image.FindStarsAsync"/>
    /// takes its own <c>BilinearMono</c> path for an RGGB frame and folds all four photosites into
    /// one luminance plane. Detecting on a channel of a colour-debayered frame is what this
    /// replaced, and it is wrong for a reason that does not show up on a star-rich field: R and B
    /// are quarter-density planes, so interpolation manufactures ~1000 spurious detections per frame
    /// that pass the only size floor because the kernel smooths them into plausible round blobs.
    /// Mono also keeps the choice independent of the filter, which a per-channel pick cannot: under
    /// a dual or quad-band filter the green photosites carry OIII plus continuum while red carries
    /// Ha alone. Coordinates are unaffected, <c>BilinearMono</c> is full-resolution on the same
    /// grid.</para>
    ///
    /// <para><b>The debayer happens FIRST and that ordering is load-bearing</b>, not incidental
    /// tidiness: <see cref="Image.DebayerAsync"/> can rescale its input in place, so it participates
    /// in what the subsequent pre-debayer detection sees. The measured 314/314 result was obtained
    /// with it in this order; do not reorder without re-measuring.</para>
    /// </summary>
    public static async Task<(StarList Stars, Image Debayered)> DetectAsync(
        Image calibrated,
        DebayerAlgorithm debayerAlgorithm,
        float snrMin,
        int minStars,
        CancellationToken cancellationToken = default)
    {
        var debayered = await calibrated.DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken);
        var stars = await calibrated.FindStarsAsync(
            channel: 0, snrMin: snrMin, minStars: minStars, cancellationToken: cancellationToken);
        return (stars, debayered);
    }

    /// <summary>
    /// Quad match across <see cref="QuadTolerances"/>, tight first, loosening on failure.
    /// <c>FindFitAsync</c> memoises the quad build per <paramref name="quadStars"/> key, so a looser
    /// retry only re-runs the match pass, not the expensive quad construction.
    /// </summary>
    /// <returns>The affine solution and the tolerance it matched at, or a null solution with NaN
    /// diagnostics when no rung fits.</returns>
    public static async Task<(Matrix3x2? Solution, float QuadTolerance, float RmsResidualPx)> TryMatchAsync(
        SortedStarList light, SortedStarList reference, int quadStars)
    {
        foreach (var tolerance in QuadTolerances)
        {
            var (solution, rmsPx) = await light.FindOffsetAndRotationWithRmsAsync(
                reference, minimumCount: 6, quadTolerance: tolerance, maxStars: quadStars);
            if (solution is not null)
            {
                return (solution, tolerance, rmsPx);
            }
        }
        return (null, float.NaN, float.NaN);
    }

    /// <summary>
    /// Debayers a calibrated frame and warps it onto the union canvas, returning the warped frame
    /// AND the canvas-space transform (the dataset side persists that per sub). These three lines
    /// were byte-identical in both pipelines -- their only divergence was what happens to the
    /// result (the stacker yields it to a streaming integrator, the registrar writes a scratch
    /// FITS) -- and a divergence HERE would be silent behavioural drift between the master a user
    /// sees and the master a model trains on, the exact class of drift this file exists to end.
    /// </summary>
    public static async Task<(Image Warped, Matrix3x2 CanvasTransform)> WarpToCanvasAsync(
        Image calibrated,
        Matrix3x2 transformToReference,
        Matrix3x2 canvasShift,
        DebayerAlgorithm debayerAlgorithm,
        int canvasWidth,
        int canvasHeight,
        CancellationToken cancellationToken = default)
    {
        var debayered = await calibrated.DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken);
        var shifted = transformToReference * canvasShift;
        var warped = await debayered.WarpToReferenceGridAsync(shifted, canvasWidth, canvasHeight, cancellationToken);
        return (warped, shifted);
    }
}
