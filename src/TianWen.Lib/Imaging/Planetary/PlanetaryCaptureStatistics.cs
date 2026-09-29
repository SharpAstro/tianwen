using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Geometry;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>What to measure a capture with (<see cref="PlanetaryCaptureStatistics.MeasureAsync"/>).</summary>
/// <param name="Limb">The planet's disk as the ephemeris has it (<see cref="PlanetaryLimbFit.OptionsFor"/>): its apparent axis
/// ratio, which places the alignment points, and its phase, which the limb fit on the aligned means needs.</param>
public sealed record CaptureStatisticsOptions(LimbFitOptions Limb)
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

    /// <summary>
    /// Every how many frames the limb is fitted on a single frame, where its own shift put its disk: the disk's own motion,
    /// which the aligner's shift measures with an error of its own, and each frame's blur, with no alignment in it.
    /// </summary>
    public int LimbStride { get; init; } = 4;
}

/// <summary>One frame's limb, fitted alone (<see cref="CaptureStatisticsOptions.LimbStride"/>).</summary>
/// <param name="Frame">The frame's index.</param>
/// <param name="Quality">Its Laplacian score.</param>
/// <param name="EdgeWidth">Its limb's edge width, in pixels (<see cref="CaptureStatistics.LimbWidthAll"/>'s rule).</param>
/// <param name="Fit">The limb fit on it; null where it found no disk.</param>
public readonly record struct FrameLimb(int Frame, double Quality, double EdgeWidth, LimbFit? Fit);

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
/// <param name="SkyLevel">The sky's mean level, 1.3 to 1.6 radii from the reference frame's disk: the camera's offset and the planet's
/// scattered light together.</param>
/// <param name="SkyNoise">A pixel's noise there as recorded (quantisation included), from consecutive frames: the camera's and the
/// scattered light's shot noise together.</param>
/// <param name="DiskLevel">The disk's mean level over the local sky's (<paramref name="LocalSkyLevel"/>) inside 0.8 radii of the
/// reference, which blur barely moves.</param>
/// <param name="FarSkyLevel">The sky's level three radii and more from the disk, before the camera rounded it.</param>
/// <param name="FarSkyNoise">A pixel's noise there before rounding, frame to frame: the camera's read noise, which a synthetic capture
/// takes. Fitted with a level of each pixel's own (<see cref="PlanetaryCaptureStatistics.SkyByPixels"/>), since a sky's level can
/// slope over the frame: 2022-09-03's falls 0.17 ADU from right to left, which widened a pooled fit to 0.21. Read in the ring beside the disk instead, it carried the planet's scattered light; read off the rounded
/// values' spread, it was the rounding's.</param>
/// <param name="LocalSkyLevel">The sky's level 2.5 to 3.5 radii from the disk, past its halo and beside it, before rounding: the
/// camera's offset where the disk is, which a synthetic capture takes, and what <see cref="CaptureStatistics.Halo"/> stands on.</param>
/// <remarks>
/// No gain: a photon transfer from consecutive frames' differences read the seeing's changes, not the shot noise
/// (2022-09-03's Red gave no slope at all), so a synthetic capture's gain is set by the finest band's noise on the disk.
/// </remarks>
public readonly record struct CameraEstimate(double FullScaleAdu, double SkyLevel, double SkyNoise, double DiskLevel, double FarSkyLevel, double FarSkyNoise,
    double LocalSkyLevel);

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
/// <param name="LimbWidthAll">The limb's edge width in the mean of every frame aligned, in pixels: the seeing's blur with its
/// motion taken out, which pins the Fried parameter where the Laplacian, mostly noise in a faint capture, cannot.</param>
/// <param name="LimbWidthBest">The same in the mean of the best tenth by quality: the lucky frames' blur.</param>
/// <param name="LimbAll">The limb fit on the mean of every frame aligned: its Minnaert k is the planet's limb darkening, its blur
/// (core and wing) the capture's. The edge width alone cannot tell the two apart, since a limb that darkens more gently reads
/// wider under any blur. Null where the fit found no disk.</param>
/// <param name="LimbBest">The same on the best tenth's mean.</param>
/// <param name="FrameLimbs">The limb fitted on single frames (<see cref="CaptureStatisticsOptions.LimbStride"/>), in frame order:
/// the blur of a frame, where the means' is the frames' and their misregistration's together.</param>
/// <param name="LimbSeeingRms">The seeing's part of the disk's motion as the single frames' limb fits place it, per axis
/// (<see cref="SeeingRms"/>'s rule). On a synthetic capture the fit follows the truth to a few hundredths of a pixel, where the
/// aligner is off by about half a pixel, so this is the disk's motion and <see cref="SeeingRms"/> is that and the aligner's error
/// together.</param>
/// <param name="AlignerErrorRms">The aligner's shift against the limb fit's centre, RMS per axis about its mean (robust, from the
/// median absolute deviation).</param>
/// <param name="LimbRadiusRms">The single frames' fitted radius, its RMS about its median (robust).</param>
/// <param name="Halo">The planet's scattered light in the sky around it, annulus by annulus (<see cref="PlanetaryCaptureStatistics.HaloAnnuli"/>):
/// each one's level before rounding over the local sky's (<see cref="CameraEstimate.LocalSkyLevel"/>), in ADU. An 8-bit ring's noise hangs on it, a sky just over a whole ADU
/// hardly ever flipping and one just under it often.</param>
/// <param name="LimbOutliers">Fits more than five robust sigmas from the aligner on either axis, left out of the three above: a
/// fit that settled in a wrong minimum.</param>
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
    double LimbWidthAll,
    double LimbWidthBest,
    LimbFit? LimbAll,
    LimbFit? LimbBest,
    ImmutableArray<FrameLimb> FrameLimbs,
    double LimbSeeingRms,
    double AlignerErrorRms,
    double LimbRadiusRms,
    int LimbOutliers,
    ImmutableArray<double> Halo,
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

    /// <summary>The annuli <see cref="CaptureStatistics.Halo"/> is measured in, their edges in radii of the disk.</summary>
    public static readonly ImmutableArray<double> HaloAnnuli = [1.15, 1.3, 1.6, 2.0, 2.5];

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
            if (PlanetaryLimbFit.Start(plane, width, height, options.Limb.AxisRatio) is not { } disk)
            {
                return null;
            }
            var region = PlanetaryDisk.BoundingBox(reference);

            // Every frame's shift against the sharpest, and its light over the sky around its disk.
            var seconds = Seconds(stream, n, options.FramesPerSecond);
            var skyLevel = SkyLevel(plane, width, height, disk, options.FullScaleAdu);
            var (farSkyBright, farSkySpread) = FarSkyBright(plane, width, height, disk, options.FullScaleAdu);
            var shiftX = new double[n];
            var shiftY = new double[n];
            var flux = new double[n];
            var bestLevel = QualityPercentile(quality, 90);
            var workers = await ForEachFrameAsync(stream, all, () => new ShiftWorker(LuckyImagingStacker.AlignerFor(reference, region, alignTileSize: 0), width, height),
                (worker, index, frame) =>
                {
                    var shift = worker.Aligner.Estimate(frame, PlanetaryDisk.BoundingBox(frame));
                    shiftX[index] = shift.Dx;
                    shiftY[index] = shift.Dy;
                    flux[index] = LightAround(frame, disk.X + shift.Dx, disk.Y + shift.Dy, 1.3 * disk.Radius, skyLevel / options.FullScaleAdu);
                    frame.AccumulateTranslatedInto(worker.All, worker.AllWeight, (float)shift.Dx, (float)shift.Dy, 1f);
                    if (quality[index] >= bestLevel)
                    {
                        frame.AccumulateTranslatedInto(worker.Best, worker.BestWeight, (float)shift.Dx, (float)shift.Dy, 1f);
                    }
                }, cancellationToken).ConfigureAwait(false);
            var (meanAll, meanBest) = (ShiftWorker.Mean(workers, best: false), ShiftWorker.Mean(workers, best: true));
            var limbWidthAll = LimbWidth(meanAll, disk.X, disk.Y, disk.Radius, skyLevel / options.FullScaleAdu);
            var limbWidthBest = LimbWidth(meanBest, disk.X, disk.Y, disk.Radius, skyLevel / options.FullScaleAdu);
            var limbAll = PlanetaryLimbFit.Fit(meanAll.Plane, meanAll.Width, meanAll.Height, disk.X, disk.Y, disk.Radius, options.Limb);
            var limbBest = PlanetaryLimbFit.Fit(meanBest.Plane, meanBest.Width, meanBest.Height, disk.X, disk.Y, disk.Radius, options.Limb);
            var (fluxSlow, fluxFast) = FluxVariation(flux, seconds);
            progress?.Report("aligned every frame");

            // Single frames' limbs, each fitted where its shift put its disk.
            var stride = Math.Max(1, options.LimbStride);
            var sampled = new int[(n + stride - 1) / stride];
            for (var k = 0; k < sampled.Length; k++)
            {
                sampled[k] = k * stride;
            }
            var limbClock = Stopwatch.StartNew();
            // Each starts from the mean's fit, moved by the frame's shift, where there is one: beside its answer.
            var limbWorkers = await ForEachFrameAsync(stream, sampled, () => new List<FrameLimb>(), (own, index, frame) =>
            {
                var (cx, cy) = limbAll is { } mean ? (mean.CenterX + shiftX[index], mean.CenterY + shiftY[index]) : (disk.X + shiftX[index], disk.Y + shiftY[index]);
                var framePlane = frame.GetChannelSpan(0).ToArray();
                var fit = limbAll is { } like
                    ? PlanetaryLimbFit.Fit(framePlane, frame.Width, frame.Height, cx, cy, like, options.Limb)
                    : PlanetaryLimbFit.Fit(framePlane, frame.Width, frame.Height, cx, cy, disk.Radius, options.Limb);
                own.Add(new FrameLimb(index, quality[index], LimbWidth((framePlane, frame.Width, frame.Height), cx, cy, disk.Radius, skyLevel / options.FullScaleAdu), fit));
            }, cancellationToken).ConfigureAwait(false);
            var frameLimbs = new List<FrameLimb>();
            foreach (var own in limbWorkers)
            {
                frameLimbs.AddRange(own);
            }
            frameLimbs.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            progress?.Report($"fitted the limb on {frameLimbs.Count} single frames in {limbClock.Elapsed.TotalSeconds:0} s");
            var (limbSeeingRms, alignerErrorRms, limbRadiusRms, limbOutliers) = LimbMotion(frameLimbs, seconds, shiftX, shiftY, disk.X, disk.Y, options.MountWindowSeconds);

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
            var reach = (disk.Radius * options.Limb.AxisRatio) - (options.AlignmentPatchSize / Math.Sqrt(2));
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
            var sky = await MeasureSkyAsync(stream, n, shiftX, shiftY, disk, farSkyBright, farSkySpread, options.FullScaleAdu, cancellationToken).ConfigureAwait(false);
            var diskLevel = DiskLevel(plane, width, height, disk, options.FullScaleAdu) - sky.LocalLevel;
            var camera = new CameraEstimate(options.FullScaleAdu, skyLevel, Median(skyNoise), diskLevel, sky.FarLevel, sky.FarNoise, sky.LocalLevel);
            var halo = sky.Halo;

            return new CaptureStatistics(n, fps, referenceIndex, disk.X, disk.Y, disk.Radius, [.. shiftX], [.. shiftY], [.. mountX], [.. mountY], seeingRms, mountRate, mountWander, [.. flux], fluxSlow, fluxFast, limbWidthAll, limbWidthBest,
                limbAll, limbBest, [.. frameLimbs], limbSeeingRms, alignerErrorRms, limbRadiusRms, limbOutliers, halo, warp, [.. quality], QualityPercentilesOf(quality), Lag1(quality), bandNoise, camera);
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

    /// <summary>
    /// The version a saved file's statistics must carry to be read back (<see cref="TryLoadAsync"/>): raised whenever what is
    /// measured, or how, changes, so a file from before is measured again rather than compared as if it were current.
    /// </summary>
    public const int FileVersion = 5;

    /// <summary>
    /// Saves <paramref name="statistics"/> to <paramref name="path"/> under <paramref name="key"/> (the capture, its frames and the
    /// options they were measured with), so a real capture measured once is compared with many synthetic ones.
    /// </summary>
    public static async Task SaveAsync(CaptureStatistics statistics, string key, string path, CancellationToken cancellationToken)
    {
        var file = new CaptureStatisticsFile(FileVersion, key, statistics);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, file, PlanetaryStatisticsJsonContext.Default.CaptureStatisticsFile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The statistics saved at <paramref name="path"/> when they are this version's, under <paramref name="key"/>; else null.</summary>
    public static async Task<CaptureStatistics?> TryLoadAsync(string path, string key, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        await using var stream = File.OpenRead(path);
        var file = await JsonSerializer.DeserializeAsync(stream, PlanetaryStatisticsJsonContext.Default.CaptureStatisticsFile, cancellationToken).ConfigureAwait(false);
        return file is { Version: FileVersion } && file.Key == key ? file.Statistics : null;
    }

    /// <summary>The percentiles of <see cref="Percentiles"/> of <paramref name="values"/>, linearly interpolated.</summary>
    public static ImmutableArray<double> QualityPercentilesOf(ReadOnlySpan<double> values) => PercentilesOf(values, Percentiles);

    /// <summary>The given percentiles of <paramref name="values"/>, linearly interpolated between ranks.</summary>
    public static ImmutableArray<double> PercentilesOf(ReadOnlySpan<double> values, ImmutableArray<double> percentiles)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var result = ImmutableArray.CreateBuilder<double>(percentiles.Length);
        foreach (var p in percentiles)
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
    private static async Task<TState[]> ForEachFrameAsync<TState>(IPlanetaryFrameStream stream, int[] indices, Func<TState> state, Action<TState, int, Image> body,
        CancellationToken cancellationToken)
    {
        var workers = Math.Max(1, Math.Min(Environment.ProcessorCount, indices.Length));
        var states = new TState[workers];
        await Parallel.ForAsync(0, workers, new ParallelOptions { CancellationToken = cancellationToken }, async (worker, token) =>
        {
            var own = states[worker] = state();
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
        }).ConfigureAwait(false);
        return states;
    }

    // One worker of the shift pass: its own aligner, and its own share of the two means, summed once the pass is done.
    private sealed class ShiftWorker(GlobalAligner aligner, int width, int height)
    {
        public GlobalAligner Aligner { get; } = aligner;
        public float[][,] All { get; } = Image.CreateChannelData(1, height, width);
        public float[,] AllWeight { get; } = new float[height, width];
        public float[][,] Best { get; } = Image.CreateChannelData(1, height, width);
        public float[,] BestWeight { get; } = new float[height, width];

        // The workers' shares summed into one mean, row-major.
        public static (float[] Plane, int Width, int Height) Mean(ShiftWorker[] workers, bool best)
        {
            var first = best ? workers[0].Best[0] : workers[0].All[0];
            var (height, width) = (first.GetLength(0), first.GetLength(1));
            var plane = new float[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    double sum = 0, weight = 0;
                    foreach (var w in workers)
                    {
                        sum += best ? w.Best[0][y, x] : w.All[0][y, x];
                        weight += best ? w.BestWeight[y, x] : w.AllWeight[y, x];
                    }
                    plane[(y * width) + x] = weight > 0 ? (float)(sum / weight) : 0;
                }
            }
            return (plane, width, height);
        }
    }

    // The value at `percentile` of `values`, the nearest rank.
    private static double QualityPercentile(double[] values, double percentile)
    {
        return StatisticsHelper.NthSmallest((double[])values.Clone(), (int)Math.Round(percentile / 100 * (values.Length - 1)));
    }

    // The limb's edge width: along 72 rays from the disk's centre, the level just inside the limb (0.85 radii) over the sky,
    // over the steepest fall between 0.75 and 1.25 radii, in pixels; the median over the rays, so a moon on one does not
    // count. For a Gaussian blur of an edge that is sqrt(2 pi) sigma. The same rule for a real and a synthetic capture, whose
    // limbs are the same planet's in the same filter.
    private static double LimbWidth((float[] Plane, int Width, int Height) mean, double cx, double cy, double radius, double sky)
    {
        var (plane, width, height) = mean;
        double At(double x, double y)
        {
            if (x < 0 || y < 0 || x > width - 1 || y > height - 1)
            {
                return double.NaN;
            }
            var (x0, y0) = ((int)x, (int)y);
            var (x1, y1) = (Math.Min(x0 + 1, width - 1), Math.Min(y0 + 1, height - 1));
            var (tx, ty) = (x - x0, y - y0);
            var top = (plane[(y0 * width) + x0] * (1 - tx)) + (plane[(y0 * width) + x1] * tx);
            var bottom = (plane[(y1 * width) + x0] * (1 - tx)) + (plane[(y1 * width) + x1] * tx);
            return (top * (1 - ty)) + (bottom * ty);
        }
        var widths = new List<double>();
        for (var ray = 0; ray < 72; ray++)
        {
            var (sin, cos) = Math.SinCos(ray * Math.PI / 36);
            var inside = At(cx + (0.85 * radius * cos), cy + (0.85 * radius * sin)) - sky;
            var steepest = 0.0;
            for (var r = 0.75 * radius; r <= 1.25 * radius; r += 0.1)
            {
                var fall = (At(cx + ((r - 0.25) * cos), cy + ((r - 0.25) * sin)) - At(cx + ((r + 0.25) * cos), cy + ((r + 0.25) * sin))) / 0.5;
                steepest = double.IsNaN(fall) ? double.NaN : Math.Max(steepest, fall);
                if (double.IsNaN(steepest))
                {
                    break;
                }
            }
            if (steepest > 0 && inside > 0)
            {
                widths.Add(inside / steepest);
            }
        }
        return Median([.. widths]);
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

    // The disk's motion by its single frames' limb fits: the seeing's part of it, the aligner's disagreement with it and the
    // fitted radius's scatter (both robust), and how many fits sit so far from the aligner that they settled in a wrong minimum,
    // which are left out of all three. NaN with fewer than three fits.
    private static (double SeeingRms, double AlignerError, double RadiusRms, int Outliers) LimbMotion(List<FrameLimb> frames, double[] seconds,
        double[] shiftX, double[] shiftY, double diskX, double diskY, double window)
    {
        var fitted = new List<(int Frame, LimbFit Fit)>();
        foreach (var f in frames)
        {
            if (f.Fit is { } fit)
            {
                fitted.Add((f.Frame, fit));
            }
        }
        if (fitted.Count < 3)
        {
            return (double.NaN, double.NaN, double.NaN, 0);
        }
        var dx = fitted.Select(f => f.Fit.CenterX - diskX - shiftX[f.Frame]).ToArray();
        var dy = fitted.Select(f => f.Fit.CenterY - diskY - shiftY[f.Frame]).ToArray();
        var (mx, sx) = (Median(dx), RobustSigma(dx));
        var (my, sy) = (Median(dy), RobustSigma(dy));
        var kept = new List<int>();
        for (var i = 0; i < fitted.Count; i++)
        {
            if (Math.Abs(dx[i] - mx) <= 5 * sx && Math.Abs(dy[i] - my) <= 5 * sy)
            {
                kept.Add(i);
            }
        }
        if (kept.Count < 3)
        {
            return (double.NaN, double.NaN, double.NaN, fitted.Count - kept.Count);
        }
        var (_, _, seeing, _, _) = SplitMotion([.. kept.Select(i => seconds[fitted[i].Frame])], [.. kept.Select(i => fitted[i].Fit.CenterX)],
            [.. kept.Select(i => fitted[i].Fit.CenterY)], window);
        var alignerError = Math.Sqrt((Square(RobustSigma([.. kept.Select(i => dx[i])])) + Square(RobustSigma([.. kept.Select(i => dy[i])]))) / 2);
        return (seeing, alignerError, RobustSigma([.. kept.Select(i => fitted[i].Fit.EquatorialRadius)]), fitted.Count - kept.Count);

        static double Square(double v) => v * v;
    }

    // A robust standard deviation: 1.4826 times the median absolute deviation from the median.
    private static double RobustSigma(double[] values)
    {
        var median = Median(values);
        return 1.4826 * Median(Array.ConvertAll(values, v => Math.Abs(v - median)));
    }

    private static double Median(double[] values)
    {
        // MedianFast averages the two middle values of an even count, as this always has; the copy FindAll makes is its to permute.
        return StatisticsHelper.MedianFast(Array.FindAll(values, v => !double.IsNaN(v)));
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
    // The far sky: three radii and more from the disk, where the planet's scattered light has gone, and nothing a moon or a star
    // lights (more than two ADU, or five of its robust sigmas, over its median).
    private const double FarSkyRadii = 3;
    private const double FarSkyBrightAdu = 2;

    // The value, in ADU, above which a far-sky pixel is taken as lit by something: its median plus FarSkyBrightAdu or five robust
    // sigmas, whichever is more (an 8-bit sky's robust sigma is zero); and that robust sigma. Infinite and NaN when the frame
    // reaches no far sky.
    private static (double Bright, double Spread) FarSkyBright(float[] plane, int width, int height, (double X, double Y, double Radius) disk, double scale)
    {
        var values = new List<double>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (Math.Sqrt(((x - disk.X) * (x - disk.X)) + ((y - disk.Y) * (y - disk.Y))) >= FarSkyRadii * disk.Radius)
                {
                    values.Add(plane[(y * width) + x] * scale);
                }
            }
        }
        if (values.Count == 0)
        {
            return (double.PositiveInfinity, double.NaN);
        }
        var spread = RobustSigma([.. values]);
        return (Median([.. values]) + Math.Max(FarSkyBrightAdu, 5 * spread), spread);
    }

    // How many frames the sky's per-pixel pass reads, from the capture's first, and how many whole-ADU values either side of the
    // sky's median each pixel keeps count of; the annulus the local sky is read in.
    private const int SkyFrames = 300;
    private const int SkyBinsEachSide = 4;
    private static readonly (double Inner, double Outer) LocalSkyAnnulus = (2.5, 3.5);

    // The sky, from each pixel's own values over the first SkyFrames frames, around the disk where it stood on average meanwhile:
    // the far sky's level and read noise, the local sky's level, and each halo annulus's level over the local sky's. A pixel that
    // ever reads past `bright`, or outside the bins kept, is left out (a moon, a star, a hot pixel). A sky whose noise spans more
    // than an ADU (16 bits) is read by each pixel's own moments instead, where rounding no longer matters.
    private static async Task<(double FarLevel, double FarNoise, double LocalLevel, ImmutableArray<double> Halo)> MeasureSkyAsync(IPlanetaryFrameStream stream, int n,
        double[] shiftX, double[] shiftY, (double X, double Y, double Radius) disk, double bright, double spread, double scale, CancellationToken cancellationToken)
    {
        if (!(spread <= 1))
        {
            return await MeasureWideSkyAsync(stream, n, shiftX, shiftY, disk, bright, scale, cancellationToken).ConfigureAwait(false);
        }
        var frames = Math.Min(n, SkyFrames);
        var (cx, cy) = (disk.X + shiftX.AsSpan(0, frames).ToArray().Average(), disk.Y + shiftY.AsSpan(0, frames).ToArray().Average());
        const int bins = (2 * SkyBinsEachSide) + 1;
        short[]? counts = null;
        bool[]? left = null;
        var (width, height, first) = (0, 0, 0);
        for (var f = 0; f < frames; f++)
        {
            var frame = await stream.LoadAsync(f, cancellationToken).ConfigureAwait(false);
            try
            {
                var plane = frame.GetChannelSpan(0);
                if (counts is null)
                {
                    (width, height) = (frame.Width, frame.Height);
                    counts = new short[width * height * bins];
                    left = new bool[width * height];
                    first = (int)Math.Round(bright - FarSkyBrightAdu) - SkyBinsEachSide;
                }
                for (var p = 0; p < width * height; p++)
                {
                    var v = (int)Math.Round(plane[p] * scale);
                    var bin = v - first;
                    if (bin < 0 || bin >= bins || v > bright)
                    {
                        left![p] = true;
                    }
                    else
                    {
                        counts[(p * bins) + bin]++;
                    }
                }
            }
            finally
            {
                frame.Release();
            }
        }
        if (counts is null || left is null)
        {
            return (double.NaN, double.NaN, double.NaN, [.. Enumerable.Repeat(double.NaN, HaloAnnuli.Length - 1)]);
        }

        // The pixels of each region, by their distance from the disk's mean place in radii.
        List<int> Region(double inner, double outer)
        {
            var pixels = new List<int>();
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) / disk.Radius;
                    if (r >= inner && r < outer && !left[(y * width) + x])
                    {
                        pixels.Add((y * width) + x);
                    }
                }
            }
            return pixels;
        }
        var (farLevel, farNoise) = SkyByPixels(counts, bins, first, Region(FarSkyRadii, double.PositiveInfinity));
        var (localLevel, _) = SkyByPixels(counts, bins, first, Region(LocalSkyAnnulus.Inner, LocalSkyAnnulus.Outer));
        var halo = ImmutableArray.CreateBuilder<double>(HaloAnnuli.Length - 1);
        for (var j = 0; j + 1 < HaloAnnuli.Length; j++)
        {
            halo.Add(SkyByPixels(counts, bins, first, Region(HaloAnnuli[j], HaloAnnuli[j + 1])).Level - localLevel);
        }
        return (farLevel, farNoise, localLevel, halo.MoveToImmutable());
    }

    // MeasureSkyAsync for a sky whose noise spans ADU: each pixel's own mean and frame-to-frame variance, averaged over each region
    // (less the rounding's twelfth); a pixel's own variance holds no gradient over the frame.
    private static async Task<(double FarLevel, double FarNoise, double LocalLevel, ImmutableArray<double> Halo)> MeasureWideSkyAsync(IPlanetaryFrameStream stream, int n,
        double[] shiftX, double[] shiftY, (double X, double Y, double Radius) disk, double bright, double scale, CancellationToken cancellationToken)
    {
        var frames = Math.Min(n, SkyFrames);
        var (cx, cy) = (disk.X + shiftX.AsSpan(0, frames).ToArray().Average(), disk.Y + shiftY.AsSpan(0, frames).ToArray().Average());
        double[]? sum = null, squares = null;
        bool[]? left = null;
        var (width, height) = (0, 0);
        for (var f = 0; f < frames; f++)
        {
            var frame = await stream.LoadAsync(f, cancellationToken).ConfigureAwait(false);
            try
            {
                var plane = frame.GetChannelSpan(0);
                if (sum is null)
                {
                    (width, height) = (frame.Width, frame.Height);
                    (sum, squares, left) = (new double[width * height], new double[width * height], new bool[width * height]);
                }
                for (var p = 0; p < width * height; p++)
                {
                    var v = plane[p] * scale;
                    if (v > bright)
                    {
                        left![p] = true;
                    }
                    sum[p] += v;
                    squares![p] += v * v;
                }
            }
            finally
            {
                frame.Release();
            }
        }
        if (sum is null || squares is null || left is null)
        {
            return (double.NaN, double.NaN, double.NaN, [.. Enumerable.Repeat(double.NaN, HaloAnnuli.Length - 1)]);
        }
        (double Level, double Noise) Region(double inner, double outer)
        {
            double level = 0, variance = 0;
            var count = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var p = (y * width) + x;
                    var r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) / disk.Radius;
                    if (r >= inner && r < outer && !left[p])
                    {
                        var mean = sum[p] / frames;
                        level += mean;
                        variance += Math.Max(0, (squares[p] / frames) - (mean * mean));
                        count++;
                    }
                }
            }
            return count > 0 ? (level / count, Math.Sqrt(Math.Max(0, (variance / count * frames / Math.Max(1, frames - 1)) - (1.0 / 12)))) : (double.NaN, double.NaN);
        }
        var (farLevel, farNoise) = Region(FarSkyRadii, double.PositiveInfinity);
        var (localLevel, _) = Region(LocalSkyAnnulus.Inner, LocalSkyAnnulus.Outer);
        var halo = ImmutableArray.CreateBuilder<double>(HaloAnnuli.Length - 1);
        for (var j = 0; j + 1 < HaloAnnuli.Length; j++)
        {
            halo.Add(Region(HaloAnnuli[j], HaloAnnuli[j + 1]).Level - localLevel);
        }
        return (farLevel, farNoise, localLevel, halo.MoveToImmutable());
    }

    /// <summary>
    /// A patch of sky's level and noise before the camera rounded them, from each pixel's own values: <paramref name="counts"/> holds,
    /// for every pixel, how many frames read each of <paramref name="bins"/> whole values from <paramref name="first"/> on. The noise is
    /// the joint maximum likelihood with a level of each pixel's own, profiled out, so a level that slopes over the patch widens
    /// nothing (2022-09-03's sky falls 0.2 ADU across the frame): pooling the pixels widened it, and grouping them by their own mean
    /// narrowed it, each group being pixels whose noise happened to land their mean in it (0.151 for 0.17). The level is the pixels'
    /// mean level at that noise. Pixels with like histograms are fitted once. NaN for no pixels.
    /// </summary>
    internal static (double Level, double Noise) SkyByPixels(ReadOnlySpan<short> counts, int bins, int first, IReadOnlyList<int> pixels)
    {
        var distinct = new Dictionary<string, (double[] Counts, int Pixels)>();
        var key = new StringBuilder();
        foreach (var p in pixels)
        {
            key.Clear();
            double total = 0;
            for (var b = 0; b < bins; b++)
            {
                key.Append(counts[(p * bins) + b]).Append(',');
                total += counts[(p * bins) + b];
            }
            if (total == 0)
            {
                continue;
            }
            var k = key.ToString();
            if (distinct.TryGetValue(k, out var entry))
            {
                distinct[k] = (entry.Counts, entry.Pixels + 1);
            }
            else
            {
                var histogram = new double[bins];
                for (var b = 0; b < bins; b++)
                {
                    histogram[b] = counts[(p * bins) + b];
                }
                distinct[k] = (histogram, 1);
            }
        }
        if (distinct.Count == 0)
        {
            return (double.NaN, double.NaN);
        }
        var histograms = distinct.Values.ToArray();

        // A histogram's best level at a noise, and its log-likelihood there, by golden section over a step either side of its mean.
        (double Level, double Likelihood) BestLevel(double[] histogram, double noise)
        {
            double total = 0, sum = 0;
            for (var b = 0; b < bins; b++)
            {
                total += histogram[b];
                sum += histogram[b] * (first + b);
            }
            double Likelihood(double level)
            {
                double ll = 0;
                for (var b = 0; b < bins; b++)
                {
                    if (histogram[b] > 0)
                    {
                        ll += histogram[b] * Math.Log(Math.Max(PlanetaryDegrade.RoundedProbability(first + b, level, noise), 1e-300));
                    }
                }
                return ll;
            }
            return GoldenMax(Likelihood, (sum / total) - 1, (sum / total) + 1, 40);
        }
        double Profile(double noise)
        {
            double sum = 0;
            foreach (var (histogram, count) in histograms)
            {
                sum += count * BestLevel(histogram, noise).Likelihood;
            }
            return sum;
        }
        var (best, _) = GoldenMax(Profile, 0.01, 2.0, 40);
        double levelSum = 0, weight = 0;
        foreach (var (histogram, count) in histograms)
        {
            levelSum += count * BestLevel(histogram, best).Level;
            weight += count;
        }
        return (levelSum / weight, best);
    }

    // The maximum of a unimodal `f` on [lo, hi] by golden section: where, and its value.
    private static (double At, double Value) GoldenMax(Func<double, double> f, double lo, double hi, int iterations)
    {
        var ratio = (Math.Sqrt(5) - 1) / 2;
        var (a, b) = (hi - (ratio * (hi - lo)), lo + (ratio * (hi - lo)));
        var (fa, fb) = (f(a), f(b));
        for (var i = 0; i < iterations; i++)
        {
            if (fa < fb)
            {
                lo = a;
                (a, fa) = (b, fb);
                b = lo + (ratio * (hi - lo));
                fb = f(b);
            }
            else
            {
                hi = b;
                (b, fb) = (a, fa);
                a = hi - (ratio * (hi - lo));
                fa = f(a);
            }
        }
        return fa > fb ? (a, fa) : (b, fb);
    }

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
