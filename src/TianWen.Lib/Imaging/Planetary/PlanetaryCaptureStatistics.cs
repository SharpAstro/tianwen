using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Geometry;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>What to measure a capture with (<see cref="PlanetaryCaptureStatistics.MeasureAsync"/>).</summary>
/// <param name="AxisRatio">The planet's apparent polar over equatorial radius, from the ephemeris.</param>
public sealed record CaptureStatisticsOptions(double AxisRatio)
{
    /// <summary>A sample's full scale in ADU (255 for an 8-bit capture): the frames arrive in [0, 1].</summary>
    public double FullScaleAdu { get; init; } = 255;

    /// <summary>The frame rate of a capture without timestamps.</summary>
    public double? FramesPerSecond { get; init; }

    /// <summary>How many pairs of consecutive frames the warp and the noise are read from, spread over the capture.</summary>
    public int Pairs { get; init; } = 500;

    /// <summary>The alignment points' spacing on the reference frame, in pixels.</summary>
    public int AlignmentPointSpacing { get; init; } = 24;

    /// <summary>The most alignment points to track.</summary>
    public int MaxAlignmentPoints { get; init; } = 256;

    /// <summary>The alignment points' patch, a power of two.</summary>
    public int AlignmentPatchSize { get; init; } = 32;

    /// <summary>
    /// How many consecutive frames, aligned by their global shifts and averaged, the warp is read from. One frame of a faint
    /// 8-bit capture cannot place a patch: 2022-09-03's Red read a warp correlated nowhere, at the noise floor.
    /// </summary>
    public int WarpFrames { get; init; } = 1;

    /// <summary>The running mean that separates the mount's slow motion from the seeing's, in seconds.</summary>
    public double MountWindowSeconds { get; init; } = 1;

    /// <summary>How many a trous wavelet bands the noise is measured in, finest first.</summary>
    public int Bands { get; init; } = 4;
}

/// <summary>One a trous band's noise in one frame, in ADU: over the sky (1.3 to 1.6 radii) and over the disk (inside 0.8).</summary>
public readonly record struct BandNoise(int Band, double Sky, double Disk);

/// <summary>One bin of the warp's spatial correlation: point pairs this far apart, and how alike their displacements are.</summary>
/// <summary>What a warp's correlation length is: read where the correlation crossed 1/e, or only bounded.</summary>
public enum WarpLengthBound
{
    /// <summary>The correlation fell through 1/e between two bins with enough pairs.</summary>
    Measured,

    /// <summary>The correlation never fell to 1/e within the disk: the length is at least the widest separation.</summary>
    AtLeast,

    /// <summary>
    /// The correlation was below 1/e already at the closest separation with enough pairs: the length is at most that, and a
    /// warp too fine or too faint for the points is indistinguishable from their noise.
    /// </summary>
    AtMost,
}

public readonly record struct CorrelationBin(double Separation, double Correlation, int Pairs);

/// <summary>
/// The local warp: each alignment point's displacement over the global shift, less the point's own mean (the reference
/// frame's own warp) and the frame's mean over its points (the global shift's residue).
/// </summary>
/// <param name="Points">Alignment points tracked.</param>
/// <param name="Rms">The displacement's RMS per axis, in pixels.</param>
/// <param name="CorrelationLength">Where the correlation between two points' displacements falls to 1/e, in pixels.</param>
/// <param name="Bound">Whether the length was read, or only bounded.</param>
/// <param name="Lag1">The correlation of a point's displacement with its own a block of frames later.</param>
/// <param name="Curve">The correlation against separation.</param>
public sealed record WarpStatistics(int Points, double Rms, double CorrelationLength, WarpLengthBound Bound, double Lag1, ImmutableArray<CorrelationBin> Curve);

/// <summary>What the frames say about the camera, in ADU.</summary>
/// <param name="FullScaleAdu">A sample's full scale.</param>
/// <param name="SkyLevel">The sky's mean level, 1.3 to 1.6 radii from the reference frame's disk.</param>
/// <param name="SkyNoise">A sky pixel's noise as recorded (quantisation included), from consecutive frames.</param>
/// <param name="DiskLevel">The disk's mean level over the sky inside 0.8 radii of the reference, which blur barely moves.</param>
/// <remarks>
/// No gain: a photon transfer from consecutive frames' differences read the seeing's changes, not the shot noise
/// (2022-09-03's Red gave no slope at all), so a synthetic capture's gain is set by the finest band's noise on the disk.
/// </remarks>
public readonly record struct CameraEstimate(double FullScaleAdu, double SkyLevel, double SkyNoise, double DiskLevel);

/// <summary>A capture's statistics (<see cref="PlanetaryCaptureStatistics.MeasureAsync"/>).</summary>
/// <param name="Frames">Frames measured.</param>
/// <param name="FramesPerSecond">The capture's rate, from its timestamps.</param>
/// <param name="ReferenceIndex">The sharpest frame, which the shifts and the warp are measured against.</param>
/// <param name="DiskX">The reference frame's disk centre, x.</param>
/// <param name="DiskY">The reference frame's disk centre, y.</param>
/// <param name="DiskRadius">The reference frame's equatorial radius, from the disk's area.</param>
/// <param name="ShiftX">Each frame's disk over the reference's, x: its disk lies at the reference's plus this.</param>
/// <param name="ShiftY">The same in y.</param>
/// <param name="MountX">The shift's running mean, x: the mount's slow part, which a synthetic capture moves its disk by.</param>
/// <param name="MountY">The same in y.</param>
/// <param name="SeeingRms">The shift's RMS per axis once the running mean (the mount's part) is taken out.</param>
/// <param name="MountRate">The running mean's straight-line rate, in pixels a second: the mount's drift.</param>
/// <param name="MountWander">The running mean's RMS about that line, per axis: the mount's own wander.</param>
/// <param name="Flux">Each frame's light over the sky within 1.3 radii of its disk, over the capture's mean: scintillation and
/// transparency, which a synthetic capture replays.</param>
/// <param name="FluxSlowRms">The flux's RMS over the capture once each quarter second is averaged: its slow part.</param>
/// <param name="FluxFastRms">Consecutive frames' flux difference over the square root of two, RMS: its fast part and the noise.</param>
/// <param name="Warp">The local warp.</param>
/// <param name="Quality">Every frame's Laplacian score (the grader's), in frame order.</param>
/// <param name="QualityPercentiles">Its 5th, 25th, 50th, 75th and 95th percentiles.</param>
/// <param name="QualityLag1">The correlation of consecutive frames' scores.</param>
/// <param name="Noise">Each band's noise in one frame.</param>
/// <param name="Camera">What the frames say about the camera.</param>
public sealed record CaptureStatistics(
    int Frames,
    double FramesPerSecond,
    int ReferenceIndex,
    double DiskX,
    double DiskY,
    double DiskRadius,
    ImmutableArray<double> ShiftX,
    ImmutableArray<double> ShiftY,
    ImmutableArray<double> MountX,
    ImmutableArray<double> MountY,
    double SeeingRms,
    double MountRate,
    double MountWander,
    ImmutableArray<double> Flux,
    double FluxSlowRms,
    double FluxFastRms,
    WarpStatistics Warp,
    ImmutableArray<double> Quality,
    ImmutableArray<double> QualityPercentiles,
    double QualityLag1,
    ImmutableArray<BandNoise> Noise,
    CameraEstimate Camera);

/// <summary>
/// A lucky-imaging capture's statistics, measured one way for a real capture and a synthetic one alike, since a synthetic
/// capture is of use only where it is statistically the real one (docs/plans/planetary-restoration.md, R2). The five the
/// plan compares: the global shift's RMS once the mount's slow part is taken out, the warp's correlation length, the frame
/// quality's distribution (its percentiles over its median), each wavelet band's noise, and consecutive frames' quality
/// correlation. The rest (the shift series, the warp's amplitude and time correlation, the camera's levels and gain) is what
/// a synthetic capture is generated from.
/// <para>Mono captures only for now: a Bayer capture's planes are sampled at twice the pitch and need their own pass.</para>
/// </summary>
public static class PlanetaryCaptureStatistics
{
    /// <summary>The quality percentiles reported.</summary>
    public static readonly ImmutableArray<double> Percentiles = [5, 25, 50, 75, 95];

    /// <summary>
    /// Measures <paramref name="stream"/>, a mono capture of a planet. Null when the sharpest frame shows no disk. Frames are
    /// read in parallel, each worker with its own aligner; every result lands in its frame's own slot, so the answer does not
    /// depend on the thread count.
    /// </summary>
    public static async Task<CaptureStatistics?> MeasureAsync(IPlanetaryFrameStream stream, CaptureStatisticsOptions options, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        if (stream.Layout != PlanetaryFrameLayout.Mono)
        {
            throw new NotSupportedException($"Capture statistics measure a mono capture; this one is {stream.Layout}.");
        }
        var n = stream.FrameCount;
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 3);
        var all = new int[n];
        for (var i = 0; i < n; i++)
        {
            all[i] = i;
        }

        // Every frame's quality, the grader's score over its own disk.
        var quality = new double[n];
        var estimator = new LaplacianEnergyEstimator();
        await ForEachFrameAsync(stream, all, () => 0, (_, index, frame) => quality[index] = estimator.Score(frame, PlanetaryDisk.BoundingBox(frame)), cancellationToken)
            .ConfigureAwait(false);
        var referenceIndex = 0;
        for (var i = 1; i < n; i++)
        {
            if (quality[i] > quality[referenceIndex])
            {
                referenceIndex = i;
            }
        }
        progress?.Report($"graded {n} frames; the sharpest is {referenceIndex}");

        var reference = await stream.LoadAsync(referenceIndex, cancellationToken).ConfigureAwait(false);
        try
        {
            var (width, height) = (reference.Width, reference.Height);
            var plane = reference.GetChannelSpan(0).ToArray();
            if (PlanetaryLimbFit.Start(plane, width, height, options.AxisRatio) is not { } disk)
            {
                return null;
            }
            var region = PlanetaryDisk.BoundingBox(reference);

            // Every frame's shift against the sharpest, and its light over the sky around its disk.
            var seconds = Seconds(stream, n, options.FramesPerSecond);
            var skyLevel = SkyLevel(plane, width, height, disk, options.FullScaleAdu);
            var shiftX = new double[n];
            var shiftY = new double[n];
            var flux = new double[n];
            await ForEachFrameAsync(stream, all, () => LuckyImagingStacker.AlignerFor(reference, region, alignTileSize: 0), (aligner, index, frame) =>
            {
                var shift = aligner.Estimate(frame, PlanetaryDisk.BoundingBox(frame));
                shiftX[index] = shift.Dx;
                shiftY[index] = shift.Dy;
                flux[index] = LightAround(frame, disk.X + shift.Dx, disk.Y + shift.Dy, 1.3 * disk.Radius, skyLevel / options.FullScaleAdu);
            }, cancellationToken).ConfigureAwait(false);
            var (fluxSlow, fluxFast) = FluxVariation(flux, seconds);
            progress?.Report("aligned every frame");

            var fps = (n - 1) / (seconds[n - 1] - seconds[0]);
            var (mountX, mountY, seeingRms, mountRate, mountWander) = SplitMotion(seconds, shiftX, shiftY, options.MountWindowSeconds);

            // Pairs of consecutive frames, spread evenly over the capture.
            // Pairs of blocks of consecutive frames for the warp, each block's first two frames a pair for the noise.
            var block = Math.Max(1, options.WarpFrames);
            ArgumentOutOfRangeException.ThrowIfLessThan(n, (2 * block) + 1);
            var pairs = Math.Min(options.Pairs, n - (2 * block));
            var firsts = new int[pairs];
            for (var k = 0; k < pairs; k++)
            {
                firsts[k] = (int)((long)k * (n - (2 * block)) / pairs);
            }
            // Only points whose whole patch lies on the disk: a patch across the limb locks onto the edge and floats along it, and
            // one on a moon follows the moon (2022-09-03's Red, all of whose first 19 points read noise, reached 222 px apart
            // over a disk 98 px across).
            var reach = (disk.Radius * options.AxisRatio) - (options.AlignmentPatchSize / Math.Sqrt(2));
            var aps = FeatureDetector.DetectAlignmentPoints(reference, region, options.AlignmentPointSpacing, options.MaxAlignmentPoints)
                .RemoveAll(p => Math.Sqrt(((p.X - disk.X) * (p.X - disk.X)) + ((p.Y - disk.Y) * (p.Y - disk.Y))) > reach);
            var residuals = new AlignmentPointShift[pairs * 2][];
            var noise = new BandNoise[pairs][];
            var skyNoise = new double[pairs];
            await Parallel.ForAsync(0, pairs, new ParallelOptions { CancellationToken = cancellationToken }, async (k, token) =>
            {
                var a = firsts[k];
                if (aps.Length > 0)
                {
                    // Each block is its frames moved onto the reference and averaged, so a block's points are matched with no
                    // global shift left.
                    var matcher = AlignmentPointMatcher.FromReference(reference, aps, options.AlignmentPatchSize);
                    residuals[2 * k] = new AlignmentPointShift[aps.Length];
                    residuals[(2 * k) + 1] = new AlignmentPointShift[aps.Length];
                    var blockA = await MeanOnReferenceAsync(stream, a, block, shiftX, shiftY, reference, token).ConfigureAwait(false);
                    var blockB = await MeanOnReferenceAsync(stream, a + block, block, shiftX, shiftY, reference, token).ConfigureAwait(false);
                    try
                    {
                        matcher.Match(blockA, 0, 0, residuals[2 * k]);
                        matcher.Match(blockB, 0, 0, residuals[(2 * k) + 1]);
                    }
                    finally
                    {
                        blockA.Release();
                        blockB.Release();
                    }
                }
                var frameA = await stream.LoadAsync(a, token).ConfigureAwait(false);
                var frameB = await stream.LoadAsync(a + 1, token).ConfigureAwait(false);
                try
                {
                    (noise[k], skyNoise[k]) = PairNoise(frameA, frameB, shiftX[a + 1] - shiftX[a], shiftY[a + 1] - shiftY[a],
                        disk.X + shiftX[a], disk.Y + shiftY[a], disk.Radius, options);
                }
                finally
                {
                    frameA.Release();
                    frameB.Release();
                }
            }).ConfigureAwait(false);
            progress?.Report($"matched {aps.Length} alignment points on {pairs} pairs of {block}-frame means and differenced {pairs} pairs of consecutive frames");

            var warp = Warp(aps, residuals, options.AlignmentPatchSize, options.AlignmentPointSpacing);
            var bandNoise = MedianNoise(noise, options.Bands);
            var diskLevel = DiskLevel(plane, width, height, disk, options.FullScaleAdu) - skyLevel;
            var camera = new CameraEstimate(options.FullScaleAdu, skyLevel, Median(skyNoise), diskLevel);

            return new CaptureStatistics(n, fps, referenceIndex, disk.X, disk.Y, disk.Radius, [.. shiftX], [.. shiftY], [.. mountX], [.. mountY], seeingRms, mountRate, mountWander, [.. flux], fluxSlow, fluxFast,
                warp, [.. quality], QualityPercentilesOf(quality), Lag1(quality), bandNoise, camera);
        }
        finally
        {
            reference.Release();
        }
    }

    // Frames first .. first + count - 1, each moved onto the reference by its global shift (bilinear) and averaged.
    private static async Task<Image> MeanOnReferenceAsync(IPlanetaryFrameStream stream, int first, int count, double[] shiftX, double[] shiftY, Image reference,
        CancellationToken cancellationToken)
    {
        var channelAccum = Image.CreateChannelData(1, reference.Height, reference.Width);
        var weightAccum = new float[reference.Height, reference.Width];
        for (var i = first; i < first + count; i++)
        {
            var frame = await stream.LoadAsync(i, cancellationToken).ConfigureAwait(false);
            try
            {
                frame.AccumulateTranslatedInto(channelAccum, weightAccum, (float)shiftX[i], (float)shiftY[i], 1f);
            }
            finally
            {
                frame.Release();
            }
        }
        return PlanetaryMaster.NormalizeInPlace(channelAccum, weightAccum, reference.ImageMeta);
    }

    /// <summary>The percentiles of <see cref="Percentiles"/> of <paramref name="values"/>, linearly interpolated.</summary>
    public static ImmutableArray<double> QualityPercentilesOf(ReadOnlySpan<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var result = ImmutableArray.CreateBuilder<double>(Percentiles.Length);
        foreach (var p in Percentiles)
        {
            var at = p / 100 * (sorted.Length - 1);
            var lo = (int)Math.Floor(at);
            var hi = Math.Min(lo + 1, sorted.Length - 1);
            result.Add(sorted[lo] + ((sorted[hi] - sorted[lo]) * (at - lo)));
        }
        return result.MoveToImmutable();
    }

    /// <summary>The Pearson correlation of consecutive values.</summary>
    public static double Lag1(ReadOnlySpan<double> values)
    {
        double mean = 0;
        foreach (var v in values)
        {
            mean += v;
        }
        mean /= values.Length;
        double num = 0, den = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var d = values[i] - mean;
            den += d * d;
            if (i + 1 < values.Length)
            {
                num += d * (values[i + 1] - mean);
            }
        }
        return den > 0 ? num / den : 0;
    }

    // Loads each frame of `indices` on one of up to a processor's count of workers, each with its own state, and hands it to
    // `body`, which must write only its own frame's slots.
    private static Task ForEachFrameAsync<TState>(IPlanetaryFrameStream stream, int[] indices, Func<TState> state, Action<TState, int, Image> body,
        CancellationToken cancellationToken)
    {
        var workers = Math.Max(1, Math.Min(Environment.ProcessorCount, indices.Length));
        return Parallel.ForAsync(0, workers, new ParallelOptions { CancellationToken = cancellationToken }, async (worker, token) =>
        {
            var own = state();
            for (var k = worker; k < indices.Length; k += workers)
            {
                var frame = await stream.LoadAsync(indices[k], token).ConfigureAwait(false);
                try
                {
                    body(own, indices[k], frame);
                }
                finally
                {
                    frame.Release();
                }
            }
        });
    }

    // Each frame's time in seconds from the first: its timestamp, else its index over the stated rate.
    private static double[] Seconds(IPlanetaryFrameStream stream, int n, double? framesPerSecond)
    {
        var seconds = new double[n];
        var first = stream.HasTimestamps ? stream.TimestampOf(0) : null;
        var last = stream.HasTimestamps ? stream.TimestampOf(n - 1) : null;
        if (first is { } t0 && last is { } t1 && t1 > t0)
        {
            for (var i = 0; i < n; i++)
            {
                seconds[i] = stream.TimestampOf(i) is { } t ? (t - t0).TotalSeconds : double.NaN;
            }
            // A frame without a time takes its neighbours' spacing.
            for (var i = 1; i < n; i++)
            {
                if (double.IsNaN(seconds[i]))
                {
                    seconds[i] = seconds[i - 1] + ((t1 - t0).TotalSeconds / (n - 1));
                }
            }
            return seconds;
        }
        var rate = framesPerSecond ?? throw new ArgumentException("The capture has no timestamps: state its frame rate.", nameof(framesPerSecond));
        for (var i = 0; i < n; i++)
        {
            seconds[i] = i / rate;
        }
        return seconds;
    }

    // The shift series split into the mount's slow part (a centred running mean over `window` seconds) and the seeing's
    // (what is left): the seeing's RMS per axis, the mount's straight-line rate and its RMS about that line per axis.
    private static (double[] MountX, double[] MountY, double SeeingRms, double MountRate, double MountWander) SplitMotion(double[] seconds, double[] x, double[] y, double window)
    {
        var n = x.Length;
        var smoothX = new double[n];
        var smoothY = new double[n];
        double sumX = 0, sumY = 0;
        int lo = 0, hi = 0;
        for (var i = 0; i < n; i++)
        {
            while (hi < n && seconds[hi] <= seconds[i] + (window / 2))
            {
                sumX += x[hi];
                sumY += y[hi];
                hi++;
            }
            while (seconds[lo] < seconds[i] - (window / 2))
            {
                sumX -= x[lo];
                sumY -= y[lo];
                lo++;
            }
            smoothX[i] = sumX / (hi - lo);
            smoothY[i] = sumY / (hi - lo);
        }

        double seeing = 0;
        for (var i = 0; i < n; i++)
        {
            seeing += ((x[i] - smoothX[i]) * (x[i] - smoothX[i])) + ((y[i] - smoothY[i]) * (y[i] - smoothY[i]));
        }
        var (ax, bx) = Line(seconds, smoothX);
        var (ay, by) = Line(seconds, smoothY);
        double wander = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = smoothX[i] - (ax + (bx * seconds[i]));
            var dy = smoothY[i] - (ay + (by * seconds[i]));
            wander += (dx * dx) + (dy * dy);
        }
        return (smoothX, smoothY, Math.Sqrt(seeing / (2 * n)), Math.Sqrt((bx * bx) + (by * by)), Math.Sqrt(wander / (2 * n)));
    }

    private static (double Intercept, double Slope) Line(double[] t, double[] v)
    {
        double st = 0, sv = 0, stt = 0, stv = 0;
        for (var i = 0; i < t.Length; i++)
        {
            st += t[i];
            sv += v[i];
            stt += t[i] * t[i];
            stv += t[i] * v[i];
        }
        var n = t.Length;
        var slope = ((n * stv) - (st * sv)) / ((n * stt) - (st * st));
        return ((sv - (slope * st)) / n, slope);
    }

    // The warp from the matched points: a match further off than a quarter of the patch is a failed one and left out; each
    // point's mean over the frames (the reference's own warp) and each frame's mean over its points (the global shift's
    // residue) are taken out.
    private static WarpStatistics Warp(ImmutableArray<PixelPoint> aps, AlignmentPointShift[][] frames, int patchSize, int spacing)
    {
        var points = aps.Length;
        if (points < 2)
        {
            return new WarpStatistics(points, double.NaN, double.NaN, WarpLengthBound.AtLeast, double.NaN, []);
        }
        var f = frames.Length;
        var rx = new double[f, points];
        var ry = new double[f, points];
        var limit = patchSize / 4.0;
        for (var t = 0; t < f; t++)
        {
            for (var i = 0; i < points; i++)
            {
                var s = frames[t][i];
                var bad = Math.Abs(s.ResidualX) > limit || Math.Abs(s.ResidualY) > limit;
                rx[t, i] = bad ? double.NaN : s.ResidualX;
                ry[t, i] = bad ? double.NaN : s.ResidualY;
            }
        }
        for (var i = 0; i < points; i++)
        {
            double mx = 0, my = 0;
            var c = 0;
            for (var t = 0; t < f; t++)
            {
                if (!double.IsNaN(rx[t, i]))
                {
                    mx += rx[t, i];
                    my += ry[t, i];
                    c++;
                }
            }
            for (var t = 0; t < f && c > 0; t++)
            {
                rx[t, i] -= mx / c;
                ry[t, i] -= my / c;
            }
        }
        for (var t = 0; t < f; t++)
        {
            double mx = 0, my = 0;
            var c = 0;
            for (var i = 0; i < points; i++)
            {
                if (!double.IsNaN(rx[t, i]))
                {
                    mx += rx[t, i];
                    my += ry[t, i];
                    c++;
                }
            }
            for (var i = 0; i < points && c > 0; i++)
            {
                rx[t, i] -= mx / c;
                ry[t, i] -= my / c;
            }
        }

        // The amplitude, and each point's own power for the correlations.
        double total = 0;
        long count = 0;
        var power = new double[points];
        var powerCount = new int[points];
        for (var t = 0; t < f; t++)
        {
            for (var i = 0; i < points; i++)
            {
                if (!double.IsNaN(rx[t, i]))
                {
                    var p = (rx[t, i] * rx[t, i]) + (ry[t, i] * ry[t, i]);
                    total += p;
                    count++;
                    power[i] += p;
                    powerCount[i]++;
                }
            }
        }
        var rms = count > 0 ? Math.Sqrt(total / (2 * count)) : double.NaN;

        // The spatial correlation: each pair of points' mean dot product over their norms, binned by separation.
        var binWidth = spacing / 2.0;
        var sums = new Dictionary<int, (double Sum, int Pairs)>();
        for (var i = 0; i < points; i++)
        {
            for (var j = i + 1; j < points; j++)
            {
                double dot = 0;
                var c = 0;
                for (var t = 0; t < f; t++)
                {
                    if (!double.IsNaN(rx[t, i]) && !double.IsNaN(rx[t, j]))
                    {
                        dot += (rx[t, i] * rx[t, j]) + (ry[t, i] * ry[t, j]);
                        c++;
                    }
                }
                if (c < 10 || powerCount[i] == 0 || powerCount[j] == 0)
                {
                    continue;
                }
                var norm = Math.Sqrt(power[i] / powerCount[i] * (power[j] / powerCount[j]));
                if (norm <= 0)
                {
                    continue;
                }
                var dx = aps[i].X - aps[j].X;
                var dy = aps[i].Y - aps[j].Y;
                var bin = (int)(Math.Sqrt((dx * dx) + (dy * dy)) / binWidth);
                var (sum, pairs) = sums.GetValueOrDefault(bin);
                sums[bin] = (sum + (dot / c / norm), pairs + 1);
            }
        }
        var keys = new List<int>(sums.Keys);
        keys.Sort();
        var curve = ImmutableArray.CreateBuilder<CorrelationBin>(keys.Count);
        foreach (var key in keys)
        {
            var (sum, pairs) = sums[key];
            curve.Add(new CorrelationBin((key + 0.5) * binWidth, sum / pairs, pairs));
        }

        // Where the correlation first falls below 1/e, interpolated between two bins with enough pairs. Below 1/e already at the
        // first such bin is only an upper bound: interpolating from an assumed 1 at no separation read 2.2 px on one capture and
        // 5.4 on its synthetic twin from the same flat noise, as the first bin had enough pairs in one and not the other.
        var threshold = 1 / Math.E;
        (double Separation, double Correlation)? last = null;
        var length = double.NaN;
        var bound = WarpLengthBound.AtLeast;
        foreach (var bin in curve)
        {
            if (bin.Pairs < 5)
            {
                continue;
            }
            if (bin.Correlation < threshold)
            {
                (length, bound) = last is { } l
                    ? (l.Separation + ((bin.Separation - l.Separation) * (l.Correlation - threshold) / (l.Correlation - bin.Correlation)), WarpLengthBound.Measured)
                    : (bin.Separation, WarpLengthBound.AtMost);
                break;
            }
            last = (bin.Separation, bin.Correlation);
        }
        if (bound == WarpLengthBound.AtLeast)
        {
            length = last?.Separation ?? double.NaN;
        }

        // A point's displacement against its own a block later: blocks 2k and 2k + 1 are consecutive.
        double lagDot = 0, lagA = 0, lagB = 0;
        for (var t = 0; t + 1 < f; t += 2)
        {
            for (var i = 0; i < points; i++)
            {
                if (!double.IsNaN(rx[t, i]) && !double.IsNaN(rx[t + 1, i]))
                {
                    lagDot += (rx[t, i] * rx[t + 1, i]) + (ry[t, i] * ry[t + 1, i]);
                    lagA += (rx[t, i] * rx[t, i]) + (ry[t, i] * ry[t, i]);
                    lagB += (rx[t + 1, i] * rx[t + 1, i]) + (ry[t + 1, i] * ry[t + 1, i]);
                }
            }
        }
        var lag1 = lagA > 0 && lagB > 0 ? lagDot / Math.Sqrt(lagA * lagB) : double.NaN;
        return new WarpStatistics(points, rms, length, bound, lag1, curve.MoveToImmutable());
    }

    // One pair's noise: frame B moved onto A by their relative shift rounded to whole pixels (a sub-pixel shift would
    // interpolate the noise away), so only a pair whose shift is within a fifth of a pixel of whole is used; their difference
    // is taken to wavelet bands, and each band's clipped RMS over the sky and the disk, over the square root of two, is one
    // frame's noise. No bands when the pair's shift is too far from whole.
    private static (BandNoise[] Bands, double SkyNoise) PairNoise(Image a, Image b, double dx, double dy, double cx, double cy, double radius,
        CaptureStatisticsOptions options)
    {
        var (ix, iy) = ((int)Math.Round(dx), (int)Math.Round(dy));
        if (Math.Abs(dx - ix) > 0.2 || Math.Abs(dy - iy) > 0.2)
        {
            return ([], double.NaN);
        }
        var (width, height) = (a.Width, a.Height);
        var half = (int)Math.Ceiling(1.6 * radius) + 4;
        var x0 = Math.Max(Math.Max(0, -ix), (int)Math.Round(cx) - half);
        var y0 = Math.Max(Math.Max(0, -iy), (int)Math.Round(cy) - half);
        var x1 = Math.Min(Math.Min(width, width - ix), (int)Math.Round(cx) + half);
        var y1 = Math.Min(Math.Min(height, height - iy), (int)Math.Round(cy) + half);
        var (w, h) = (x1 - x0, y1 - y0);
        if (w < 32 || h < 32)
        {
            return ([], double.NaN);
        }

        var scale = options.FullScaleAdu;
        var planeA = a.GetChannelSpan(0);
        var planeB = b.GetChannelSpan(0);
        var difference = new float[w * h];
        var region = new byte[w * h];   // 1 sky, 2 disk
        var skySquares = 0.0;
        var skyCount = 0;
        for (var y = 0; y < h; y++)
        {
            var ya = (y + y0) * width;
            var yb = (y + y0 + iy) * width;
            for (var x = 0; x < w; x++)
            {
                var va = planeA[ya + x + x0] * scale;
                var vb = planeB[yb + x + x0 + ix] * scale;
                var d = vb - va;
                difference[(y * w) + x] = (float)d;
                var r = Math.Sqrt(((x + x0 - cx) * (x + x0 - cx)) + ((y + y0 - cy) * (y + y0 - cy))) / radius;
                if (r >= 1.3 && r <= 1.6)
                {
                    region[(y * w) + x] = 1;
                    skySquares += d * d;
                    skyCount++;
                }
                else if (r < 0.8)
                {
                    region[(y * w) + x] = 2;
                }
            }
        }

        var decomposition = ATrousWaveletTransform.Decompose(difference, w, h, options.Bands);
        var bands = new BandNoise[options.Bands];
        var sky = new List<double>();
        var disk = new List<double>();
        for (var j = 0; j < options.Bands; j++)
        {
            sky.Clear();
            disk.Clear();
            var margin = 2 << j;
            var detail = decomposition.Detail(j);
            for (var y = margin; y < h - margin; y++)
            {
                for (var x = margin; x < w - margin; x++)
                {
                    var i = (y * w) + x;
                    if (region[i] == 1)
                    {
                        sky.Add(detail[i]);
                    }
                    else if (region[i] == 2)
                    {
                        disk.Add(detail[i]);
                    }
                }
            }
            bands[j] = new BandNoise(j + 1, Rms(sky) / Math.Sqrt(2), ClippedRms(disk) / Math.Sqrt(2));
        }
        return (bands, skyCount > 0 ? Math.Sqrt(skySquares / skyCount / 2) : double.NaN);
    }

    // The sky's: plain, since a clip collapses on an 8-bit sky whose differences are mostly exactly zero (2022-09-03's Red
    // clipped every one of its rare steps of one away and read a fifth of its noise).
    private static double Rms(List<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }
        double sum = 0;
        foreach (var v in values)
        {
            sum += v * v;
        }
        return Math.Sqrt(sum / values.Count);
    }

    // The disk's: the RMS after dropping values beyond five of it, iterated, robust to a limb or a belt edge the difference did
    // not cancel.
    private static double ClippedRms(List<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }
        var limit = double.PositiveInfinity;
        var rms = 0.0;
        for (var pass = 0; pass < 5; pass++)
        {
            double sum = 0;
            var c = 0;
            foreach (var v in values)
            {
                if (Math.Abs(v) <= limit)
                {
                    sum += v * v;
                    c++;
                }
            }
            var next = c > 0 ? Math.Sqrt(sum / c) : 0;
            if (next == rms || next == 0)
            {
                return next;
            }
            rms = next;
            limit = 5 * rms;
        }
        return rms;
    }

    private static ImmutableArray<BandNoise> MedianNoise(BandNoise[][] pairs, int bands)
    {
        var result = ImmutableArray.CreateBuilder<BandNoise>(bands);
        var sky = new List<double>();
        var disk = new List<double>();
        for (var j = 0; j < bands; j++)
        {
            sky.Clear();
            disk.Clear();
            foreach (var pair in pairs)
            {
                if (pair is { Length: > 0 } && j < pair.Length)
                {
                    if (!double.IsNaN(pair[j].Sky))
                    {
                        sky.Add(pair[j].Sky);
                    }
                    if (!double.IsNaN(pair[j].Disk))
                    {
                        disk.Add(pair[j].Disk);
                    }
                }
            }
            result.Add(new BandNoise(j + 1, Median([.. sky]), Median([.. disk])));
        }
        return result.MoveToImmutable();
    }

    private static double Median(double[] values)
    {
        var finite = Array.FindAll(values, v => !double.IsNaN(v));
        if (finite.Length == 0)
        {
            return double.NaN;
        }
        Array.Sort(finite);
        var mid = finite.Length / 2;
        return finite.Length % 2 == 1 ? finite[mid] : (finite[mid - 1] + finite[mid]) / 2;
    }

    // A frame's light over `sky` within `radius` of (cx, cy), in the frame's own units.
    private static double LightAround(Image frame, double cx, double cy, double radius, double sky)
    {
        var plane = frame.GetChannelSpan(0);
        var (width, height) = (frame.Width, frame.Height);
        double sum = 0;
        var y0 = Math.Max(0, (int)Math.Floor(cy - radius));
        var y1 = Math.Min(height - 1, (int)Math.Ceiling(cy + radius));
        var x0 = Math.Max(0, (int)Math.Floor(cx - radius));
        var x1 = Math.Min(width - 1, (int)Math.Ceiling(cx + radius));
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= radius * radius)
                {
                    sum += plane[(y * width) + x] - sky;
                }
            }
        }
        return sum;
    }

    // The flux series made relative to its mean, in place, and its slow (quarter-second means) and fast (consecutive
    // differences) variation.
    private static (double Slow, double Fast) FluxVariation(double[] flux, double[] seconds)
    {
        double mean = 0;
        foreach (var f in flux)
        {
            mean += f;
        }
        mean /= flux.Length;
        for (var i = 0; i < flux.Length; i++)
        {
            flux[i] = mean != 0 ? flux[i] / mean : 1;
        }
        double fast = 0;
        for (var i = 1; i < flux.Length; i++)
        {
            fast += (flux[i] - flux[i - 1]) * (flux[i] - flux[i - 1]);
        }
        fast = Math.Sqrt(fast / (2 * Math.Max(1, flux.Length - 1)));
        // Quarter-second means, their spread about 1.
        double slow = 0;
        var blocks = 0;
        var start = 0;
        for (var i = 1; i <= flux.Length; i++)
        {
            if (i == flux.Length || seconds[i] - seconds[start] >= 0.25)
            {
                double sum = 0;
                for (var j = start; j < i; j++)
                {
                    sum += flux[j];
                }
                var m = sum / (i - start);
                slow += (m - 1) * (m - 1);
                blocks++;
                start = i;
            }
        }
        return (Math.Sqrt(slow / Math.Max(1, blocks)), fast);
    }

    // The sky's mean over 1.3 to 1.6 radii of the reference's disk, in ADU.
    private static double SkyLevel(float[] plane, int width, int height, (double X, double Y, double Radius) disk, double scale)
    {
        double sum = 0;
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = Math.Sqrt(((x - disk.X) * (x - disk.X)) + ((y - disk.Y) * (y - disk.Y))) / disk.Radius;
                if (r >= 1.3 && r <= 1.6)
                {
                    sum += plane[(y * width) + x];
                    count++;
                }
            }
        }
        return count > 0 ? sum / count * scale : double.NaN;
    }

    // The mean inside 0.8 radii of the reference's disk, in ADU.
    private static double DiskLevel(float[] plane, int width, int height, (double X, double Y, double Radius) disk, double scale)
    {
        double sum = 0;
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = Math.Sqrt(((x - disk.X) * (x - disk.X)) + ((y - disk.Y) * (y - disk.Y))) / disk.Radius;
                if (r < 0.8)
                {
                    sum += plane[(y * width) + x];
                    count++;
                }
            }
        }
        return count > 0 ? sum / count * scale : double.NaN;
    }
}
