using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// PSF estimator that reuses TianWen's existing star detector. Runs
/// <see cref="Image.FindStarsAsync"/> on the luminance channel, takes the
/// median per-star FWHM (already measured during star analysis), converts
/// FWHM to radius (= FWHM / 2), and log2-encodes the radius into the [0, 1]
/// "psf01" representation the NAFNet conditional-PSF deconvolution model
/// consumes.
/// </summary>
/// <remarks>
/// Whole-image scalar for v1: <see cref="IPsfEstimator.EstimateChunkAsync"/>
/// delegates to the whole-image variant. A future per-chunk variant could
/// reuse FindStars on chunk slices or port SAS Pro's
/// <c>measure_psf_radius</c> (SEP-based), but the per-image scalar is good
/// enough for typical small-FOV astro frames where PSF is roughly uniform.
/// </remarks>
// The defaults below are literals rather than MinRadiusPx / MaxRadiusPx because a primary
// constructor's parameter defaults cannot see the type's own constants. They must stay equal to them,
// and HfdPsfEstimatorTests.TheDefaultRangeIsStillTheOneTheShippedModelWasTrainedUnder is what says so.
public sealed class HfdPsfEstimator(
    ILogger<HfdPsfEstimator>? logger = null,
    float minRadiusPx = 1.0f,
    float maxRadiusPx = 8.0f)
    : IPsfEstimator
{
    /// <summary>Default PSF radius (in pixels) used when star detection finds
    /// no usable stars -- matches SAS Pro's <c>default_radius = 3.0</c>.</summary>
    public const float DefaultRadiusPx = 3.0f;

    /// <summary>Lower bound of the log2-radius training range -- corresponds to psf01 = 0.</summary>
    public const float MinRadiusPx = 1.0f;

    /// <summary>Upper bound of the log2-radius training range -- corresponds to psf01 = 1.</summary>
    public const float MaxRadiusPx = 8.0f;

    /// <summary>
    /// The floor of TianWen's OWN deconvolution contract, measured rather than chosen
    /// (`docs/plans/deconvolver-training.md` H5, 2026-09-06). Below the sharpest master this archive
    /// holds (0.88 px), so nothing clamps at the bottom.
    /// </summary>
    public const float TianWenMinRadiusPx = 0.5f;

    /// <summary>
    /// The ceiling of TianWen's own contract. Above the widest input the degradation exporter can
    /// produce (a +4 px FWHM draw in quadrature on the archive's widest master reaches 3.63 px
    /// radius), and deliberately far below SAS's 8 px: the SPREAD of psf01 over a set of frames is
    /// <c>log2(r_hi / r_lo) / log2(max / min)</c>, so the range's total log span divides every
    /// difference, and a ceiling nothing ever approaches spends resolution for nothing. Measured over
    /// all 79 masters, this pair spreads them 0.330 where SAS's spreads them 0.293.
    /// </summary>
    public const float TianWenMaxRadiusPx = 4.0f;

    /// <summary>
    /// The range THIS instance encodes into, defaulting to SAS AI4's because that is the model the
    /// shipped <c>OnnxNonStellarDeconvolver</c> runs.
    /// </summary>
    /// <remarks>
    /// <b>The range belongs to the MODEL, not to the estimator, and mismatching them is silent.</b>
    /// psf01 is a conditioning input: a graph trained on `[1, 8]` handed a number encoded over
    /// `[0.5, 4]` still runs, still produces a plausible image, and is being told a PSF roughly twice
    /// the one it was given. So the default stays SAS's for as long as a SAS graph is what resolves,
    /// and TianWen's own contract (<see cref="TianWenMinRadiusPx"/>, <see cref="TianWenMaxRadiusPx"/>)
    /// becomes the default only alongside the model trained under it.
    /// </remarks>
    public (float Min, float Max) RadiusRange { get; } = (minRadiusPx, maxRadiusPx);

    /// <summary>Minimum SNR for a star to count toward the PSF estimate.</summary>
    public const float MinSnr = 20f;

    public async Task<float> EstimateAsync(Image image, CancellationToken cancellationToken = default)
    {
        var measured = await MeasureRadiusPxAsync(image, cancellationToken);
        var psf01 = EncodeRadiusToPsf01(measured.RadiusPx, RadiusRange.Min, RadiusRange.Max);
        logger?.LogDebug("HfdPsfEstimator: n={Count} medianFWHM={Fwhm:F2}px radius={Radius:F2}px psf01={Psf01:F3} over [{Min}, {Max}] px",
            measured.Stars, measured.RadiusPx * 2f, measured.RadiusPx, psf01, RadiusRange.Min, RadiusRange.Max);
        return psf01;
    }

    /// <summary>The estimator's measurement before any encoding: the radius it read, in pixels, and
    /// how many stars it read it from (0 when it fell back to <see cref="DefaultRadiusPx"/>).</summary>
    internal readonly record struct Measurement(float RadiusPx, int Stars);

    /// <summary>
    /// The measurement half of <see cref="EstimateAsync"/>, UNCLAMPED and unencoded. Split out
    /// rather than duplicated because the encoding range is exactly what P2's H5 is deciding
    /// (<c>docs/plans/deconvolver-training.md</c>): under the shipped <c>[1, 8]</c> px range this
    /// archive's masters all clamp to psf01 = 0, so a probe that needs to compare candidate ranges
    /// cannot invert the encoded value to recover the radius, because the clamp has already
    /// destroyed it. Anything measuring the encoding must read the radius here.
    /// </summary>
    internal async Task<Measurement> MeasureRadiusPxAsync(Image image, CancellationToken cancellationToken = default)
    {
        var stars = await image.FindStarsAsync(
            channel: 0,
            snrMin: MinSnr,
            logger: logger,
            cancellationToken: cancellationToken);

        if (stars.Count == 0)
        {
            logger?.LogDebug("HfdPsfEstimator: no stars found at SNR>={Snr}, falling back to default radius {Px} px",
                MinSnr, DefaultRadiusPx);
            return new Measurement(DefaultRadiusPx, 0);
        }

        // Median FWHM across detected stars. ImagedStar.StarFWHM is already in pixels (measured
        // during analyse-star as an interpolated radial half-maximum crossing), so the only
        // conversion needed is the FWHM -> radius halving. SAS Pro's measure_psf_radius arrives at
        // its radius by a different route (SEP-based), so treat the two as the same QUANTITY, not
        // as the same estimator.
        var fwhms = stars.Select(s => s.StarFWHM).Where(f => f > 0f && !float.IsNaN(f)).ToArray();
        if (fwhms.Length == 0)
        {
            logger?.LogDebug("HfdPsfEstimator: no positive FWHM samples; falling back to default radius");
            return new Measurement(DefaultRadiusPx, 0);
        }

        Array.Sort(fwhms);
        var medianFwhm = fwhms[fwhms.Length / 2];
        return new Measurement(medianFwhm * 0.5f, fwhms.Length);
    }

    /// <summary>
    /// Maps a physical PSF radius (in pixels) to the [0, 1] log2-encoded
    /// scalar the NAFNet conditional-PSF model expects. Inputs outside
    /// [<see cref="MinRadiusPx"/>, <see cref="MaxRadiusPx"/>] saturate at 0
    /// or 1 respectively -- the model was only trained on that range.
    /// </summary>
    public static float EncodeRadiusToPsf01(float radiusPx)
        => EncodeRadiusToPsf01(radiusPx, MinRadiusPx, MaxRadiusPx);

    /// <summary>
    /// The same encoding over an arbitrary radius range, so a candidate contract can be evaluated
    /// against the shipped one without a second implementation to disagree with this one. The
    /// shipped range is <see cref="MinRadiusPx"/> to <see cref="MaxRadiusPx"/> and is SAS AI4's;
    /// P2's H5 is measuring whether TianWen's own model wants a lower floor.
    /// </summary>
    public static float EncodeRadiusToPsf01(float radiusPx, float minRadiusPx, float maxRadiusPx)
    {
        var clamped = Math.Clamp(radiusPx, minRadiusPx, maxRadiusPx);
        var t = (MathF.Log2(clamped) - MathF.Log2(minRadiusPx))
              / (MathF.Log2(maxRadiusPx) - MathF.Log2(minRadiusPx));
        return t;
    }
}
