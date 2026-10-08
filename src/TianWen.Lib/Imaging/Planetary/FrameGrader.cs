using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using TianWen.Lib.Geometry;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>Why a frame is left out of a stack (<see cref="FrameGrader.LeftOutBecause(in FrameGrade, in FrameRun)"/>), or <see cref="None"/>.</summary>
public enum FrameExclusion
{
    /// <summary>Kept.</summary>
    None,

    /// <summary>Its planet is cut or missing, in a capture that drops cut frames (<see cref="FrameGrader.DropsCutFrames"/>).</summary>
    Cut,

    /// <summary>Its planet lies too long for the run (<see cref="FrameGrader.IsSmeared"/>).</summary>
    Smeared,

    /// <summary>Its planet is too faint for the run (<see cref="FrameGrader.IsDim"/>).</summary>
    Dim,
}

/// <summary>
/// What a run says about its frames (<see cref="FrameGrader.RunOf"/>): whether it drops its cut frames, and its whole frames' median
/// elongation and brightness, NaN when unread.
/// </summary>
public readonly record struct FrameRun(bool DropsCut, double Elongation, double Brightness);

/// <summary>A frame's sharpness score (higher = sharper), keyed by its index in the stream.</summary>
/// <param name="Index">The frame's index in the stream.</param>
/// <param name="Score">Its sharpness; zero for a frame no stack should hold.</param>
/// <param name="Cut">Whether its planet is cut by the frame's edge or missing from it (<see cref="FrameGrader.IsCutOrEmpty"/>).</param>
/// <param name="Elongation">How much longer than wide its planet lies (<see cref="PlanetaryDisk.Elongation"/>); NaN when unread or no planet.</param>
/// <param name="Smeared">Whether its planet lies too long for the run, the telescope moving during it, so it was left out
/// (<see cref="FrameGrader.SmearRatio"/>).</param>
public readonly record struct FrameGrade(int Index, float Score, bool Cut = false, float Elongation = float.NaN, bool Smeared = false, float Brightness = float.NaN, bool Dim = false);

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
    /// Where each capture file's own grades are kept between stacks (#1351), or null to grade every frame every time. A file found there
    /// is not graded again; what depends on the run (<see cref="WithoutCutSmearedOrDimFrames"/>) is decided again either way.
    /// </summary>
    public FrameGradeCache? Cache { get; init; }

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
        var elongation = new float[count];
        var brightness = new float[count];
        // Each capture file's own grades from the cache where it holds them; the frames of every other file graded, and kept.
        var files = Cache is not null ? FrameGradeCache.FilesOf(stream) : null;
        var entries = new string?[files?.Count ?? 0];
        var graded = new bool[count];
        if (Cache is { } cache && files is not null)
        {
            for (var f = 0; f < files.Count; f++)
            {
                var (path, start, frames) = files[f];
                entries[f] = cache.EntryFor(path, estimator.CacheKey, stream.Layout, region);
                if (entries[f] is { } entry && FrameGradeCache.Read(entry, frames) is { } kept)
                {
                    for (var i = 0; i < frames; i++)
                    {
                        (scores[start + i], cut[start + i], elongation[start + i], brightness[start + i]) = kept[i];
                        graded[start + i] = true;
                    }
                }
            }
        }
        var toGrade = ImmutableArray.CreateBuilder<int>(count);
        for (var i = 0; i < count; i++)
        {
            if (!graded[i])
            {
                toGrade.Add(i);
            }
        }
        if (toGrade.Count > 0)
        {
            await PlanetaryFrameBatches.RunAsync(stream, toGrade.ToImmutable(),
                (image, _, _) => GradeAndShape(estimator, image, region),
                (_, index, grade) => (scores[index], cut[index], elongation[index], brightness[index]) = grade,
                cancellationToken).ConfigureAwait(false);
            if (Cache is { } writer && files is not null)
            {
                for (var f = 0; f < files.Count; f++)
                {
                    var (_, start, frames) = files[f];
                    if (entries[f] is { } entry && !graded[start])
                    {
                        var own = new FrameGradeCache.OwnGrade[frames];
                        for (var i = 0; i < frames; i++)
                        {
                            own[i] = new FrameGradeCache.OwnGrade(scores[start + i], cut[start + i], elongation[start + i], brightness[start + i]);
                        }
                        writer.Write(entry, own);
                    }
                }
            }
        }

        var grades = ImmutableArray.CreateBuilder<FrameGrade>(count);
        for (var i = 0; i < count; i++)
        {
            grades.Add(new FrameGrade(i, scores[i], cut[i], elongation[i], Brightness: brightness[i]));
        }

        return WithoutCutSmearedOrDimFrames(grades.MoveToImmutable());
    }

    /// <summary>
    /// <paramref name="grades"/> with every cut frame (<see cref="FrameGrade.Cut"/>) scored zero, so no stack holds it, when at least
    /// <see cref="MinimumWholeFraction"/> of them are whole (<see cref="DropsCutFrames"/>), and every smeared one (its planet more than
    /// <see cref="SmearRatio"/> times as elongated as the run's whole frames are at their median, <see cref="IsSmeared"/>) scored zero
    /// and marked <see cref="FrameGrade.Smeared"/>; as they are otherwise.
    /// </summary>
    public static ImmutableArray<FrameGrade> WithoutCutSmearedOrDimFrames(ImmutableArray<FrameGrade> grades)
    {
        var run = RunOf(grades);
        var builder = grades.ToBuilder();
        var changed = false;
        for (var i = 0; i < builder.Count; i++)
        {
            var grade = builder[i];
            switch (LeftOutBecause(grade, run))
            {
                case FrameExclusion.Cut:
                    builder[i] = grade with { Score = 0 };
                    changed = true;
                    break;
                case FrameExclusion.Smeared:
                    builder[i] = grade with { Score = 0, Smeared = true };
                    changed = true;
                    break;
                case FrameExclusion.Dim:
                    builder[i] = grade with { Score = 0, Dim = true };
                    changed = true;
                    break;
            }
        }
        return changed ? builder.MoveToImmutable() : grades;
    }

    /// <summary>
    /// What <paramref name="grades"/>' run says about its frames: whether it drops its cut frames (<see cref="DropsCutFrames"/>), and its
    /// whole frames' median elongation (<see cref="RunElongation"/>) and brightness (<see cref="RunBrightness"/>).
    /// </summary>
    public static FrameRun RunOf(ImmutableArray<FrameGrade> grades)
    {
        var cut = 0;
        foreach (var grade in grades)
        {
            cut += grade.Cut ? 1 : 0;
        }
        return new FrameRun(cut > 0 && DropsCutFrames(grades.Length - cut, grades.Length), RunElongation(grades), RunBrightness(grades));
    }

    /// <summary>Whether, and why, <paramref name="grade"/> is left out of a stack of <paramref name="run"/>'s.</summary>
    public static FrameExclusion LeftOutBecause(in FrameGrade grade, in FrameRun run)
        => LeftOutBecause(grade.Cut, run.DropsCut, grade.Elongation, run.Elongation, grade.Brightness, run.Brightness);

    /// <summary>
    /// Whether, and why, a frame is left out of a stack (#1237, #1300, #1307): a <paramref name="cut"/> frame when the capture drops its cut
    /// frames (<paramref name="dropsCut"/>, <see cref="DropsCutFrames"/>), else a whole one smeared (<see cref="IsSmeared"/>) or dim
    /// (<see cref="IsDim"/>) against the run's medians. ONE rule for the batch's grades, the capture statistics and the live stack, which
    /// keeps running medians of its own (the audit on #1343 found it written out three times).
    /// </summary>
    public static FrameExclusion LeftOutBecause(bool cut, bool dropsCut, float elongation, double runElongation, float brightness, double runBrightness)
    {
        if (cut)
        {
            return dropsCut ? FrameExclusion.Cut : FrameExclusion.None;
        }
        return IsSmeared(elongation, runElongation) ? FrameExclusion.Smeared
            : IsDim(brightness, runBrightness) ? FrameExclusion.Dim
            : FrameExclusion.None;
    }

    /// <summary>
    /// How many times as elongated as the run's typical whole frame a frame's planet may lie before it was taken while the telescope
    /// MOVED, and is left out (#1300). A telescope that moves during a frame (a bump, a nudge, a slew) draws the planet out along a
    /// line: the long sharp edge of that line is the sharpest thing the gradient sees, so on the owner's 2021-08-01 Saturn one such
    /// frame (2.07 times the run's median) was every stack's reference,
    /// and every frame registered against it left the stack's left and top edges covered by no frame. On four captures off tracked
    /// mounts no frame lies past 1.06 times its run's median; on that capture 25 lie past 1.5.
    /// </summary>
    public const double SmearRatio = 1.5;

    /// <summary>
    /// Whether a frame whose planet lies <paramref name="elongation"/> times as long as wide is smeared in a run whose whole frames lie
    /// <paramref name="runElongation"/> at their median: past <see cref="SmearRatio"/> times it. Never when either is unread (NaN).
    /// </summary>
    public static bool IsSmeared(float elongation, double runElongation)
        => runElongation > 0 && elongation > SmearRatio * runElongation;

    /// <summary>
    /// The share of the run's typical brightness a frame's planet must reach, below which it is left out (#1307): its peak above the sky,
    /// against the run's whole frames at their median. Cloud, a bump or defocus all lower a planet's peak, and the grader divides its
    /// score by the frame's brightness squared, so a dim frame's noise reads as detail: the blurred last frame of the owner's 2021-08-19
    /// 21:54:54 Saturn, at 0.35 of the run, would have been every stack's reference. On thirteen captures off tracked mounts no frame
    /// lies below 0.81 of its run's median.
    /// </summary>
    public const double DimRatio = 0.5;

    /// <summary>
    /// Whether a frame whose planet's peak lies <paramref name="brightness"/> above its sky is dim in a run whose whole frames lie
    /// <paramref name="runBrightness"/> at their median: under <see cref="DimRatio"/> of it. Never when either is unread (NaN).
    /// </summary>
    public static bool IsDim(float brightness, double runBrightness) => runBrightness > 0 && brightness < DimRatio * runBrightness;

    /// <summary>The median <see cref="FrameGrade.Brightness"/> of <paramref name="grades"/>' whole frames, NaN with none read.</summary>
    public static double RunBrightness(ImmutableArray<FrameGrade> grades)
    {
        var read = new List<float>(grades.Length);
        foreach (var grade in grades)
        {
            if (!grade.Cut && float.IsFinite(grade.Brightness))
            {
                read.Add(grade.Brightness);
            }
        }
        return MedianOf(read);
    }

    /// <summary>The median <see cref="FrameGrade.Elongation"/> of <paramref name="grades"/>' whole frames, NaN with none read.</summary>
    public static double RunElongation(ImmutableArray<FrameGrade> grades)
    {
        var read = new List<float>(grades.Length);
        foreach (var grade in grades)
        {
            if (!grade.Cut && float.IsFinite(grade.Elongation))
            {
                read.Add(grade.Elongation);
            }
        }
        return MedianOf(read);
    }

    // The median of `values`, NaN when there are none; reorders the list (StatisticsHelper.MedianFast, the one median).
    internal static double MedianOf(List<float> values) => StatisticsHelper.MedianFast(CollectionsMarshal.AsSpan(values));

    /// <summary>
    /// Whether cut frames are left out of a capture of which <paramref name="whole"/> of <paramref name="graded"/> frames hold their
    /// planet whole: when at least <see cref="MinimumWholeFraction"/> of them do. A planet drifting across an untracked Dobsonian's
    /// field leaves many frames cut, and a whole one in twenty is still a stack; a Moon filling the field leaves none whole, and its
    /// frames stay as graded.
    /// </summary>
    public static bool DropsCutFrames(int whole, int graded) => whole > 0 && whole >= MinimumWholeFraction * graded;

    /// <summary>
    /// How many of <paramref name="grades"/> read as cut, either all left out or all kept by the capture's one rule
    /// (<see cref="DropsCutFrames"/>). Kept, the test is off for the whole capture, which a host must say: when every frame of the
    /// owner's 2021-08-19 Jupiter read as holding no planet, the cut test was off without a word and half a planet became the
    /// reference (#1307).
    /// </summary>
    public static (int LeftOut, int Kept) CutFrames(ImmutableArray<FrameGrade> grades)
    {
        var cut = 0;
        foreach (var grade in grades)
        {
            cut += grade.Cut ? 1 : 0;
        }
        return cut > 0 && DropsCutFrames(grades.Length - cut, grades.Length) ? (cut, 0) : (0, cut);
    }

    /// <summary>The share of a capture's frames that must hold the planet whole before its cut frames are left out.</summary>
    public const double MinimumWholeFraction = 0.05;

    /// <summary>
    /// Whether <paramref name="frame"/>'s planet is cut by the frame's edge, or missing: its largest blob above the sky (the luminance
    /// above <see cref="PlanetaryDisk.SmearLevel"/> of the way from its sky to its peak, joined by the pixels' edges) touches the edge,
    /// or holds fewer than <see cref="PlanetaryDisk.PlanetPixels"/>. The level is the planet's own, never the mean plus three
    /// deviations, which a disk filling a tenth of the frame never reaches (#1307): every frame then read as empty, and the test was
    /// off for the whole capture. A planet drifting out of an untracked Dobsonian's field did both on 2022-10-09's Saturn: the
    /// last sixth of the capture holds it half out of the frame and then not at all, the stacker kept those frames among the best half,
    /// and the ones it registered wrong summed into a second, partial Saturn beside the first, its edge the frame's own. The planet's own
    /// blob is read, never every bright pixel, so a moon at the frame's edge cuts nothing. A planet the camera cut and PIPP's crop then
    /// moved away from the frame's edge is cut too (#1291): a straight row or column of its light with the dark beyond it, inside the
    /// frame, which no blurred limb makes (<see cref="PlanetaryDisk.InteriorCutPixels"/>, <see cref="PlanetaryDisk.InteriorCutFraction"/>).
    /// </summary>
    public static bool IsCutOrEmpty(Image frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return PlanetaryDisk.BoundingBoxAndCut(frame).CutOrEmpty;
    }

    /// <summary>
    /// One frame's score by <paramref name="estimator"/> over <paramref name="region"/> (its own disk's bounding box when
    /// empty, or where that box finds no planet the planet's own blob, never the whole frame: #1307), and zero for a frame the camera corrupted as it read it out (<see cref="IsCorruptReadout"/>): never the
    /// reference, and no weight in a stack. The one grading rule, for the batch stacker, the live one and the capture
    /// statistics.
    /// </summary>
    public static float Grade(IFrameQualityEstimator estimator, Image frame, PixelRect region = default)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        return IsCorruptReadout(frame) ? 0f : estimator.Score(frame, region.IsEmpty ? PlanetaryDisk.BoundingBoxAndCut(frame).Graded : region);
    }

    /// <summary>
    /// <see cref="Grade"/>, whether the frame's planet is cut by its edge or missing (<see cref="IsCutOrEmpty"/>) and how elongated it
    /// lies (<see cref="PlanetaryDisk.Elongation"/>), from one pass over the frame's luminance; the score is <see cref="Grade"/>'s, bit
    /// for bit. Whether a cut or a smeared frame is left out is the capture's to say (<see cref="DropsCutFrames"/>,
    /// <see cref="IsSmeared"/>), so the score is not zeroed here.
    /// </summary>
    public static (float Score, bool Cut, float Elongation, float Brightness) GradeAndShape(IFrameQualityEstimator estimator, Image frame, PixelRect region = default)
    {
        var (score, cut, _, elongation, brightness) = GradeCutAndBox(estimator, frame, region);
        return (score, cut, elongation, brightness);
    }

    /// <summary>
    /// <see cref="GradeAndShape"/> with the disk's box, <see cref="PlanetaryDisk.BoundingBox"/>'s at its defaults: a stack that registers the
    /// frame by that box takes it from here rather than scanning the frame for it again (the rolling stack, #1174). The score is taken
    /// over that box, or where it finds no planet (and so is the whole frame) over the planet's own blob (#1307).
    /// </summary>
    internal static (float Score, bool Cut, PixelRect Box, float Elongation, float Brightness) GradeCutAndBox(IFrameQualityEstimator estimator, Image frame, PixelRect region = default)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        var (box, cut, elongation, graded, brightness) = PlanetaryDisk.BoundingBoxAndCut(frame);
        return (IsCorruptReadout(frame) ? 0f : estimator.Score(frame, region.IsEmpty ? graded : region), cut, box, elongation, brightness);
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

    // Counted in vector lanes, the same count (a NaN is at no level in either), since every frame of every stack reads each row (#1310).
    internal static int CountAtLeast(ReadOnlySpan<float> row, float level)
    {
        var lanes = Vector<float>.Count;
        var threshold = new Vector<float>(level);
        var counted = Vector<int>.Zero;
        var i = 0;
        for (; i + lanes <= row.Length; i += lanes)
        {
            counted -= Vector.GreaterThanOrEqual(new Vector<float>(row.Slice(i, lanes)), threshold);
        }
        var count = Vector.Sum(counted);
        for (; i < row.Length; i++)
        {
            if (row[i] >= level)
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

    /// <summary>
    /// The time of <paramref name="stream"/>'s best frame, graded as a stack grades it (#1308): the instant a run of several captures can
    /// be de-rotated to, such as the best frame of its middle file. Null for a capture without frame times.
    /// </summary>
    public async Task<DateTimeOffset?> BestFrameTimeAsync(IPlanetaryFrameStream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var best = Reference(await GradeAllAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false));
        return best >= 0 && stream.HasTimestamps ? stream.TimestampOf(best) : null;
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
