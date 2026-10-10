using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// One statistic of a real capture beside its synthetic twin's (docs/plans/planetary-restoration.md, R2): its name, both values,
/// and whether the twin's calibration fits it (<see cref="TwinComparison.Fitted"/>) or only reports it.
/// </summary>
public readonly record struct TwinStatistic(string Name, double Real, double Twin, bool Fitted)
{
    /// <summary>The twin's value over the real one.</summary>
    public double Ratio => Twin / Real;

    /// <summary>Whether the ratio is within <paramref name="tolerance"/> of one (R2's pre-registration: 10 %).</summary>
    public bool Within(double tolerance = TwinComparison.Tolerance) => Math.Abs(Ratio - 1) <= tolerance;
}

/// <summary>
/// A synthetic twin measured against the real capture it stands in for, statistic by statistic, one routine for
/// <c>planetary degrade</c>'s comparison and for <c>planetary twin</c>'s search (docs/plans/planetary-stacking.md, A1, #817). The
/// search fits the twin's air to <see cref="Fitted"/> statistics, the ones R2's hand calibration of 2022-09-03 Red found to say
/// something about it: the limb's edge width in the mean of every frame and of the best tenth, the disk's motion by the limb, the
/// aligner's error against it, each band's noise on the disk, the flux's slow and fast parts, and the halo. The quality's spread and its
/// lag 1 as the Laplacian reads them are reported, never fitted: R2 found that reading to be noise on an 8-bit capture.
/// </summary>
public static class TwinComparison
{
    /// <summary>How near one a ratio must be to call the statistic matched (R2's pre-registration).</summary>
    public const double Tolerance = 0.1;

    /// <summary>Each statistic the two captures share, in the order <c>planetary degrade</c> prints them.</summary>
    public static ImmutableArray<TwinStatistic> Compare(CaptureStatistics real, CaptureStatistics twin)
    {
        ArgumentNullException.ThrowIfNull(real);
        ArgumentNullException.ThrowIfNull(twin);
        var rows = ImmutableArray.CreateBuilder<TwinStatistic>();
        void Row(string name, double a, double b, bool fitted = false) => rows.Add(new TwinStatistic(name, a, b, fitted));

        Row("shift RMS, seeing part (px)", real.SeeingRms, twin.SeeingRms);
        Row("the same by the limb (px)", real.LimbSeeingRms, twin.LimbSeeingRms, fitted: true);
        Row("aligner's error against the limb (px)", real.AlignerErrorRms, twin.AlignerErrorRms, fitted: true);
        Row("single frames' radius RMS (px)", real.LimbRadiusRms, twin.LimbRadiusRms);
        Row("limb edge width, every frame (px)", real.LimbWidthAll, twin.LimbWidthAll, fitted: true);
        Row("limb edge width, best tenth (px)", real.LimbWidthBest, twin.LimbWidthBest, fitted: true);
        if (real.LimbAll is { } ra && twin.LimbAll is { } ta)
        {
            Row("limb darkening k, every frame", ra.LimbDarkening, ta.LimbDarkening);
            Row("limb blur sigma, every frame (px)", ra.PsfSigma, ta.PsfSigma);
            Row("limb blur wing's share, every frame", ra.HaloFraction, ta.HaloFraction);
            // Saturn's rings, each over the globe's brightness as the ringed limb fit reads them (S3).
            if (ra.RingLevels is { } realRings && ta.RingLevels is { } twinRings && realRings.Length == twinRings.Length)
            {
                for (var i = 0; i < realRings.Length; i++)
                {
                    Row($"ring {SaturnRings.Main.Rings[i].Name} over the globe", realRings[i], twinRings[i]);
                }
            }
        }
        if (real.LimbBest is { } rb && twin.LimbBest is { } tb)
        {
            Row("limb blur sigma, best tenth (px)", rb.PsfSigma, tb.PsfSigma);
        }
        if (FrameLimbPercentiles(real) is { } rf && FrameLimbPercentiles(twin) is { } tf)
        {
            Row("single frames' edge width, p10 (px)", rf.Width[0], tf.Width[0]);
            Row("single frames' edge width, p50 (px)", rf.Width[1], tf.Width[1]);
            Row("single frames' edge width, p90 (px)", rf.Width[2], tf.Width[2]);
            Row("single frames' blur sigma, p10 (px)", rf.Sigma[0], tf.Sigma[0]);
            Row("single frames' blur sigma, p50 (px)", rf.Sigma[1], tf.Sigma[1]);
            Row("single frames' blur sigma, p90 (px)", rf.Sigma[2], tf.Sigma[2]);
        }
        for (var j = 0; j < Math.Min(real.Halo.Length, twin.Halo.Length); j++)
        {
            var annulus = string.Create(CultureInfo.InvariantCulture,
                $"halo {PlanetaryCaptureStatistics.HaloAnnuli[j]:0.0#} to {PlanetaryCaptureStatistics.HaloAnnuli[j + 1]:0.0#} radii (ADU)");
            Row(annulus, real.Halo[j], twin.Halo[j], fitted: true);
        }
        Row("disk level over the local sky (ADU)", real.Camera.DiskLevel, twin.Camera.DiskLevel);
        Row("flux, quarter-second RMS", real.FluxSlowRms, twin.FluxSlowRms, fitted: true);
        Row("flux, frame to frame RMS", real.FluxFastRms, twin.FluxFastRms, fitted: true);
        if (real.Warp.Bound == WarpLengthBound.Measured && twin.Warp.Bound == WarpLengthBound.Measured)
        {
            Row("warp correlation length (px)", real.Warp.CorrelationLength, twin.Warp.CorrelationLength);
        }
        Row("warp RMS (px)", real.Warp.Rms, twin.Warp.Rms);
        for (var i = 0; i < PlanetaryCaptureStatistics.Percentiles.Length; i++)
        {
            if (i == 2)
            {
                continue;
            }
            Row(string.Create(CultureInfo.InvariantCulture, $"quality p{PlanetaryCaptureStatistics.Percentiles[i]:0} over median"),
                real.QualityPercentiles[i] / real.QualityPercentiles[2], twin.QualityPercentiles[i] / twin.QualityPercentiles[2]);
        }
        Row("quality median (absolute)", real.QualityPercentiles[2], twin.QualityPercentiles[2]);
        Row("quality lag-1", real.QualityLag1, twin.QualityLag1);
        for (var j = 0; j < Math.Min(real.Noise.Length, twin.Noise.Length); j++)
        {
            Row(string.Create(CultureInfo.InvariantCulture, $"noise band {j + 1}, sky (ADU)"), real.Noise[j].Sky, twin.Noise[j].Sky);
            Row(string.Create(CultureInfo.InvariantCulture, $"noise band {j + 1}, disk (ADU)"), real.Noise[j].Disk, twin.Noise[j].Disk, fitted: true);
        }
        return rows.ToImmutable();
    }

    /// <summary>The rows the calibration fits.</summary>
    public static ImmutableArray<TwinStatistic> Fitted(ImmutableArray<TwinStatistic> rows) => [.. rows.Where(static r => r.Fitted)];

    /// <summary>
    /// How far the twin is from the real capture over the fitted statistics: the sum of each one's squared log ratio, so 10 % high and
    /// 10 % low cost the same and no statistic's units weigh it. The REAL capture alone decides which statistics count (finite and
    /// positive there), so the count returned beside the sum is the same for every twin of one capture; a statistic the twin could not
    /// measure where the capture could costs <see cref="UnmeasuredCost"/>, more than any it measured within a factor of ten.
    /// </summary>
    /// <remarks>
    /// Left out on the twin's side as well, a statistic was a row a trial could lose: a halo annulus (a level less the sky, near zero at its
    /// outer edge) pushed below zero dropped out of the mean instead of paying for it, and the trial scored better than one that measured
    /// it twice too bright (#1413).
    /// </remarks>
    public static (double Mismatch, int Used) Mismatch(ImmutableArray<TwinStatistic> rows)
    {
        double sum = 0;
        var used = 0;
        foreach (var row in rows)
        {
            if (!row.Fitted || !(row.Real > 0) || !double.IsFinite(row.Real))
            {
                continue;
            }
            if (row.Twin > 0 && double.IsFinite(row.Twin))
            {
                var log = Math.Log(row.Ratio);
                sum += log * log;
            }
            else
            {
                sum += UnmeasuredCost;
            }
            used++;
        }
        return (sum, used);
    }

    /// <summary>What a statistic the twin could not measure costs in <see cref="Mismatch"/>: a factor of ten's squared log.</summary>
    public static readonly double UnmeasuredCost = Math.Log(10) * Math.Log(10);

    /// <summary>The single frames' limb edge widths and fitted blur sigmas at their 10th, 50th and 90th percentiles, and their median
    /// limb darkening; null with no frames.</summary>
    public static (ImmutableArray<double> Width, ImmutableArray<double> Sigma, double K)? FrameLimbPercentiles(CaptureStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        var widths = statistics.FrameLimbs.Select(f => f.EdgeWidth).Where(double.IsFinite).ToArray();
        var fits = statistics.FrameLimbs.Select(f => f.Fit).OfType<LimbFit>().ToArray();
        if (widths.Length == 0 || fits.Length == 0)
        {
            return null;
        }
        ImmutableArray<double> tenths = [10, 50, 90];
        return (PlanetaryCaptureStatistics.PercentilesOf(widths, tenths), PlanetaryCaptureStatistics.PercentilesOf(fits.Select(f => f.PsfSigma).ToArray(), tenths),
            PlanetaryCaptureStatistics.PercentilesOf(fits.Select(f => f.LimbDarkening).ToArray(), [50])[0]);
    }
}
