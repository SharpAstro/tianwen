using System;
using System.Collections.Immutable;
using TianWen.Lib.Geometry;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>A frame's sharpness score (higher = sharper), keyed by its index in the stream.</summary>
/// <param name="Index">The frame's index in the stream.</param>
/// <param name="Score">Its sharpness; zero for a frame no stack should hold.</param>
/// <param name="Cut">Whether its planet is cut by the frame's edge or missing from it (<see cref="FrameGrader.IsCutOrEmpty"/>).</param>
public readonly record struct FrameGrade(int Index, float Score, bool Cut = false);

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

        // A batch of frames graded side by side (PlanetaryFrameBatches): a grade is the frame's own, and every estimator takes
        // its scratch per call, so the grades are the ones a frame-by-frame walk gives.
        var scores = new float[count];
        var cut = new bool[count];
        var every = ImmutableArray.CreateBuilder<int>(count);
        for (var i = 0; i < count; i++)
        {
            every.Add(i);
        }
        await PlanetaryFrameBatches.RunAsync(stream, every.MoveToImmutable(),
            (image, _, _) => GradeAndCut(estimator, image, region),
            (_, index, graded) => (scores[index], cut[index]) = graded,
            cancellationToken).ConfigureAwait(false);

        var grades = ImmutableArray.CreateBuilder<FrameGrade>(count);
        for (var i = 0; i < count; i++)
        {
            grades.Add(new FrameGrade(i, scores[i], cut[i]));
        }

        return WithoutCutFrames(grades.MoveToImmutable());
    }

    /// <summary>
    /// <paramref name="grades"/> with every cut frame (<see cref="FrameGrade.Cut"/>) scored zero, so no stack holds it, when at least
    /// <see cref="MinimumWholeFraction"/> of them are whole (<see cref="DropsCutFrames"/>); as they are otherwise.
    /// </summary>
    public static ImmutableArray<FrameGrade> WithoutCutFrames(ImmutableArray<FrameGrade> grades)
    {
        var cut = 0;
        foreach (var grade in grades)
        {
            cut += grade.Cut ? 1 : 0;
        }
        if (cut == 0 || !DropsCutFrames(grades.Length - cut, grades.Length))
        {
            return grades;
        }
        var builder = grades.ToBuilder();
        for (var i = 0; i < builder.Count; i++)
        {
            if (builder[i].Cut)
            {
                builder[i] = builder[i] with { Score = 0 };
            }
        }
        return builder.MoveToImmutable();
    }

    /// <summary>
    /// Whether cut frames are left out of a capture of which <paramref name="whole"/> of <paramref name="graded"/> frames hold their
    /// planet whole: when at least <see cref="MinimumWholeFraction"/> of them do. A planet drifting across an untracked Dobsonian's
    /// field leaves many frames cut, and a whole one in twenty is still a stack; a Moon filling the field leaves none whole, and its
    /// frames stay as graded.
    /// </summary>
    public static bool DropsCutFrames(int whole, int graded) => whole > 0 && whole >= MinimumWholeFraction * graded;

    /// <summary>The share of a capture's frames that must hold the planet whole before its cut frames are left out.</summary>
    public const double MinimumWholeFraction = 0.05;

    /// <summary>
    /// Whether <paramref name="frame"/>'s planet is cut by the frame's edge, or missing: its largest blob above the sky (the luminance
    /// above its mean by three standard deviations, joined by the pixels' edges) touches the edge, or holds fewer than
    /// <see cref="PlanetaryDisk.PlanetPixels"/>. A planet drifting out of an untracked Dobsonian's field did both on 2022-10-09's Saturn: the
    /// last sixth of the capture holds it half out of the frame and then not at all, the stacker kept those frames among the best half,
    /// and the ones it registered wrong summed into a second, partial Saturn beside the first, its edge the frame's own. The planet's own
    /// blob is read, never every bright pixel, so a moon at the frame's edge cuts nothing.
    /// </summary>
    public static bool IsCutOrEmpty(Image frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return PlanetaryDisk.BoundingBoxAndCut(frame).CutOrEmpty;
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
    /// <see cref="Grade"/>, and whether the frame's planet is cut by its edge or missing (<see cref="IsCutOrEmpty"/>), from one pass
    /// over the frame's luminance; the score is <see cref="Grade"/>'s, bit for bit. Whether a cut frame is left out is the capture's to
    /// say (<see cref="DropsCutFrames"/>), so the score is not zeroed here.
    /// </summary>
    public static (float Score, bool Cut) GradeAndCut(IFrameQualityEstimator estimator, Image frame, PixelRect region = default)
    {
        var (score, cut, _) = GradeCutAndBox(estimator, frame, region);
        return (score, cut);
    }

    /// <summary>
    /// <see cref="GradeAndCut"/> with the disk's box, <see cref="PlanetaryDisk.BoundingBox"/>'s at its defaults: a stack that registers the
    /// frame by that box takes it from here rather than scanning the frame for it again (the rolling stack, #1174).
    /// </summary>
    internal static (float Score, bool Cut, PixelRect Box) GradeCutAndBox(IFrameQualityEstimator estimator, Image frame, PixelRect region = default)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        var (box, cut) = PlanetaryDisk.BoundingBoxAndCut(frame);
        return (IsCorruptReadout(frame) ? 0f : estimator.Score(frame, region.IsEmpty ? box : region), cut, box);
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
    /// frame indices best-first, never a frame scored zero while any scores above it (a corrupt readout, a cut frame: a drift
    /// capture can hold more of those than the fraction leaves out). Always keeps at least one frame.
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
        var scored = 0;
        while (scored < sorted.Length && sorted[scored].Score > 0)
        {
            scored++;
        }
        keep = scored > 0 ? Math.Min(keep, scored) : keep;

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
