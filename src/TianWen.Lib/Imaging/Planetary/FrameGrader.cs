using System;
using System.Collections.Immutable;
using TianWen.Lib.Geometry;
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
    /// Grades every frame. When <paramref name="region"/> is <see cref="PixelRect.Empty"/> the disk
    /// bounding box is auto-detected per frame (<see cref="PlanetaryDisk.BoundingBox"/>) so each frame is
    /// scored over its own disk, robust to the planet drifting before alignment. The returned grades are
    /// in frame order; use <see cref="SelectBest"/> / <see cref="Reference"/> to rank them.
    /// <para>Sequential by design (deterministic, and frame I/O dominates); parallel grading is a later
    /// perf lever -- the estimator is stateless and the SER reader is thread-safe.</para>
    /// </summary>
    public async Task<ImmutableArray<FrameGrade>> GradeAllAsync(IPlanetaryFrameStream stream, PixelRect region = default, CancellationToken cancellationToken = default)
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
                grades.Add(new FrameGrade(i, Grade(estimator, image, region)));
            }
            finally
            {
                image.Release();
            }
        }

        return grades.MoveToImmutable();
    }

    /// <summary>
    /// One frame's score by <paramref name="estimator"/> over <paramref name="region"/> (its own disk's bounding box when
    /// empty), and zero for a frame the camera corrupted as it read it out (<see cref="IsCorruptReadout"/>): never the
    /// reference, and no weight in a stack. The one grading rule, for the batch stacker, the live one and the capture
    /// statistics.
    /// </summary>
    public static float Grade(IFrameQualityEstimator estimator, Image frame, PixelRect region = default)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        return IsCorruptReadout(frame) ? 0f : estimator.Score(frame, region.IsEmpty ? PlanetaryDisk.BoundingBox(frame) : region);
    }

    /// <summary>
    /// Whether the camera corrupted <paramref name="frame"/> as it read it out: a band of rows of any channel whose samples
    /// stand at the frame's full scale nearly all the way across (<see cref="CorruptRowFraction"/> of them), ending ABRUPTLY
    /// in a row that is mostly under half scale. A planet never fills a row of the frame at full scale, and a readout glitch
    /// does exactly that: four frames of the 30,000 in 2024-12-15's Uranus-C capture carry their top two to four rows at 255
    /// over a sky of 6, and that one sharp line is the sharpest thing a Laplacian sees, so the grader made one of them every
    /// stack's reference and the capture statistics' (docs/plans/planetary-restoration.md, R5a). The abrupt edge is what
    /// tells the glitch from an overexposed Moon filling the frame, whose saturated rows fade into bright ones, so a lunar
    /// stack is never emptied by it. Full scale is the frame's own <see cref="Image.MaxValue"/>, the 1 a SER or a live
    /// stream normalises to.
    /// </summary>
    public static bool IsCorruptReadout(Image frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var ceiling = frame.MaxValue;
        if (!(ceiling > 0))
        {
            return false;
        }
        var (width, height) = (frame.Width, frame.Height);
        var needed = (int)Math.Ceiling(CorruptRowFraction * width);
        for (var c = 0; c < frame.ChannelCount; c++)
        {
            var plane = frame.GetChannelSpan(c);
            var bandStart = -1;
            for (var y = 0; y <= height; y++)
            {
                var full = y < height && CountAtLeast(plane.Slice(y * width, width), ceiling * (1 - FullScaleTolerance)) >= needed;
                if (full)
                {
                    bandStart = bandStart < 0 ? y : bandStart;
                    continue;
                }
                if (bandStart >= 0)
                {
                    // A band [bandStart, y) at full scale: a glitch when a row beside it is mostly dark, or when it is the frame.
                    var above = bandStart > 0 && CountAtLeast(plane.Slice((bandStart - 1) * width, width), ceiling / 2) < width / 2;
                    var below = y < height && CountAtLeast(plane.Slice(y * width, width), ceiling / 2) < width / 2;
                    if (above || below || (bandStart == 0 && y == height))
                    {
                        return true;
                    }
                    bandStart = -1;
                }
            }
        }
        return false;
    }

    /// <summary>The share of a row at full scale that marks a frame corrupt (<see cref="IsCorruptReadout"/>).</summary>
    public const double CorruptRowFraction = 0.9;

    // A sample within this fraction of full scale is at it: an 8-bit 254 (0.4 % under) is not, a 255 is.
    private const float FullScaleTolerance = 1e-3f;

    private static int CountAtLeast(ReadOnlySpan<float> row, float level)
    {
        var count = 0;
        foreach (var sample in row)
        {
            if (sample >= level)
            {
                count++;
            }
        }
        return count;
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
