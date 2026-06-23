using System;
using System.Collections.Immutable;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>A frame's sharpness score (higher = sharper), keyed by its index in the stream.</summary>
public readonly record struct FrameGrade(int Index, float Score);

/// <summary>
/// Grades every frame of an <see cref="IPlanetaryFrameStream"/> with an
/// <see cref="IFrameQualityEstimator"/>, then selects the best fraction (lucky imaging's "keep the
/// sharpest N%") and the single best reference frame. The top-K reference refinement (rebuild the
/// reference from a quality-weighted mean of the best frames once a coarse align exists) is Phase 4 --
/// here the reference is simply the single highest-scoring frame, the bootstrap for that refinement.
/// </summary>
public sealed class FrameGrader(IFrameQualityEstimator estimator)
{
    /// <summary>The estimator used to score frames.</summary>
    public IFrameQualityEstimator Estimator => estimator;

    /// <summary>
    /// Grades every frame. When <paramref name="region"/> is <see cref="Rectangle.Empty"/> the disk
    /// bounding box is auto-detected per frame (<see cref="PlanetaryDisk.BoundingBox"/>) so each frame is
    /// scored over its own disk, robust to the planet drifting before alignment. The returned grades are
    /// in frame order; use <see cref="SelectBest"/> / <see cref="Reference"/> to rank them.
    /// <para>Sequential by design (deterministic, and frame I/O dominates); parallel grading is a later
    /// perf lever -- the estimator is stateless and the SER reader is thread-safe.</para>
    /// </summary>
    public async Task<ImmutableArray<FrameGrade>> GradeAllAsync(IPlanetaryFrameStream stream, Rectangle region = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var count = stream.FrameCount;
        if (count <= 0)
        {
            return ImmutableArray<FrameGrade>.Empty;
        }

        var grades = ImmutableArray.CreateBuilder<FrameGrade>(count);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = await stream.LoadAsync(i, cancellationToken).ConfigureAwait(false);
            try
            {
                var r = region.IsEmpty ? PlanetaryDisk.BoundingBox(image) : region;
                grades.Add(new FrameGrade(i, estimator.Score(image, r)));
            }
            finally
            {
                image.Release();
            }
        }

        return grades.MoveToImmutable();
    }

    /// <summary>Returns <paramref name="grades"/> sorted best-first (descending score, ties by index).</summary>
    public static ImmutableArray<FrameGrade> SortByQuality(ImmutableArray<FrameGrade> grades)
        => grades.Sort(static (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });

    /// <summary>
    /// Selects the best <paramref name="fraction"/> (in <c>(0, 1]</c>) of frames by score, returned as
    /// frame indices best-first. Always keeps at least one frame.
    /// </summary>
    public static ImmutableArray<int> SelectBest(ImmutableArray<FrameGrade> grades, double fraction)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fraction, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fraction, 1.0);
        if (grades.IsDefaultOrEmpty)
        {
            return ImmutableArray<int>.Empty;
        }

        var sorted = SortByQuality(grades);
        var keep = Math.Max(1, (int)Math.Round(sorted.Length * fraction, MidpointRounding.AwayFromZero));
        keep = Math.Min(keep, sorted.Length);

        var picks = ImmutableArray.CreateBuilder<int>(keep);
        for (var i = 0; i < keep; i++)
        {
            picks.Add(sorted[i].Index);
        }

        return picks.MoveToImmutable();
    }

    /// <summary>The index of the single highest-scoring frame (the reference bootstrap), or <c>-1</c> when empty.</summary>
    public static int Reference(ImmutableArray<FrameGrade> grades)
    {
        if (grades.IsDefaultOrEmpty)
        {
            return -1;
        }

        var bestIndex = grades[0].Index;
        var bestScore = grades[0].Score;
        for (var i = 1; i < grades.Length; i++)
        {
            if (grades[i].Score > bestScore)
            {
                bestScore = grades[i].Score;
                bestIndex = grades[i].Index;
            }
        }

        return bestIndex;
    }
}
