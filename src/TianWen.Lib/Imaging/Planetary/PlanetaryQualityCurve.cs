using System;
using System.Collections.Immutable;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>A point on the sorted quality curve: the share of the run's frames kept, and the quality of the last frame that keeps.</summary>
public readonly record struct QualityCut(double Share, float Quality);

/// <summary>
/// The quality of a stretch of the run's frames in time order: the frames <paramref name="First"/> on, <paramref name="Graded"/> of them
/// graded, and their 10th percentile, median and 90th percentile, each over the run's best frame (100).
/// </summary>
public readonly record struct QualityBin(int First, int Count, int Graded, float P10, float Median, float P90);

/// <summary>
/// A run's frame-quality curve, AutoStakkert's graph (#1364): the curve the keep is chosen on, read off the grades the stack already gave
/// (<see cref="FrameGrader.GradeAllAsync"/>, or the grade cache), so nothing is measured again.
/// <list type="bullet">
/// <item><b>Sorted, best first</b> (<see cref="Sorted"/>): each graded frame's grade over the best frame's (100), against the share of the
/// run's frames kept, with the keep's own cut and the reference cuts (<see cref="ReferenceShares"/>).</item>
/// <item><b>Through the run</b> (<see cref="Bins"/>): the 10th to 90th percentile and the median per <see cref="BinFrames"/> frames in the
/// order they were taken, a bin never spanning two files (<see cref="FileStarts"/>), so a passing cloud, a dew-up or a seeing change shows,
/// which the sorted curve hides.</item>
/// </list>
/// A frame the stack leaves out (its grade zero: cut, smeared, dim, or a readout the grader could not score) is in neither, and counted by
/// cause, since it is not graded on the same footing.
/// </summary>
public sealed record PlanetaryQualityCurve
{
    /// <summary>How many frames, in time order, one point of the run's curve reads.</summary>
    public const int BinFrames = 100;

    /// <summary>The cuts the sorted curve is read at beside the keep's own, shares of the run's frames.</summary>
    public static readonly ImmutableArray<double> ReferenceShares = [0.10, 0.20, 0.35, 0.50, 0.70];

    /// <summary>Each graded frame's quality over the best frame's (100), best first.</summary>
    public required ImmutableArray<float> Sorted { get; init; }

    /// <summary>Every frame of the run, graded or left out: the shares on the sorted curve are of these.</summary>
    public required int Frames { get; init; }

    /// <summary>The keep asked for, a share of the run's frames.</summary>
    public required double Keep { get; init; }

    /// <summary>The frames that keep holds, as <see cref="FrameGrader.SelectBest"/> counts them (never a frame left out).</summary>
    public required int KeptFrames { get; init; }

    /// <summary>The quality of the last frame the keep holds, over the best (100).</summary>
    public required float KeepQuality { get; init; }

    /// <summary>The sorted curve at each of <see cref="ReferenceShares"/> it reaches.</summary>
    public required ImmutableArray<QualityCut> ReferenceCuts { get; init; }

    /// <summary>The run's quality in time order, <see cref="BinFrames"/> frames a point.</summary>
    public required ImmutableArray<QualityBin> Bins { get; init; }

    /// <summary>The frame each file of the run starts at, the first at zero: one segment of <see cref="Bins"/> a file.</summary>
    public required ImmutableArray<int> FileStarts { get; init; }

    /// <summary>Frames left out because their planet is cut or missing (#1237, #1291).</summary>
    public int LeftOutCut { get; init; }

    /// <summary>Frames left out because their planet is smeared, the telescope moving (#1300).</summary>
    public int LeftOutSmeared { get; init; }

    /// <summary>Frames left out because their planet is dim (#1307).</summary>
    public int LeftOutDim { get; init; }

    /// <summary>Frames left out for no cause above: a readout the grader scored zero (<see cref="FrameGrader.IsCorruptReadout"/>).</summary>
    public int LeftOutUnscored { get; init; }

    /// <summary>
    /// Where the sorted curve's head ends, a share of the run's frames: the first share past which the curve falls no faster than twice its
    /// mean slope between 5 and 50 %. Null when the curve does not reach 50 %, or is flat there.
    /// </summary>
    public double? HeadEndsAt { get; init; }

    /// <summary>The frames graded, which both panels hold.</summary>
    public int Graded => Sorted.Length;

    /// <summary>The frames left out, which neither panel holds.</summary>
    public int LeftOut => Frames - Graded;

    /// <summary>
    /// The curve of <paramref name="grades"/> (one run's, every frame, as <see cref="FrameGrader.GradeAllAsync"/> gives them), kept at
    /// <paramref name="keep"/>; <paramref name="fileStarts"/> the frame each file of the run starts at, or default for a single file.
    /// </summary>
    public static PlanetaryQualityCurve From(ImmutableArray<FrameGrade> grades, double keep, ImmutableArray<int> fileStarts = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keep, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keep, 1.0);
        if (grades.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A quality curve needs the run's grades.", nameof(grades));
        }

        var frames = grades.Length;
        var byIndex = new float[frames];
        var best = 0f;
        var (cut, smeared, dim, unscored) = (0, 0, 0, 0);
        foreach (var grade in grades)
        {
            if ((uint)grade.Index >= (uint)frames)
            {
                throw new ArgumentException($"Frame {grade.Index} is outside a run of {frames} frames.", nameof(grades));
            }
            if (grade.Score > 0f)
            {
                byIndex[grade.Index] = grade.Score;
                best = MathF.Max(best, grade.Score);
                continue;
            }
            byIndex[grade.Index] = float.NaN;
            if (grade.Cut)
            {
                cut++;
            }
            else if (grade.Smeared)
            {
                smeared++;
            }
            else if (grade.Dim)
            {
                dim++;
            }
            else
            {
                unscored++;
            }
        }

        // Each graded frame over the best, best first; the shares on this curve are of every frame, as the keep's is.
        var sortedBuilder = ImmutableArray.CreateBuilder<float>(frames - cut - smeared - dim - unscored);
        foreach (var score in byIndex)
        {
            if (!float.IsNaN(score))
            {
                sortedBuilder.Add(100f * score / best);
            }
        }
        sortedBuilder.Sort(static (a, b) => b.CompareTo(a));
        var sorted = sortedBuilder.MoveToImmutable();

        // The frames the keep holds, as the stack counts them (FrameGrader.SelectBest): never a frame left out while one is graded.
        var kept = Math.Clamp((int)Math.Round(frames * keep, MidpointRounding.AwayFromZero), 1, frames);
        kept = sorted.Length > 0 ? Math.Min(kept, sorted.Length) : kept;
        var keepQuality = sorted.Length > 0 ? sorted[kept - 1] : float.NaN;

        var cuts = ImmutableArray.CreateBuilder<QualityCut>(ReferenceShares.Length);
        foreach (var share in ReferenceShares)
        {
            var at = (int)Math.Round(frames * share, MidpointRounding.AwayFromZero);
            if (at >= 1 && at <= sorted.Length)
            {
                cuts.Add(new QualityCut(share, sorted[at - 1]));
            }
        }

        ImmutableArray<int> starts = fileStarts.IsDefaultOrEmpty ? [0] : fileStarts;
        return new PlanetaryQualityCurve
        {
            Sorted = sorted,
            Frames = frames,
            Keep = keep,
            KeptFrames = kept,
            KeepQuality = keepQuality,
            ReferenceCuts = cuts.ToImmutable(),
            Bins = BinsOf(byIndex, best, starts),
            FileStarts = starts,
            LeftOutCut = cut,
            LeftOutSmeared = smeared,
            LeftOutDim = dim,
            LeftOutUnscored = unscored,
            HeadEndsAt = HeadEnd(sorted, frames),
        };
    }

    /// <summary>The frame each file of <paramref name="stream"/> starts at: one start a part of a session, a single zero for one file.</summary>
    public static ImmutableArray<int> FileStartsOf(IPlanetaryFrameStream stream)
    {
        if (stream is not PlanetaryFrameSequence { PartCount: > 1 } sequence)
        {
            return [0];
        }
        var starts = ImmutableArray.CreateBuilder<int>(sequence.PartCount);
        for (var part = 0; part < sequence.PartCount; part++)
        {
            starts.Add(sequence.StartOf(part));
        }
        return starts.MoveToImmutable();
    }

    /// <summary>The sorted curve's quality at <paramref name="share"/> of the run's frames, or NaN past its graded frames.</summary>
    public float QualityAt(double share)
    {
        var at = (int)Math.Round(Frames * share, MidpointRounding.AwayFromZero);
        return at >= 1 && at <= Sorted.Length ? Sorted[at - 1] : float.NaN;
    }

    // BinFrames frames a bin in time order, each file's frames binned on their own; a bin's percentiles are of its graded frames only.
    private static ImmutableArray<QualityBin> BinsOf(float[] byIndex, float best, ImmutableArray<int> starts)
    {
        var bins = ImmutableArray.CreateBuilder<QualityBin>();
        var scratch = new float[BinFrames];
        for (var file = 0; file < starts.Length; file++)
        {
            var end = file + 1 < starts.Length ? starts[file + 1] : byIndex.Length;
            for (var first = starts[file]; first < end; first += BinFrames)
            {
                var count = Math.Min(BinFrames, end - first);
                var graded = 0;
                for (var i = first; i < first + count; i++)
                {
                    if (!float.IsNaN(byIndex[i]))
                    {
                        scratch[graded++] = 100f * byIndex[i] / best;
                    }
                }
                if (graded == 0)
                {
                    bins.Add(new QualityBin(first, count, 0, float.NaN, float.NaN, float.NaN));
                    continue;
                }
                var values = scratch.AsSpan(0, graded);
                bins.Add(new QualityBin(first, count, graded,
                    StatisticsHelper.PercentileFast(values, 0.10), StatisticsHelper.PercentileFast(values, 0.50), StatisticsHelper.PercentileFast(values, 0.90)));
            }
        }
        return bins.ToImmutable();
    }

    // The first share past which the sorted curve falls no faster than twice its mean slope between 5 and 50 %, each local slope read
    // over half a percent of the run (at least one frame).
    private static double? HeadEnd(ImmutableArray<float> sorted, int frames)
    {
        var at5 = (int)Math.Round(frames * 0.05, MidpointRounding.AwayFromZero);
        var at50 = (int)Math.Round(frames * 0.50, MidpointRounding.AwayFromZero);
        if (at5 < 1 || at50 > sorted.Length || at50 <= at5)
        {
            return null;
        }
        var meanFall = (sorted[at5 - 1] - sorted[at50 - 1]) / (at50 - at5);
        if (meanFall <= 0f)
        {
            return null;
        }
        var step = Math.Max(1, (int)Math.Round(frames * 0.005, MidpointRounding.AwayFromZero));
        for (var i = 0; i + step < at50; i++)
        {
            var fall = (sorted[i] - sorted[i + step]) / step;
            if (fall <= 2f * meanFall)
            {
                return (double)(i + 1) / frames;
            }
        }
        return (double)at50 / frames;
    }
}
