using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The five things about a night's air and telescope a synthetic twin is fitted by (docs/plans/planetary-stacking.md, A1): the free air's
/// Fried parameter at 500 nm and its wind, the still layer at the telescope's Fried parameter (its outer scale the tube's, set elsewhere),
/// and the share of the light the telescope scatters wide and that scatter's core. Everything else a twin needs is read off the capture.
/// </summary>
public sealed record TwinKnobs(double R0M, double WindMps, double LocalR0M, double ScatterFraction, double ScatterCoreArcsec)
{
    /// <summary>The fewest and most each knob may take: a scatter of a thousandth is the floor for "none" (the search is in logarithms).</summary>
    public static readonly TwinKnobs Lower = new TwinKnobs(0.02, 2, 0.005, 0.001, 1);

    /// <summary>The upper bounds: a still layer of 1 m is no layer at all at these apertures.</summary>
    public static readonly TwinKnobs Upper = new TwinKnobs(0.3, 40, 1, 0.3, 20);

    /// <summary>R2's hand calibration of 2022-09-03 Red, the search's start where nothing better is known.</summary>
    public static readonly TwinKnobs HandCalibratedRed = new TwinKnobs(0.085, 22, 0.027, 0.05, 5);

    /// <summary><paramref name="options"/> with these knobs in it.</summary>
    public DegradeOptions ApplyTo(DegradeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with { R0M = R0M, WindMps = WindMps, LocalR0M = LocalR0M, ScatterFraction = ScatterFraction, ScatterCoreArcsec = ScatterCoreArcsec };
    }

    /// <summary>The knobs <paramref name="options"/> already holds, a finite still layer and scatter put at their floors where there are none.</summary>
    public static TwinKnobs From(DegradeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TwinKnobs(options.R0M, options.WindMps, double.IsFinite(options.LocalR0M) ? options.LocalR0M : Upper.LocalR0M,
            Math.Max(options.ScatterFraction, Lower.ScatterFraction), options.ScatterCoreArcsec);
    }

    internal double[] ToLog() => [Math.Log(R0M), Math.Log(WindMps), Math.Log(LocalR0M), Math.Log(ScatterFraction), Math.Log(ScatterCoreArcsec)];

    internal static TwinKnobs FromLog(ReadOnlySpan<double> x) => new TwinKnobs(
        Clamp(x[0], Lower.R0M, Upper.R0M), Clamp(x[1], Lower.WindMps, Upper.WindMps), Clamp(x[2], Lower.LocalR0M, Upper.LocalR0M),
        Clamp(x[3], Lower.ScatterFraction, Upper.ScatterFraction), Clamp(x[4], Lower.ScatterCoreArcsec, Upper.ScatterCoreArcsec));

    private static double Clamp(double log, double low, double high) => Math.Clamp(Math.Exp(log), low, high);
}

/// <summary>One twin made with <see cref="Knobs"/> and measured against the capture: its fitted statistics' mean squared log ratio.</summary>
public sealed record TwinTrial(TwinKnobs Knobs, double Mismatch, ImmutableArray<TwinStatistic> Rows);

/// <summary>
/// A synthetic twin's air fitted to its capture (docs/plans/planetary-stacking.md, A1, #817): what R2 and #1281 did by hand in ten and eleven
/// trials, run by the code. A Nelder-Mead simplex over the knobs' logarithms (a step is a ratio, as the knobs are), each trial a twin made and
/// measured by the caller and scored by <see cref="TwinComparison"/>: the mean of the fitted statistics' squared log ratios, so a trial whose
/// twin could not measure one statistic is still compared fairly. The search holds the seed fixed (the caller's), so two trials differ by their
/// knobs alone. It is only as good as the statistics it fits, which R2 chose; a match on them is not a match on everything (R2 found two mixtures
/// of seeing and aligner error that read alike), so its rules on #817's A1 issue judge it against the hand calibration.
/// </summary>
public static class PlanetaryTwinCalibration
{
    /// <summary>The mean squared log ratio over <paramref name="rows"/>' fitted statistics; infinite where none could be measured.</summary>
    public static double MeanMismatch(ImmutableArray<TwinStatistic> rows)
    {
        var (sum, used) = TwinComparison.Mismatch(rows);
        return used > 0 ? sum / used : double.PositiveInfinity;
    }

    /// <summary>
    /// Searches from <paramref name="start"/> for the knobs whose twin best matches <paramref name="real"/>, making at most
    /// <paramref name="maxTrials"/> twins through <paramref name="measureTwin"/> (null where a twin could not be measured, which scores
    /// infinitely bad). Every trial is returned in the order it ran, the best first beside them.
    /// </summary>
    public static async Task<(TwinTrial Best, ImmutableArray<TwinTrial> Trials)> FitAsync(CaptureStatistics real, TwinKnobs start,
        Func<TwinKnobs, CancellationToken, Task<CaptureStatistics?>> measureTwin, int maxTrials = 40, IProgress<TwinTrial>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(real);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(measureTwin);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrials, 6);

        var trials = ImmutableArray.CreateBuilder<TwinTrial>();
        // A knob set already tried (the simplex can land on one twice once clamped at a bound) is not made again.
        var seen = new Dictionary<TwinKnobs, TwinTrial>();
        async Task<TwinTrial> Evaluate(double[] x)
        {
            var knobs = TwinKnobs.FromLog(x);
            if (seen.TryGetValue(knobs, out var known))
            {
                return known;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var twin = await measureTwin(knobs, cancellationToken).ConfigureAwait(false);
            var rows = twin is null ? [] : TwinComparison.Compare(real, twin);
            var trial = new TwinTrial(knobs, twin is null ? double.PositiveInfinity : MeanMismatch(rows), rows);
            seen[knobs] = trial;
            trials.Add(trial);
            progress?.Report(trial);
            return trial;
        }

        // The start and a step of half again along each knob: a twin's statistics move by a few percent for a step that small at best.
        const int n = 5;
        var step = Math.Log(1.5);
        var simplex = new double[n + 1][];
        var scores = new double[n + 1];
        simplex[0] = start.ToLog();
        scores[0] = (await Evaluate(simplex[0]).ConfigureAwait(false)).Mismatch;
        for (var i = 0; i < n; i++)
        {
            double[] vertex = [.. simplex[0]];
            vertex[i] += step;
            simplex[i + 1] = vertex;
            scores[i + 1] = (await Evaluate(vertex).ConfigureAwait(false)).Mismatch;
        }

        while (trials.Count < maxTrials)
        {
            var order = Enumerable.Range(0, n + 1).OrderBy(i => scores[i]).ToArray();
            (simplex, scores) = ([.. order.Select(i => simplex[i])], [.. order.Select(i => scores[i])]);
            // Converged once every vertex scores within a hundredth of the best's mismatch, or the best is a match to a percent a statistic.
            if (scores[n] - scores[0] <= 0.01 * scores[0] + 1e-8 || scores[0] < 1e-4)
            {
                break;
            }

            var centroid = new double[n];
            for (var i = 0; i < n; i++)
            {
                for (var d = 0; d < n; d++)
                {
                    centroid[d] += simplex[i][d] / n;
                }
            }
            double[] Toward(double t) => [.. Enumerable.Range(0, n).Select(d => centroid[d] + (t * (simplex[n][d] - centroid[d])))];

            var reflected = Toward(-1);
            var reflectedScore = (await Evaluate(reflected).ConfigureAwait(false)).Mismatch;
            if (reflectedScore < scores[0])
            {
                var expanded = Toward(-2);
                var expandedScore = trials.Count < maxTrials ? (await Evaluate(expanded).ConfigureAwait(false)).Mismatch : double.PositiveInfinity;
                (simplex[n], scores[n]) = expandedScore < reflectedScore ? (expanded, expandedScore) : (reflected, reflectedScore);
            }
            else if (reflectedScore < scores[n - 1])
            {
                (simplex[n], scores[n]) = (reflected, reflectedScore);
            }
            else
            {
                var outside = reflectedScore < scores[n];
                var contracted = Toward(outside ? -0.5 : 0.5);
                var contractedScore = trials.Count < maxTrials ? (await Evaluate(contracted).ConfigureAwait(false)).Mismatch : double.PositiveInfinity;
                if (contractedScore < Math.Min(reflectedScore, scores[n]))
                {
                    (simplex[n], scores[n]) = (contracted, contractedScore);
                }
                else
                {
                    // Shrink toward the best vertex.
                    for (var i = 1; i <= n && trials.Count < maxTrials; i++)
                    {
                        for (var d = 0; d < n; d++)
                        {
                            simplex[i][d] = simplex[0][d] + (0.5 * (simplex[i][d] - simplex[0][d]));
                        }
                        scores[i] = (await Evaluate(simplex[i]).ConfigureAwait(false)).Mismatch;
                    }
                }
            }
        }

        var all = trials.ToImmutable();
        return (all.MinBy(t => t.Mismatch) ?? all[0], all);
    }
}
