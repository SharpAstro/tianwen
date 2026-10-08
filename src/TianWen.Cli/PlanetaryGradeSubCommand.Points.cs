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
        // Bilinear's transfer at band 2's middle (0.18 cycles a pixel) where the frame put the point between samples (#1350, H1).
        var phase = new double[frames * count];
        var fft3Estimator = new FftHighBandEstimator(FftBands[2]);
        var gradientEstimator = new GradientEnergyEstimator();
        var patchLength = GainPatch * GainPatch;
        var (sums, sumsLanczos) = (new double[count * patchLength], new double[count * patchLength]);
        var sumsLock = new Lock();
        await Parallel.ForAsync(0, frames, new ParallelOptions { CancellationToken = ct }, async (t, token) =>
        {
            var frame = await stream.LoadAsync(t, token);
            try
            {
                var sharpness = FrameSharpnessMap.Build(frame);
                var plane = frame.GetChannelSpan(0);
                var (local, localLanczos) = (new double[count * patchLength], new double[count * patchLength]);
                var patch = new float[patchLength];
                for (var k = 0; k < count; k++)
                {
                    var (x, y) = At(t, k);
                    var (ix, iy) = ((int)Math.Round(x), (int)Math.Round(y));
                    var i = (t * count) + k;
                    map[i] = ix >= 0 && iy >= 0 && ix < width && iy < height ? sharpness[iy, ix] : double.NaN;
                    var region = new PixelRect(ix - (PointPatch / 2), iy - (PointPatch / 2), PointPatch, PointPatch);
                    gradient[i] = gradientEstimator.Score(frame, region);
                    fft3[i] = fft3Estimator.Score(frame, region);
                    phase[i] = BilinearTransfer(x - Math.Floor(x)) * BilinearTransfer(y - Math.Floor(y));
                    PlanetaryPointQuality.Cut(plane, width, height, x, y, GainPatch, patch);
                    for (var s = 0; s < patch.Length; s++)
                    {
                        local[(k * patch.Length) + s] = patch[s];
                    }
                    PlanetaryPointQuality.CutLanczos3(plane, width, height, x, y, GainPatch, patch);
                    for (var s = 0; s < patch.Length; s++)
                    {
                        localLanczos[(k * patch.Length) + s] = patch[s];
                    }
                }
                // Each frame's patches added to every point's stack at once: one short hold a frame, against a lock-free merge of
                // count x 1,024 doubles.
                lock (sumsLock)
                {
                    for (var s = 0; s < sums.Length; s++)
                    {
                        sums[s] += local[s];
                        sumsLanczos[s] += localLanczos[s];
                    }
                }
            }
            finally
            {
                frame.Release();
            }
        });

        // Pass 2, each frame's patch gain at each point against the point's stack of every frame, cut bilinearly and by Lanczos-3.
        float[][] References(double[] from)
        {
            var references = new float[count][];
            for (var k = 0; k < count; k++)
            {
                references[k] = new float[patchLength];
                for (var s = 0; s < patchLength; s++)
                {
                    references[k][s] = (float)(from[(k * patchLength) + s] / frames);
                }
            }
            return references;
        }
        var (references, referencesLanczos) = (References(sums), References(sumsLanczos));
        var (referenceGain, referenceGainLanczos) = (new double[frames * count], new double[frames * count]);
        await Parallel.ForAsync(0, frames, new ParallelOptions { CancellationToken = ct }, async (t, token) =>
        {
            var frame = await stream.LoadAsync(t, token);
            try
            {
                var plane = frame.GetChannelSpan(0);
                var patch = new float[patchLength];
                for (var k = 0; k < count; k++)
                {
                    var (x, y) = At(t, k);
                    PlanetaryPointQuality.Cut(plane, width, height, x, y, GainPatch, patch);
                    referenceGain[(t * count) + k] = PlanetaryPointQuality.BandGain(patch, references[k], GainPatch, PointBand, PointPatch);
                    PlanetaryPointQuality.CutLanczos3(plane, width, height, x, y, GainPatch, patch);
                    referenceGainLanczos[(t * count) + k] = PlanetaryPointQuality.BandGain(patch, referencesLanczos[k], GainPatch, PointBand, PointPatch);
                }
            }
            finally
            {
                frame.Release();
            }
        });

        // Pooled (H3): the frame's whole-disk gradient rank and the point's local rank (Lanczos-3), added with equal weights, set before
        // measuring. And the oracle frame score (H4): the true quality averaged over the points.
        var wholeRank = Ranks(wholeGradient);
        var pooled = new double[frames * count];
        var oracleFrame = new double[frames];
        for (var k = 0; k < count; k++)
        {
            var column = new double[frames];
            for (var t = 0; t < frames; t++)
            {
                column[t] = referenceGainLanczos[(t * count) + k];
            }
            var localRank = Ranks(column);
            for (var t = 0; t < frames; t++)
            {
                pooled[(t * count) + k] = wholeRank[t] + localRank[t];
                oracleFrame[t] += trueGain[(t * count) + k] / count;
            }
        }

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
            ("the reference gain, cut by Lanczos-3", Ranking(referenceGainLanczos, perFrame: false)),
            ("the two ranks added (pooled)", Ranking(pooled, perFrame: false)),
            ("the true quality over the points", Ranking(oracleFrame, perFrame: true)),
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

        // #1350's hypotheses. H1: each cut's gain against bilinear's transfer where the frame put the point (the median over the points of
        // the Spearman over the frames, and of its size). H2: Lanczos-3's gain over the bilinear one's. H3: pooled over the whole-disk gradient.
        (double Median, double MedianSize) AgainstPhase(double[] score)
        {
            var (rhos, sizes) = (new List<double>(), new List<double>());
            for (var k = 0; k < count; k++)
            {
                var good = Enumerable.Range(0, frames).Where(t => double.IsFinite(score[(t * count) + k])).ToArray();
                if (good.Length > 2)
                {
                    var rho = StatisticsHelper.Spearman([.. good.Select(t => score[(t * count) + k])], [.. good.Select(t => phase[(t * count) + k])]);
                    rhos.Add(rho);
                    sizes.Add(Math.Abs(rho));
                }
            }
            return rhos.Count == 0 ? (double.NaN, double.NaN) : (rhos.Order().ElementAt(rhos.Count / 2), sizes.Order().ElementAt(sizes.Count / 2));
        }
        var (bilinearPhase, lanczosPhase, truthPhase) = (AgainstPhase(referenceGain), AgainstPhase(referenceGainLanczos), AgainstPhase(trueGain));
        consoleHost.WriteScrollable(string.Create(inv,
            $"    H1, each score against bilinear's transfer where the frame put the point (median Spearman, median size): the bilinear gain {bilinearPhase.Median:+0.000;-0.000} ({bilinearPhase.MedianSize:0.000}), Lanczos-3's {lanczosPhase.Median:+0.000;-0.000} ({lanczosPhase.MedianSize:0.000}), the true quality {truthPhase.Median:+0.000;-0.000} ({truthPhase.MedianSize:0.000}); claimed at least 0.2 for the bilinear gain: {(bilinearPhase.MedianSize >= 0.2 ? "holds" : "FAILS")}"));
        var (lanczosRho, pooledRho, oracleRho) = (rankings[5].Rho.Median, rankings[6].Rho.Median, rankings[7].Rho.Median);
        consoleHost.WriteScrollable(string.Create(inv,
            $"    H2, Lanczos-3's gain over the bilinear one's: {lanczosRho - patchGain:+0.000;-0.000} (claimed at least +0.1; {(lanczosRho - patchGain >= 0.1 ? "holds" : "FAILS")})"));
        consoleHost.WriteScrollable(string.Create(inv,
            $"    H3, the pooled ranks over the whole-disk gradient: {pooledRho - wholeFrame:+0.000;-0.000} (claimed at least +0.03; {(pooledRho - wholeFrame >= 0.03 ? "holds" : "FAILS")}); the true quality over the points ranks a point's frames at {oracleRho:+0.000;-0.000}, the most any score shared by a frame's points can"));

        // Pass 3, each point's best frames by each score stacked against the whole frames' best, the oracles' (H4) among them.
        await StackPointsAsync(stream, frames, truth, keeps, points, all, windowX, windowY, moveX, moveY, warps, first,
            [
                ("each point's best by its patch gain (bilinear)", referenceGain, 0),
                ("each point's best by its patch gain (Lanczos-3)", referenceGainLanczos, 0),
                ("each point's best by the two ranks added", pooled, 0),
                ("each point's best by its TRUE quality (oracle)", trueGain, 1),
            ],
            [
                ("the whole frames' best by the gradient", wholeGradient),
                ("the whole frames' best by their TRUE quality (oracle)", oracleFrame),
            ], ct);
        return 0;
    }

    // Bilinear interpolation's transfer at band 2's middle, 0.18 cycles a pixel, for a sample `t` of the way between two.
    private static double BilinearTransfer(double t) => Math.Sqrt(1 - (2 * t * (1 - t) * (1 - Math.Cos(2 * Math.PI * 0.18))));

    // Each value's rank among them, from 0 (the lowest); one not a number is ranked lowest.
    private static double[] Ranks(double[] values)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(i => Finite(values[i])).ToArray();
        var ranks = new double[values.Length];
        for (var r = 0; r < order.Length; r++)
        {
            ranks[order[r]] = r;
        }
        return ranks;
    }

    // Each point's best frames (by each point arm's score) against the whole frames' best (by each frame arm's) at each keep, every frame
    // registered onto the truth by its true motion and warp, each pixel weighted by the tents of the points about it (where no point
    // reaches, the choice of the frame arm the point arm names), scored by band against the truth.
    private async Task StackPointsAsync(IPlanetaryFrameStream stream, int frames, Target truth, double[] keeps, ImmutableArray<int> points,
        ImmutableArray<(double X, double Y)> all, int windowX, int windowY, double[] moveX, double[] moveY, ImmutableArray<SyntheticWarp> warps, int first,
        (string Name, double[] Score, int Fallback)[] pointArms, (string Name, double[] Score)[] frameArms, CancellationToken ct)
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

        // The selections: each point arm's best frames at each point, and each frame arm's best frames.
        var (pa, fa) = (pointArms.Length, frameArms.Length);
        var selectedAt = new bool[keeps.Length, pa][];
        var selectedWhole = new bool[keeps.Length, fa][];
        for (var q = 0; q < keeps.Length; q++)
        {
            var keep = Math.Max(1, (int)Math.Round(keeps[q] * frames));
            for (var a = 0; a < pa; a++)
            {
                var score = pointArms[a].Score;
                var chosen = new bool[frames * count];
                for (var k = 0; k < count; k++)
                {
                    foreach (var t in Enumerable.Range(0, frames).OrderByDescending(t => Finite(score[(t * count) + k])).Take(keep))
                    {
                        chosen[(t * count) + k] = true;
                    }
                }
                selectedAt[q, a] = chosen;
            }
            for (var b = 0; b < fa; b++)
            {
                var score = frameArms[b].Score;
                var chosen = new bool[frames];
                foreach (var t in Enumerable.Range(0, frames).OrderByDescending(t => Finite(score[t])).Take(keep))
                {
                    chosen[t] = true;
                }
                selectedWhole[q, b] = chosen;
            }
        }

        // Every frame registered onto the truth's square and added to each stack with its weights: per keep, the point arms, then the
        // frame arms.
        var perKeep = pa + fa;
        var stacks = keeps.Length * perKeep;
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
                            for (var a = 0; a < pa; a++)
                            {
                                var chosen = selectedAt[q, a];
                                var local = (1 - covered[i]) * (selectedWhole[q, pointArms[a].Fallback][t] ? 1.0 : 0.0);
                                foreach (var (k, w) in cover[i])
                                {
                                    local += chosen[(t * count) + k] ? w : 0;
                                }
                                var s = (((q * perKeep) + a) * size * size) + i;
                                (localSum[s], localWeight[s]) = (localSum[s] + (local * value), localWeight[s] + local);
                            }
                            for (var b = 0; b < fa; b++)
                            {
                                var whole = selectedWhole[q, b][t] ? 1.0 : 0.0;
                                var s = (((q * perKeep) + pa + b) * size * size) + i;
                                (localSum[s], localWeight[s]) = (localSum[s] + (whole * value), localWeight[s] + whole);
                            }
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
            var arms = pointArms.Select((arm, a) => (arm.Name, (q * perKeep) + a)).Concat(frameArms.Select((arm, b) => (arm.Name, (q * perKeep) + pa + b)));
            foreach (var (name, s) in arms)
            {
                var plane = new float[size * size];
                for (var i = 0; i < plane.Length; i++)
                {
                    var w = weight[(s * size * size) + i];
                    plane[i] = w > 0 ? (float)(sum[(s * size * size) + i] / w) : 0f;
                }
                var bands = truth.Fidelity(PlanetaryMetrics.Normalise(plane, size, size, truth.Disk));
                consoleHost.WriteScrollable(string.Create(inv,
                    $"        keep {keeps[q]:P0}, {name,-54} {string.Join(", ", bands.Select(b => b.Error.ToString("0.000", inv)))} ({bands.Sum(b => b.Error):0.000})"));
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
