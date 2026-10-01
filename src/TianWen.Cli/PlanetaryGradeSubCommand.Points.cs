using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Stat;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-grade --points</c> (docs/plans/planetary-restoration.md, R4 per-point, #1071): on a layered synthetic capture, whose
/// <c>.field</c> holds each field point's true quality frame by frame, the share of that quality which is the point's own, each local
/// estimator ranked against it at every point, and each point's best frames stacked against the whole frames' best.
/// </summary>
internal sealed partial class PlanetaryGradeSubCommand
{
    // The patch a point is scored over (an alignment point's), the patch its reference gain is read from (twice, so the a trous band's
    // reach stays inside), and the band the claims are read in.
    private const int PointPatch = 16;
    private const int GainPatch = 32;
    private const int PointBand = 2;
    // The points kept: every other field point of the 6 px grid, so 12 px apart (the stacker's spacing).
    private const double PointSpacing = 12;

    private async Task<int> RunPointsAsync(string input, IPlanetaryFrameStream stream, int first, int frames, Target truth, double[] keeps, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var (width, height) = (stream.Width, stream.Height);
        var fieldPath = SyntheticFieldFile.PathFor(input);
        var warpPath = SyntheticWarpFile.PathFor(input);
        var recordPath = Path.ChangeExtension(input, ".frames.csv");
        if (!File.Exists(fieldPath) || SyntheticFieldFile.Read(fieldPath) is not { } field || !File.Exists(warpPath) || SyntheticWarpFile.Read(warpPath) is not { } warps
            || ReadColumn(recordPath, "shift_x", first, frames) is not { } moveX || ReadColumn(recordPath, "shift_y", first, frames) is not { } moveY)
        {
            consoleHost.WriteError($"{input}: --points needs a layered twin's .field, .warp and .frames.csv beside it (planetary-degrade --high-r0)");
            return 1;
        }
        if (field.Frames.Length < first + frames || warps.Length < first + frames)
        {
            consoleHost.WriteError($"{input}: its .field holds {field.Frames.Length} frames and its .warp {warps.Length}, short of {first + frames}");
            return 1;
        }

        // The rendered window's corner on the detector with no shift: where each frame's window landed, less the whole pixels it moved.
        var (windowX, windowY) = (field.Frames[first].OriginX - (int)Math.Round(moveX[0]), field.Frames[first].OriginY - (int)Math.Round(moveY[0]));
        var all = field.Frames[0].Points;

        // The points 12 px apart whose patch lies wholly on the disk, counted from the field point nearest the disk's centre.
        var disk = truth.Disk;
        var (centreX, centreY) = (disk.X + truth.X0 - windowX, disk.Y + truth.Y0 - windowY);
        var origin = all.MinBy(p => Math.Pow(p.X - centreX, 2) + Math.Pow(p.Y - centreY, 2));
        var chosen = new List<int>();
        for (var k = 0; k < all.Length; k++)
        {
            var (u, v) = ((all[k].X - origin.X) / PointSpacing, (all[k].Y - origin.Y) / PointSpacing);
            if (Math.Abs(u - Math.Round(u)) > 1e-6 || Math.Abs(v - Math.Round(v)) > 1e-6)
            {
                continue;
            }
            var (tx, ty) = (windowX + all[k].X - truth.X0, windowY + all[k].Y - truth.Y0);
            var half = PointPatch / 2.0;
            if (disk.RadiiAt(tx - half, ty - half) < 1 && disk.RadiiAt(tx + half, ty - half) < 1 && disk.RadiiAt(tx - half, ty + half) < 1 && disk.RadiiAt(tx + half, ty + half) < 1)
            {
                chosen.Add(k);
            }
        }
        var points = chosen.ToImmutableArray();
        var count = points.Length;
        if (count < 2)
        {
            consoleHost.WriteError($"{input}: {count} field points lie wholly on the disk 12 px apart");
            return 1;
        }

        // Where frame t put point k on the detector: the point's place with no shift, the frame's motion, and the warp there.
        (double X, double Y) At(int t, int k)
        {
            var f = field.Frames[first + t];
            return (windowX + all[points[k]].X + moveX[t] + f.TiltX[points[k]], windowY + all[points[k]].Y + moveY[t] + f.TiltY[points[k]]);
        }

        // The true quality, point by point, and its share that is each point's own.
        var trueGain = new double[frames * count];
        for (var t = 0; t < frames; t++)
        {
            for (var k = 0; k < count; k++)
            {
                trueGain[(t * count) + k] = field.Frames[first + t].Gain(points[k], PointBand);
            }
        }
        var ownShare = PlanetaryPointQuality.OwnShare(trueGain, frames, count);

        // Pass 1, every frame scored at every point: the sharpness map the alignment-point stack weights by, the gradient and fft3 on the
        // patch, and the patch cut where the frame put the point, summed into each point's stack of every frame.
        var whole = await new FrameGrader(new GradientEnergyEstimator()).GradeAllAsync(stream, cancellationToken: ct);
        var wholeGradient = new double[frames];
        foreach (var g in whole)
        {
            wholeGradient[g.Index] = g.Score;
        }
        var (map, gradient, fft3) = (new double[frames * count], new double[frames * count], new double[frames * count]);
        var fft3Estimator = new FftHighBandEstimator(FftBands[2]);
        var gradientEstimator = new GradientEnergyEstimator();
        var sums = new double[count * GainPatch * GainPatch];
        var sumsLock = new Lock();
        await Parallel.ForAsync(0, frames, new ParallelOptions { CancellationToken = ct }, async (t, token) =>
        {
            var frame = await stream.LoadAsync(t, token);
            try
            {
                var sharpness = FrameSharpnessMap.Build(frame);
                var plane = frame.GetChannelSpan(0);
                var local = new double[count * GainPatch * GainPatch];
                var patch = new float[GainPatch * GainPatch];
                for (var k = 0; k < count; k++)
                {
                    var (x, y) = At(t, k);
                    var (ix, iy) = ((int)Math.Round(x), (int)Math.Round(y));
                    var i = (t * count) + k;
                    map[i] = ix >= 0 && iy >= 0 && ix < width && iy < height ? sharpness[iy, ix] : double.NaN;
                    var region = new PixelRect(ix - (PointPatch / 2), iy - (PointPatch / 2), PointPatch, PointPatch);
                    gradient[i] = gradientEstimator.Score(frame, region);
                    fft3[i] = fft3Estimator.Score(frame, region);
                    PlanetaryPointQuality.Cut(plane, width, height, x, y, GainPatch, patch);
                    for (var s = 0; s < patch.Length; s++)
                    {
                        local[(k * patch.Length) + s] = patch[s];
                    }
                }
                // Each frame's patches added to every point's stack at once: one short hold a frame, against a lock-free merge of
                // count x 1,024 doubles.
                lock (sumsLock)
                {
                    for (var s = 0; s < sums.Length; s++)
                    {
                        sums[s] += local[s];
                    }
                }
            }
            finally
            {
                frame.Release();
            }
        });

        // Pass 2, each frame's patch gain at each point against the point's stack of every frame.
        var references = new float[count][];
        for (var k = 0; k < count; k++)
        {
            references[k] = new float[GainPatch * GainPatch];
            for (var s = 0; s < references[k].Length; s++)
            {
                references[k][s] = (float)(sums[(k * references[k].Length) + s] / frames);
            }
        }
        var referenceGain = new double[frames * count];
        await Parallel.ForAsync(0, frames, new ParallelOptions { CancellationToken = ct }, async (t, token) =>
        {
            var frame = await stream.LoadAsync(t, token);
            try
            {
                var plane = frame.GetChannelSpan(0);
                var patch = new float[GainPatch * GainPatch];
                for (var k = 0; k < count; k++)
                {
                    var (x, y) = At(t, k);
                    PlanetaryPointQuality.Cut(plane, width, height, x, y, GainPatch, patch);
                    referenceGain[(t * count) + k] = PlanetaryPointQuality.BandGain(patch, references[k], GainPatch, PointBand, PointPatch);
                }
            }
            finally
            {
                frame.Release();
            }
        });

        // Each estimator ranked against the true quality at every point, Spearman over the frames, then the median over the points.
        (double Median, double P25, double P75) Ranking(double[] score, bool perFrame)
        {
            var rhos = new double[count];
            for (var k = 0; k < count; k++)
            {
                var (a, b) = (new double[frames], new double[frames]);
                for (var t = 0; t < frames; t++)
                {
                    (a[t], b[t]) = (perFrame ? score[t] : score[(t * count) + k], trueGain[(t * count) + k]);
                }
                var good = Enumerable.Range(0, frames).Where(t => double.IsFinite(a[t]) && double.IsFinite(b[t])).ToArray();
                rhos[k] = good.Length > 2 ? StatisticsHelper.Spearman([.. good.Select(t => a[t])], [.. good.Select(t => b[t])]) : double.NaN;
            }
            var sorted = rhos.Where(double.IsFinite).Order().ToArray();
            return sorted.Length == 0 ? (double.NaN, double.NaN, double.NaN) : (sorted[sorted.Length / 2], sorted[sorted.Length / 4], sorted[3 * sorted.Length / 4]);
        }
        var rankings = new (string Name, (double Median, double P25, double P75) Rho)[]
        {
            ("the sharpness map at the point", Ranking(map, perFrame: false)),
            ("the gradient on the patch", Ranking(gradient, perFrame: false)),
            ("fft3 on the patch", Ranking(fft3, perFrame: false)),
            ("the reference gain on the patch", Ranking(referenceGain, perFrame: false)),
            ("the frame's whole-disk gradient", Ranking(wholeGradient, perFrame: true)),
        };

        consoleHost.WriteScrollable(string.Create(inv,
            $"{Path.GetFileName(input)}: {count} points 12 px apart wholly on the disk, frames {first} to {first + frames - 1}; band {PointBand}'s true quality from {Path.GetFileName(fieldPath)}"));
        consoleHost.WriteScrollable(string.Create(inv, $"    the share of a point's true quality that is its own: {ownShare:P1} (claimed at least 20 %; {(ownShare >= 0.2 ? "holds" : "FAILS: the kill line")})"));
        consoleHost.WriteScrollable("    each estimator ranked against a point's true quality (Spearman over the frames; the median over the points, and its quartiles):");
        foreach (var (name, (median, p25, p75)) in rankings)
        {
            consoleHost.WriteScrollable(string.Create(inv, $"        {name,-34} {median:+0.000;-0.000}   ({p25:+0.000;-0.000} to {p75:+0.000;-0.000})"));
        }
        var (patchGain, wholeFrame, sharpnessMap, patchGradient) = (rankings[3].Rho.Median, rankings[4].Rho.Median, rankings[0].Rho.Median, rankings[1].Rho.Median);
        consoleHost.WriteScrollable(string.Create(inv,
            $"    the reference gain on the patch over the whole-disk gradient: {patchGain - wholeFrame:+0.000;-0.000} (claimed at least +0.1; {(patchGain - wholeFrame >= 0.1 ? "holds" : "FAILS")})"));
        consoleHost.WriteScrollable(string.Create(inv,
            $"    the sharpness map against the gradient on the patch: {sharpnessMap - patchGradient:+0.000;-0.000} (claimed within 0.05; {(Math.Abs(sharpnessMap - patchGradient) <= 0.05 ? "holds" : "FAILS")})"));

        // Pass 3, each point's best frames by its patch gain stacked against the whole frames' best by the gradient.
        await StackPointsAsync(stream, frames, truth, keeps, points, all, windowX, windowY, moveX, moveY, warps, first, referenceGain, wholeGradient, ct);
        return 0;
    }

    // Each point's best frames (by `pointScore`) against the whole frames' best (by `frameScore`) at each keep, every frame registered onto
    // the truth by its true motion and warp, each pixel weighted by the tents of the points about it (the whole frames' choice where no
    // point reaches), scored by band against the truth.
    private async Task StackPointsAsync(IPlanetaryFrameStream stream, int frames, Target truth, double[] keeps, ImmutableArray<int> points,
        ImmutableArray<(double X, double Y)> all, int windowX, int windowY, double[] moveX, double[] moveY, ImmutableArray<SyntheticWarp> warps, int first,
        double[] pointScore, double[] frameScore, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var (width, height, size, count) = (stream.Width, stream.Height, truth.Size, points.Length);

        // Each truth sample's covering points and their tents (half a spacing wide either way), and their sum.
        var cover = new List<(int Point, double Weight)>[size * size];
        var covered = new double[size * size];
        for (var i = 0; i < cover.Length; i++)
        {
            cover[i] = [];
        }
        for (var k = 0; k < count; k++)
        {
            var (px, py) = (windowX + all[points[k]].X - truth.X0, windowY + all[points[k]].Y - truth.Y0);
            for (var y = (int)Math.Ceiling(py - PointSpacing); y <= (int)Math.Floor(py + PointSpacing); y++)
            {
                for (var x = (int)Math.Ceiling(px - PointSpacing); x <= (int)Math.Floor(px + PointSpacing); x++)
                {
                    if (x < 0 || y < 0 || x >= size || y >= size)
                    {
                        continue;
                    }
                    var w = Math.Max(0, 1 - (Math.Abs(x - px) / PointSpacing)) * Math.Max(0, 1 - (Math.Abs(y - py) / PointSpacing));
                    if (w > 0)
                    {
                        cover[(y * size) + x].Add((k, w));
                        covered[(y * size) + x] += w;
                    }
                }
            }
        }

        // The selections: each point's best frames, and the whole frames' best.
        var selectedAt = new bool[keeps.Length][];
        var selectedWhole = new bool[keeps.Length][];
        for (var q = 0; q < keeps.Length; q++)
        {
            var keep = Math.Max(1, (int)Math.Round(keeps[q] * frames));
            selectedAt[q] = new bool[frames * count];
            for (var k = 0; k < count; k++)
            {
                foreach (var t in Enumerable.Range(0, frames).OrderByDescending(t => Finite(pointScore[(t * count) + k])).Take(keep))
                {
                    selectedAt[q][(t * count) + k] = true;
                }
            }
            selectedWhole[q] = new bool[frames];
            foreach (var t in Enumerable.Range(0, frames).OrderByDescending(t => Finite(frameScore[t])).Take(keep))
            {
                selectedWhole[q][t] = true;
            }
        }

        // Every frame registered onto the truth's square and added to each stack with its weights.
        var stacks = keeps.Length * 2;
        var sum = new double[stacks * size * size];
        var weight = new double[stacks * size * size];
        var merge = new Lock();
        await Parallel.ForAsync(0, frames, new ParallelOptions { CancellationToken = ct }, async (t, token) =>
        {
            var frame = await stream.LoadAsync(t, token);
            try
            {
                var plane = frame.GetChannelSpan(0);
                var warp = warps[first + t];
                var (localSum, localWeight) = (new double[sum.Length], new double[weight.Length]);
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        // The truth's sample at (x, y) is where the frame's motion and the warp there put it.
                        var (dx, dy) = (truth.X0 + x + moveX[t], truth.Y0 + y + moveY[t]);
                        var (wx, wy) = warp.AtWindow(dx - warp.OriginX, dy - warp.OriginY);
                        var value = Bilinear(plane, width, height, dx + wx, dy + wy);
                        var i = (y * size) + x;
                        for (var q = 0; q < keeps.Length; q++)
                        {
                            var whole = selectedWhole[q][t] ? 1.0 : 0.0;
                            var local = (1 - covered[i]) * whole;
                            foreach (var (k, w) in cover[i])
                            {
                                local += selectedAt[q][(t * count) + k] ? w : 0;
                            }
                            var (a, b) = ((2 * q * size * size) + i, (((2 * q) + 1) * size * size) + i);
                            (localSum[a], localWeight[a]) = (localSum[a] + (local * value), localWeight[a] + local);
                            (localSum[b], localWeight[b]) = (localSum[b] + (whole * value), localWeight[b] + whole);
                        }
                    }
                }
                // One merge a frame, against holding the stacks for every sample.
                lock (merge)
                {
                    for (var s = 0; s < sum.Length; s++)
                    {
                        sum[s] += localSum[s];
                        weight[s] += localWeight[s];
                    }
                }
            }
            finally
            {
                frame.Release();
            }
        });

        consoleHost.WriteScrollable("    stacked, every frame registered onto the truth by its true motion and warp: band errors 1 to 4 (sum)");
        for (var q = 0; q < keeps.Length; q++)
        {
            foreach (var (name, s) in new[] { ("each point's best by its patch gain", 2 * q), ("the whole frames' best by the gradient", (2 * q) + 1) })
            {
                var plane = new float[size * size];
                for (var i = 0; i < plane.Length; i++)
                {
                    var w = weight[(s * size * size) + i];
                    plane[i] = w > 0 ? (float)(sum[(s * size * size) + i] / w) : 0f;
                }
                var bands = truth.Fidelity(PlanetaryMetrics.Normalise(plane, size, size, truth.Disk));
                consoleHost.WriteScrollable(string.Create(inv,
                    $"        keep {keeps[q]:P0}, {name,-40} {string.Join(", ", bands.Select(b => b.Error.ToString("0.000", inv)))} ({bands.Sum(b => b.Error):0.000})"));
            }
        }
    }

    private static double Finite(double v) => double.IsFinite(v) ? v : double.NegativeInfinity;

    private static float Bilinear(ReadOnlySpan<float> plane, int width, int height, double x, double y)
    {
        var (x0, y0) = ((int)Math.Floor(x), (int)Math.Floor(y));
        if (x0 < 0 || y0 < 0 || x0 + 1 >= width || y0 + 1 >= height)
        {
            return 0f;
        }
        var (tx, ty) = (x - x0, y - y0);
        var top = (plane[(y0 * width) + x0] * (1 - tx)) + (plane[(y0 * width) + x0 + 1] * tx);
        var bottom = (plane[((y0 + 1) * width) + x0] * (1 - tx)) + (plane[((y0 + 1) * width) + x0 + 1] * tx);
        return (float)((top * (1 - ty)) + (bottom * ty));
    }
}
