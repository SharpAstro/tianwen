using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Per-channel background / noise stats. <c>NoiseSigma</c> is the
/// MAD-based robust noise estimate, scaled to the unit range -- same
/// convention as <see cref="Image.EstimateNoiseProfile"/>.
/// </summary>
public readonly record struct ChannelStats(int ChannelIndex, float Pedestal, float Median, float Mad, float NoiseSigma);

/// <summary>
/// Whole-image stats: dimensions, linear-vs-stretched detection,
/// per-channel background/noise, and per-star geometry aggregated across
/// detected stars (HFD/FWHM/Ellipticity/SNR -- median across the catalog).
/// </summary>
/// <remarks>
/// <para>HFD/FWHM/SNR are physically meaningful only on a *linear*
/// input. Stretch is a non-linear monotone map, so the same Gaussian
/// PSF in linear space yields different fitted HFD/FWHM after stretch
/// and the SNR figure stops being a photon-count statistic. The
/// <see cref="IsLinear"/> flag is set from
/// <see cref="Image.DetectPreStretched(Image)"/> (mid-image median > 0.2
/// classifies the input as already stretched). Stats are computed
/// either way; callers that compare across plates should refuse to
/// compare when one side reports <see cref="IsLinear"/> = false.</para>
///
/// <para>All members of <see cref="PerChannel"/> are non-NaN even for
/// zero-pixel-variance inputs (<see cref="ChannelStats.NoiseSigma"/>
/// falls back to 0 if MAD cannot be measured).</para>
/// </remarks>
public sealed record ImageStats(
    int Width,
    int Height,
    int ChannelCount,
    bool IsLinear,
    int StarCount,
    float HfdMedian,
    float FwhmMedian,
    float EllipticityMedian,
    float SnrMedian,
    ImmutableArray<ChannelStats> PerChannel)
{
    /// <summary>
    /// Compute stats for <paramref name="image"/>. Runs star detection
    /// against channel 0 (SNR &gt;= <paramref name="snrMin"/>, up to
    /// <paramref name="maxStars"/>), then collects per-channel
    /// pedestal/median/MAD + noise σ. <paramref name="image"/> is not
    /// modified or retained beyond this call.
    /// </summary>
    public static async Task<ImageStats> ComputeAsync(
        Image image,
        float snrMin = 20f,
        int maxStars = 500,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var (channels, width, height) = image.Shape;

        // DetectPreStretched only makes sense on a [0, 1]-normalised plate
        // (its threshold is `median > 0.2`). ScaleFloatValuesToUnit is a
        // no-op when MaxValue <= 1, so this is free for already-normalised
        // inputs (canonical for SharpenPipeline outputs) and a one-pass
        // multiply for raw FITS plates that come in at sensor ADU range.
        var normalised = image.ScaleFloatValuesToUnit();
        var isLinear = !Image.DetectPreStretched(normalised);

        var stars = await image.FindStarsAsync(
            channel: 0,
            snrMin: snrMin,
            maxStars: maxStars,
            minStars: 1,
            maxRetries: 2,
            logger: logger,
            cancellationToken: cancellationToken);

        // MapReduceStarProperty crashes on count==0 (zero-length sample);
        // explicit guard keeps the record's float fields predictable.
        float hfdMed = 0f, fwhmMed = 0f, eccMed = 0f, snrMed = 0f;
        if (stars.Count > 0)
        {
            hfdMed = stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);
            fwhmMed = stars.MapReduceStarProperty(SampleKind.FWHM, AggregationMethod.Median);
            eccMed = stars.MapReduceStarProperty(SampleKind.Ellipticity, AggregationMethod.Median);

            // SNR isn't surfaced through MapReduceStarProperty (SampleKind
            // covers HFD/FWHM/Ellipticity only). Compute median directly
            // over the per-star SNR field.
            var snrArr = new float[stars.Count];
            var i = 0;
            foreach (var s in stars) snrArr[i++] = s.SNR;
            snrMed = StatisticsHelper.MedianFast(snrArr);
        }

        var noise = image.EstimateNoiseProfile();
        var perChannel = ImmutableArray.CreateBuilder<ChannelStats>(channels);
        for (var c = 0; c < channels; c++)
        {
            var (pedestal, median, mad) = image.GetPedestralMedianAndMADScaledToUnit(c);
            var sigma = c < noise.Length ? noise[c] : 0f;
            perChannel.Add(new ChannelStats(c, pedestal, median, mad, sigma));
        }

        return new ImageStats(width, height, channels, isLinear, stars.Count, hfdMed, fwhmMed, eccMed, snrMed, perChannel.ToImmutable());
    }
}
