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
public sealed class HfdPsfEstimator(ILogger<HfdPsfEstimator>? logger = null) : IPsfEstimator
{
    /// <summary>Default PSF radius (in pixels) used when star detection finds
    /// no usable stars -- matches SAS Pro's <c>default_radius = 3.0</c>.</summary>
    public const float DefaultRadiusPx = 3.0f;

    /// <summary>Lower bound of the log2-radius training range -- corresponds to psf01 = 0.</summary>
    public const float MinRadiusPx = 1.0f;

    /// <summary>Upper bound of the log2-radius training range -- corresponds to psf01 = 1.</summary>
    public const float MaxRadiusPx = 8.0f;

    /// <summary>Minimum SNR for a star to count toward the PSF estimate.</summary>
    public const float MinSnr = 20f;

    public async Task<float> EstimateAsync(Image image, CancellationToken cancellationToken = default)
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
            return EncodeRadiusToPsf01(DefaultRadiusPx);
        }

        // Median FWHM across detected stars. ImagedStar.StarFWHM is already in
        // pixels (computed during analyse-star using the same Gaussian-fit
        // assumption SAS Pro's measure_psf_radius uses), so no further
        // conversion is needed besides the FWHM -> radius halving.
        var fwhms = stars.Select(s => s.StarFWHM).Where(f => f > 0f && !float.IsNaN(f)).ToArray();
        if (fwhms.Length == 0)
        {
            logger?.LogDebug("HfdPsfEstimator: no positive FWHM samples; falling back to default radius");
            return EncodeRadiusToPsf01(DefaultRadiusPx);
        }
        Array.Sort(fwhms);
        var medianFwhm = fwhms[fwhms.Length / 2];
        var radius = medianFwhm * 0.5f;
        var psf01 = EncodeRadiusToPsf01(radius);
        logger?.LogDebug("HfdPsfEstimator: n={Count} medianFWHM={Fwhm:F2}px radius={Radius:F2}px psf01={Psf01:F3}",
            fwhms.Length, medianFwhm, radius, psf01);
        return psf01;
    }

    /// <summary>
    /// Maps a physical PSF radius (in pixels) to the [0, 1] log2-encoded
    /// scalar the NAFNet conditional-PSF model expects. Inputs outside
    /// [<see cref="MinRadiusPx"/>, <see cref="MaxRadiusPx"/>] saturate at 0
    /// or 1 respectively -- the model was only trained on that range.
    /// </summary>
    public static float EncodeRadiusToPsf01(float radiusPx)
    {
        var clamped = Math.Clamp(radiusPx, MinRadiusPx, MaxRadiusPx);
        var t = (MathF.Log2(clamped) - MathF.Log2(MinRadiusPx))
              / (MathF.Log2(MaxRadiusPx) - MathF.Log2(MinRadiusPx));
        return t;
    }
}
