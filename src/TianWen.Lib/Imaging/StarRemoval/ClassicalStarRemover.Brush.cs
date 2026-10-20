using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

// R0's brush (StarlessBrush): what runs after the builder's own fill when the options carry one.
public static partial class ClassicalStarRemover
{
    // The field's halo profile is read out to this radius, its sky in the 10 px beyond.
    private const int HaloMaxPx = 120;
    private const int HaloProfileStars = 40;
    private const int MaxFootprintPx = 150;
    private const int MinRingSamples = 24;
    private const int RingWidenings = 3;
    private const int GrainTilePx = 1024;
    private const int GrainRealisations = 8;
    private const int GrainCalibrationBlockPx = 128;
    private const int LevelBlockPx = 32;
    private const double PlanarSpread = 1.5;
    private const double PitSigma = 3.0;
    private static readonly int[] SmoothFillRadii = [4, 8, 16, 32, 64, 128];
    private const double SmoothFillPixels = 64.0;
    private const int TextureBlockPx = 64;
    private const int TextureHighPassPx = 4;

    private sealed partial class Builder
    {
        // Taken: the builder subtracted this star, so its plate there is the star's model taken out of the data.
        private readonly record struct BrushEntry(double X, double Y, double Radius, bool Taken);

        // What the brush painted, and which of it by a ring's plane: StarlessPlate.BrushFootprints and BrushPlanes.
        private BitMatrix? _brushFootprints;
        private BitMatrix? _brushPlanes;

        // The plate's own fine texture round the footprints, per TextureBlockPx block: each channel's robust spread after a
        // TextureHighPassPx local mean is taken out (over the pixels no footprint or fill covers), and how far the channels
        // move together there. On an empty sky that is the noise, every channel its own; in a cluster it is the unresolved
        // stars, several times the noise and the same in every channel. Grain drawn at the noise alone left flat patches in
        // Omega Cen's outskirts, and grain at the texture's level with every channel its own painted the core in colour noise.
        private sealed class TextureMap(int blocksX, int blocksY, double[][] sigma, double[] rho, double gain)
        {
            // Bilinear between block centres.
            private (int I0, int I1, int J0, int J1, double Fx, double Fy) Cell(int x, int y)
            {
                var gx = Math.Clamp((x + 0.5) / TextureBlockPx - 0.5, 0.0, blocksX - 1);
                var gy = Math.Clamp((y + 0.5) / TextureBlockPx - 0.5, 0.0, blocksY - 1);
                var (i0, j0) = ((int)gx, (int)gy);
                return (i0, Math.Min(i0 + 1, blocksX - 1), j0, Math.Min(j0 + 1, blocksY - 1), gx - i0, gy - j0);
            }

            private static double Lerp(double[] v, int bx, (int I0, int I1, int J0, int J1, double Fx, double Fy) c)
                => (1 - c.Fy) * ((1 - c.Fx) * v[c.J0 * bx + c.I0] + c.Fx * v[c.J0 * bx + c.I1])
                    + c.Fy * ((1 - c.Fx) * v[c.J1 * bx + c.I0] + c.Fx * v[c.J1 * bx + c.I1]);

            // The grain's sigma for a channel here: the texture's high-passed spread over the unit grain's own high-passed
            // spread, so the painted pixels carry the texture's fine variance.
            public double Sigma(int channel, int x, int y) => Lerp(sigma[channel], blocksX, Cell(x, y)) / gain;

            public double Rho(int x, int y) => Lerp(rho, blocksX, Cell(x, y));
        }

        private TextureMap Texture(float[][] plate, BitMatrix footprints, BitMatrix inpainted, float[][] grain)
        {
            var n = _width * _height;
            var known = new float[n];
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    var at = y * _width + x;
                    var finite = true;
                    for (var c = 0; c < _channels; c++)
                    {
                        finite &= float.IsFinite(plate[c][at]);
                    }
                    known[at] = !finite || footprints[y, x] || inpainted[y, x] || IsAbsent(x, y) ? 0f : 1f;
                }
            }
            float[] weight = [.. known];
            var scratch = new float[n];
            BackgroundExtraction.RobustBackgroundFit.BoxBlur3(weight, _width, _height, TextureHighPassPx, scratch);
            var highPass = new float[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                // Never known times the value: a NaN canvas ring times zero is NaN, and a running box sum that takes one
                // in stays NaN for the rest of its row and column. On the ASI585MC Tarantula that blanked every block, and
                // the fill went out with no grain at all.
                var sum = new float[n];
                for (var i = 0; i < n; i++)
                {
                    sum[i] = known[i] > 0f ? plate[c][i] : 0f;
                }
                BackgroundExtraction.RobustBackgroundFit.BoxBlur3(sum, _width, _height, TextureHighPassPx, scratch);
                var hp = new float[n];
                for (var i = 0; i < n; i++)
                {
                    hp[i] = known[i] > 0f && weight[i] > 0f ? plate[c][i] - sum[i] / weight[i] : float.NaN;
                }
                highPass[c] = hp;
            }
            var bx = Math.Max(1, _width / TextureBlockPx);
            var by = Math.Max(1, _height / TextureBlockPx);
            var sigma = new double[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                sigma[c] = new double[bx * by];
                Array.Fill(sigma[c], double.NaN);
            }
            var rho = new double[bx * by];
            Array.Fill(rho, double.NaN);
            ParallelFor.Run(bx * by, b =>
            {
                var (x0, y0) = (b % bx * TextureBlockPx, b / bx * TextureBlockPx);
                var samples = new List<float>[_channels];
                for (var c = 0; c < _channels; c++)
                {
                    samples[c] = new List<float>(TextureBlockPx * TextureBlockPx);
                }
                for (var y = y0; y < Math.Min(_height, y0 + TextureBlockPx); y++)
                {
                    for (var x = x0; x < Math.Min(_width, x0 + TextureBlockPx); x++)
                    {
                        var at = y * _width + x;
                        if (!float.IsFinite(highPass[0][at]))
                        {
                            continue;
                        }
                        for (var c = 0; c < _channels; c++)
                        {
                            samples[c].Add(highPass[c][at]);
                        }
                    }
                }
                if (samples[0].Count < 256)
                {
                    return;
                }
                for (var c = 0; c < _channels; c++)
                {
                    float[] copy = [.. samples[c]];
                    sigma[c][b] = 1.4826 * StatisticsHelper.MedianAndMad(copy.AsSpan()).Mad;
                }
                var pairs = 0.0;
                var count = 0;
                for (var c = 0; c < _channels; c++)
                {
                    for (var d = c + 1; d < _channels; d++)
                    {
                        pairs += RobustCorrelation([.. samples[c]], [.. samples[d]]);
                        count++;
                    }
                }
                rho[b] = count > 0 ? Math.Clamp(pairs / count, 0.0, 0.95) : 0.0;
            });
            // A block with too few clear pixels takes its neighbours' values, ring by ring; failing all, the frame's median.
            FillGaps(rho, bx, by);
            foreach (var s in sigma)
            {
                FillGaps(s, bx, by);
            }
            // The unit grain's own spread through the same high pass.
            float[] field = [.. grain[1]];
            var tileScratch = new float[field.Length];
            float[] mean = [.. field];
            BackgroundExtraction.RobustBackgroundFit.BoxBlur3(mean, GrainTilePx, GrainTilePx, TextureHighPassPx, tileScratch);
            for (var i = 0; i < field.Length; i++)
            {
                field[i] -= mean[i];
            }
            var gain = 1.4826 * StatisticsHelper.MedianAndMad(field.AsSpan()).Mad;
            return new TextureMap(bx, by, sigma, rho, gain > 0 ? gain : 1.0);
        }

        private static void FillGaps(double[] values, int bx, int by)
        {
            for (var round = 0; round < Math.Max(bx, by) && Array.Exists(values, static v => double.IsNaN(v)); round++)
            {
                double[] next = [.. values];
                for (var j = 0; j < by; j++)
                {
                    for (var i = 0; i < bx; i++)
                    {
                        if (!double.IsNaN(values[j * bx + i]))
                        {
                            continue;
                        }
                        double sum = 0;
                        var count = 0;
                        for (var dj = -1; dj <= 1; dj++)
                        {
                            for (var di = -1; di <= 1; di++)
                            {
                                var (ii, jj) = (i + di, j + dj);
                                if (ii >= 0 && jj >= 0 && ii < bx && jj < by && !double.IsNaN(values[jj * bx + ii]))
                                {
                                    sum += values[jj * bx + ii];
                                    count++;
                                }
                            }
                        }
                        if (count > 0)
                        {
                            next[j * bx + i] = sum / count;
                        }
                    }
                }
                Array.Copy(next, values, values.Length);
            }
            for (var i = 0; i < values.Length; i++)
            {
                if (double.IsNaN(values[i]))
                {
                    values[i] = 0.0;
                }
            }
        }

        // The surface a region takes: a plane per channel fitted in the ring outside its biggest footprint, and the ring's
        // robust scatter about it.
        private readonly record struct RingSurface(bool Valid, double[] A, double[] Bx, double[] By, double[] Spread);

        // The fill for a region no plane serves: a normalised convolution of the plate's known pixels (every pixel outside
        // the holes) at the smallest of SmoothFillRadii whose window holds SmoothFillPixels known pixels, so each value is a
        // mean of that many from the nearest side. Push-pull, which the builder's own holes take, caps each pyramid cell's
        // weight at one, so deep inside a large region a coarse cell rests on a handful of boundary pixels: their noise came
        // out as discs of half a sigma either side of the October Sculptor master's saturated pair. The window is judged by
        // its COUNT of known pixels, not their share: in Omega Cen's core the gaps between footprints are 14 % of the pixels
        // everywhere, a quarter's share was never reached below the largest scale, and a pixel beside a gap took a 4 px mean
        // while its neighbour took a 128 px one, half the core's level, per pixel and per channel.
        private void SmoothFill(float[] plane, BitMatrix holes)
        {
            var n = plane.Length;
            var known = new bool[n];
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    var at = y * _width + x;
                    known[at] = !holes[y, x] && !IsAbsent(x, y) && float.IsFinite(plane[at]);
                }
            }
            var pending = new BitMatrix(_height, _width);
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    pending[y, x] = holes[y, x] && !IsAbsent(x, y);
                }
            }
            var weight = new float[n];
            var sum = new float[n];
            var scratch = new float[n];
            foreach (var radius in SmoothFillRadii)
            {
                for (var i = 0; i < n; i++)
                {
                    (weight[i], sum[i]) = known[i] ? (1f, plane[i]) : (0f, 0f);
                }
                BackgroundExtraction.RobustBackgroundFit.BoxBlur3(weight, _width, _height, radius, scratch);
                BackgroundExtraction.RobustBackgroundFit.BoxBlur3(sum, _width, _height, radius, scratch);
                var last = radius == SmoothFillRadii[^1];
                var enough = (float)(SmoothFillPixels / ((2.0 * radius + 1) * (2.0 * radius + 1)));
                var remaining = false;
                for (var y = 0; y < _height; y++)
                {
                    for (var x = 0; x < _width; x++)
                    {
                        var at = y * _width + x;
                        if (!pending[y, x])
                        {
                            continue;
                        }
                        if (weight[at] >= enough || (last && weight[at] > 0f))
                        {
                            plane[at] = sum[at] / weight[at];
                            pending[y, x] = false;
                        }
                        else
                        {
                            remaining = true;
                        }
                    }
                }
                if (!remaining)
                {
                    break;
                }
            }
        }

        // The 8-connected regions of a mask, labelled from 1 (0 is outside every region), and how many there are.
        private (int[] Labels, int Count) Regions(BitMatrix mask)
        {
            var labels = new int[_width * _height];
            var count = 0;
            var stack = new Stack<int>();
            for (var start = 0; start < labels.Length; start++)
            {
                if (labels[start] != 0 || !mask[start / _width, start % _width])
                {
                    continue;
                }
                labels[start] = ++count;
                stack.Push(start);
                while (stack.TryPop(out var at))
                {
                    var (x, y) = (at % _width, at / _width);
                    for (var j = Math.Max(0, y - 1); j <= Math.Min(_height - 1, y + 1); j++)
                    {
                        for (var i = Math.Max(0, x - 1); i <= Math.Min(_width - 1, x + 1); i++)
                        {
                            var next = j * _width + i;
                            if (labels[next] == 0 && mask[j, i])
                            {
                                labels[next] = count;
                                stack.Push(next);
                            }
                        }
                    }
                }
            }
            return (labels, count);
        }

        private StarlessBrushStatistics Brush(StarlessBrush brush, List<Fit> fits, BitMatrix inpainted)
        {
            var n = _width * _height;
            var fwhmPx = LumPsf(1.0).Fwhm;
            var matchRadius = 1.5 * fwhmPx;
            var protect = brush.Protect;
            bool Protected(int x, int y) => protect is { } p && p[y, x];

            // The catalogue against what the builder found: matched within 1.5 FWHM, twice, the second time with the
            // catalogue moved by the local median offset of the first (a WCS a pixel off in a corner would otherwise place
            // every unmatched star beside its light).
            var grid = new FitGrid(fits, matchRadius);
            var catalogue = brush.Catalogue;
            var positions = new (double X, double Y)[catalogue.Length];
            for (var k = 0; k < catalogue.Length; k++)
            {
                positions[k] = (catalogue[k].X, catalogue[k].Y);
            }
            var catFit = Match(positions);
            var offsets = OffsetField(positions, catFit, fits, out var medianDx, out var medianDy, out var spread);
            for (var k = 0; k < positions.Length; k++)
            {
                var (ox, oy) = offsets.At(positions[k].X, positions[k].Y);
                positions[k] = (positions[k].X - ox, positions[k].Y - oy);
            }
            catFit = Match(positions);

            var fitMoving = new bool[fits.Count];
            var fitMatched = new bool[fits.Count];
            var fitMagnitude = new double[fits.Count];
            Array.Fill(fitMagnitude, double.PositiveInfinity);
            var inFrame = 0;
            var matched = 0;
            for (var k = 0; k < catalogue.Length; k++)
            {
                var (cx, cy) = Pixel(positions[k].X, positions[k].Y);
                if (IsAbsent(cx, cy))
                {
                    continue;
                }
                inFrame++;
                if (catFit[k] is var i and >= 0)
                {
                    matched++;
                    fitMatched[i] = true;
                    fitMoving[i] |= catalogue[k].Moving;
                    fitMagnitude[i] = Math.Min(fitMagnitude[i], catalogue[k].Magnitude);
                }
            }

            // A catalogue star's peak from its magnitude, calibrated on the matched stars the builder fitted cleanly.
            var zeroPoints = new List<double>();
            for (var i = 0; i < fits.Count; i++)
            {
                var f = fits[i];
                if (fitMatched[i] && f.Outcome == StarFitOutcome.Subtracted && !f.Saturated && f.Profile is null
                    && f.Significance is >= 20 and <= 3000 && f.Amplitude > 0 && double.IsFinite(fitMagnitude[i]))
                {
                    zeroPoints.Add(Math.Log10(f.Amplitude) + 0.4 * fitMagnitude[i]);
                }
            }
            var zeroPoint = zeroPoints.Count >= 10 ? StatisticsHelper.MedianFast(CollectionsMarshal.AsSpan(zeroPoints)) : double.NaN;

            var (halo, haloReach, haloStars) = HaloProfile(fits, grid, protect);

            // What is brushed and what is put back.
            var entries = new List<BrushEntry>();
            var restores = new List<int>();
            for (var i = 0; i < fits.Count; i++)
            {
                var f = fits[i];
                var (cx, cy) = Pixel(f.X, f.Y);
                switch (f.Outcome)
                {
                    case StarFitOutcome.Subtracted when Protected(cx, cy) && !fitMoving[i]:
                        restores.Add(i);
                        break;
                    case StarFitOutcome.Subtracted:
                        entries.Add(new BrushEntry(f.X, f.Y, FitFootprint(f, brush, halo, haloReach), Taken: true));
                        break;
                    case StarFitOutcome.Knot or StarFitOutcome.TooNarrow when fitMoving[i] && f.Amplitude > 0:
                        var width = f.Width > 0 ? f.Width : 1.0;
                        entries.Add(new BrushEntry(f.X, f.Y, Footprint(f.Amplitude, width, f.Beta, cx, cy, brush, halo, haloReach), Taken: false));
                        break;
                }
            }
            // Only RESOLVED stars are painted, the detector's own detections, catalogued or not, each sized by its own fitted
            // light. A catalogue star the detector did not find is the field's unresolved glow, which a starless plate keeps,
            // and stays. Painted out one by one, the 1.1 million Gaia stars on the ASI585MC Tarantula's 30 Doradus covered
            // 80 % of the frame and took 8 to 11 % more than each star's own light; let in where a forced fit at the catalogued
            // place stood 3 sigma over the pixel noise, 134,872 of them still did, the glow and the neighbours raising the fit
            // at almost every place, and the footprints would have covered 75 %. The catalogue's part is to say which
            // detections are moving stars.

            var footprints = new BitMatrix(_height, _width);
            foreach (var e in entries)
            {
                ForDisc(e.X, e.Y, e.Radius, (x, y) => footprints[y, x] = true);
            }

            // A frame whose footprints would cover more than MaxCoverage of it is refused before anything is written: past
            // that the fill has too little sky round it to come from, and an airbrushed plate is no truth.
            var (present, covered) = (0, 0);
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    if (!IsAbsent(x, y))
                    {
                        present++;
                        covered += footprints[y, x] ? 1 : 0;
                    }
                }
            }
            var coverage = present > 0 ? (double)covered / present : 0.0;
            if (coverage > brush.MaxCoverage)
            {
                logger?.LogWarning(
                    "ClassicalStarRemover brush REFUSED: {Footprints} footprints would cover {Coverage:P1} of the frame, over {Max:P0}; the builder's plate stands as it was.",
                    entries.Count, coverage, brush.MaxCoverage);
                (_brushFootprints, _brushPlanes) = (footprints, new BitMatrix(_height, _width));
                return new StarlessBrushStatistics(
                    inFrame, matched, entries.Count, 0, protect is { } p0 ? (double)p0.PopCount() / n : 0.0, 0.0, haloReach, haloStars,
                    zeroPoint, medianDx, medianDy, spread, double.NaN, double.NaN, double.NaN, Completeness(), 0, 0.0, 0.0, 0,
                    Refused: true, Coverage: coverage);
            }
            logger?.LogInformation(
                "ClassicalStarRemover brush: {Footprints} footprints cover {Coverage:P1} of the frame (limit {Max:P0}); painting.",
                entries.Count, coverage, brush.MaxCoverage);

            // A source the builder took on a protected galaxy that no moving star explains is the galaxy's own (a cluster,
            // an HII region): the input goes back where the builder changed it. One inside a footprint is not: a bright
            // star's core comes out of the builder in pieces, the piece nearest the catalogue position matched and the others
            // a few pixels beyond the match radius (a G 5.7 star on an IC galaxy's outline left four), and the footprint paints
            // over them anyway.
            restores.RemoveAll(i => footprints[Pixel(fits[i].X, fits[i].Y).Y, Pixel(fits[i].X, fits[i].Y).X]);
            foreach (var i in restores)
            {
                var f = fits[i];
                var radius = Math.Max(f.SubtractRadius, 2.0 * fwhmPx);
                ForDisc(f.X, f.Y, radius, (x, y) =>
                {
                    var at = y * _width + x;
                    for (var c = 0; c < _channels; c++)
                    {
                        _work[c][at] = _original[c][at];
                    }
                    inpainted[y, x] = false;
                });
                f.Outcome = StarFitOutcome.Knot;
                fits[i] = f;
            }

            // The surfaces, read off the plate before any footprint is painted. Footprints that touch make one region, and a
            // region takes one surface: one no larger than its biggest footprint's disc (a bright star, fainter ones inside its
            // halo) takes that footprint's plane, fitted in the ring outside every footprint; a larger one (a crowded field,
            // where the footprints join into a sponge round the gaps between stars) takes the smooth fill (SmoothFill) from
            // every pixel round and inside it that no footprint covers, the gaps being where only the unresolved light is. Each
            // footprint taking a plane of its own drew the fainter stars inside a bright one's disc as discs of their own level
            // and colour on the Omega Cen master. A single star whose ring is no plane sits on structure (a galaxy's edge),
            // which neither a plane nor a local mean follows: where the builder took every star of such a region, its plate,
            // the star's model taken out of the data, stays.
            var snapshot = new float[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                snapshot[c] = [.. _work[c]];
            }
            var (labels, regionCount) = Regions(footprints);
            var biggest = new int[regionCount + 1];
            var allTaken = new bool[regionCount + 1];
            Array.Fill(biggest, -1);
            Array.Fill(allTaken, true);
            for (var e = 0; e < entries.Count; e++)
            {
                var (cx, cy) = Pixel(entries[e].X, entries[e].Y);
                var l = labels[cy * _width + cx];
                if (l > 0 && (biggest[l] < 0 || entries[e].Radius > entries[biggest[l]].Radius))
                {
                    biggest[l] = e;
                }
                allTaken[l] &= entries[e].Taken;
            }
            var (minX, minY, maxX, maxY) = (new int[regionCount + 1], new int[regionCount + 1], new int[regionCount + 1], new int[regionCount + 1]);
            Array.Fill(minX, int.MaxValue);
            Array.Fill(minY, int.MaxValue);
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    if (labels[y * _width + x] is var l and > 0)
                    {
                        (minX[l], minY[l]) = (Math.Min(minX[l], x), Math.Min(minY[l], y));
                        (maxX[l], maxY[l]) = (Math.Max(maxX[l], x), Math.Max(maxY[l], y));
                    }
                }
            }
            var slack = 2.0 * (fwhmPx + 1.0);
            var simple = new List<int>();
            for (var l = 1; l <= regionCount; l++)
            {
                if (biggest[l] >= 0 && maxX[l] - minX[l] + 1 <= 2.0 * (entries[biggest[l]].Radius + slack)
                    && maxY[l] - minY[l] + 1 <= 2.0 * (entries[biggest[l]].Radius + slack))
                {
                    simple.Add(l);
                }
            }
            var (resample, ratio, rho, grain) = GrainFields(snapshot, footprints, inpainted, options.Seed);
            var level = LevelModels(snapshot, footprints, inpainted);
            // A plane serves a region only where its ring IS a plane: the ring's scatter about it no more than PlanarSpread
            // times the noise there. On a galaxy's edge it is not, and a plane drew a disc across the edge.
            var surfaces = new RingSurface[regionCount + 1];
            var keepPlate = new bool[regionCount + 1];
            ParallelFor.Run(simple.Count, k =>
            {
                var l = simple[k];
                var s = FitRing(entries[biggest[l]], snapshot, footprints, brush.RingPx);
                for (var c = 0; c < _channels && s.Valid; c++)
                {
                    if (s.Spread[c] > PlanarSpread * level[c].Sigma(s.A[c], s.Spread[c]))
                    {
                        s = s with { Valid = false };
                        keepPlate[l] = allTaken[l];
                    }
                }
                surfaces[l] = s;
            });
            ct.ThrowIfCancellationRequested();
            // A region the brush leaves to the builder is not painted, but for its pits (below).
            var kept = 0;
            var keep = new BitMatrix(_height, _width);
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    if (footprints[y, x] && keepPlate[labels[y * _width + x]])
                    {
                        footprints[y, x] = false;
                        keep[y, x] = true;
                        kept++;
                    }
                }
            }
            var texture = Texture(snapshot, footprints, inpainted, grain);
            var ceiling = Ceiling();
            var rest = new BitMatrix(_height, _width);
            var planeMask = new BitMatrix(_height, _width);
            var restAny = false;
            var planes = 0;
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    if (!footprints[y, x] || IsAbsent(x, y))
                    {
                        continue;
                    }
                    var at = y * _width + x;
                    var l = labels[at];
                    if (!surfaces[l].Valid)
                    {
                        rest[y, x] = true;
                        restAny = true;
                        continue;
                    }
                    planes++;
                    planeMask[y, x] = true;
                    var e = entries[biggest[l]];
                    var s = surfaces[l];
                    var (dx, dy) = (x - e.X, y - e.Y);
                    for (var c = 0; c < _channels; c++)
                    {
                        var surface = s.A[c] + s.Bx[c] * dx + s.By[c] * dy;
                        Paint(c, at, surface, Math.Max(texture.Sigma(c, x, y), level[c].Sigma(surface, 0.0)), Grain(grain, texture.Rho(x, y), c, x, y), ceiling);
                    }
                }
            }
            // The smooth fill, from every pixel no footprint covers, for the regions no plane serves and for the ones left to
            // the builder. There it is a floor, never a replacement: a pixel the builder dug more than PitSigma under it in
            // any channel (a saturated star subtracted into a pit, black on the ASI585MC Tarantula's nebula) takes the fill
            // in every channel, so no colour is made; one above it is structure, and stays.
            var pits = 0;
            if (restAny || kept > 0)
            {
                var holes = new BitMatrix(_height, _width);
                for (var y = 0; y < _height; y++)
                {
                    for (var x = 0; x < _width; x++)
                    {
                        holes[y, x] = rest[y, x] || keep[y, x];
                    }
                }
                var fill = new float[_channels][];
                for (var c = 0; c < _channels; c++)
                {
                    fill[c] = [.. _work[c]];
                    SmoothFill(fill[c], holes);
                }
                for (var y = 0; y < _height; y++)
                {
                    for (var x = 0; x < _width; x++)
                    {
                        var at = y * _width + x;
                        var paint = rest[y, x];
                        if (keep[y, x] && !IsAbsent(x, y))
                        {
                            for (var c = 0; c < _channels && !paint; c++)
                            {
                                var sigma = Math.Max(texture.Sigma(c, x, y), level[c].Sigma(fill[c][at], 0.0));
                                paint = _work[c][at] < fill[c][at] - PitSigma * sigma;
                            }
                            if (paint)
                            {
                                pits++;
                                inpainted[y, x] = true;
                            }
                        }
                        if (!paint)
                        {
                            continue;
                        }
                        for (var c = 0; c < _channels; c++)
                        {
                            Paint(c, at, fill[c][at], Math.Max(texture.Sigma(c, x, y), level[c].Sigma(fill[c][at], 0.0)), Grain(grain, texture.Rho(x, y), c, x, y), ceiling);
                        }
                    }
                }
            }
            var brushed = 0;
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    if (footprints[y, x] && !IsAbsent(x, y))
                    {
                        inpainted[y, x] = true;
                        brushed++;
                    }
                }
            }
            (_brushFootprints, _brushPlanes) = (footprints, planeMask);

            var protectedCount = protect is { } pm ? pm.PopCount() : 0;
            var statistics = new StarlessBrushStatistics(
                inFrame, matched, entries.Count, restores.Count,
                (double)protectedCount / n, (double)brushed / n, haloReach, haloStars, zeroPoint,
                medianDx, medianDy, spread, resample, ratio, rho, Completeness(),
                regionCount, brushed > 0 ? (double)planes / brushed : 0.0, brushed + kept > 0 ? (double)kept / (brushed + kept) : 0.0, pits,
                Refused: false, Coverage: coverage);
            logger?.LogInformation(
                "ClassicalStarRemover brush: {Catalogue} catalogue stars on the frame, {Matched} matched; {Brushed} footprints ({Fraction:P2} of the frame), {Restored} put back on protected sources ({Protect:P2} protected); halo to {Halo} px from {HaloStars} stars; grain resample {Resample:F2} px (band ratio {Ratio:F3}), channel correlation {Rho:F2}.",
                inFrame, matched, entries.Count, statistics.BrushedFraction, restores.Count, statistics.ProtectFraction, haloReach, haloStars, resample, ratio, rho);
            return statistics;

            int[] Match((double X, double Y)[] at)
            {
                var result = new int[at.Length];
                for (var k = 0; k < at.Length; k++)
                {
                    result[k] = grid.Nearest(at[k].X, at[k].Y, matchRadius);
                }
                return result;
            }

            // How complete the catalogue is against what the builder found, outside the protect mask.
            ImmutableArray<CatalogueCompleteness> Completeness()
            {
                ReadOnlySpan<(float Lo, float Hi)> bands = [(5f, 20f), (20f, 100f), (100f, 1000f), (1000f, float.PositiveInfinity)];
                var completeness = ImmutableArray.CreateBuilder<CatalogueCompleteness>(bands.Length);
                foreach (var (lo, hi) in bands)
                {
                    int stars = 0, held = 0;
                    for (var i = 0; i < fits.Count; i++)
                    {
                        var f = fits[i];
                        var (cx, cy) = Pixel(f.X, f.Y);
                        if (f.Outcome == StarFitOutcome.Subtracted && f.Significance >= lo && f.Significance < hi && !Protected(cx, cy))
                        {
                            stars++;
                            held += fitMatched[i] ? 1 : 0;
                        }
                    }
                    completeness.Add(new CatalogueCompleteness(lo, hi, stars, held));
                }
                return completeness.MoveToImmutable();
            }
        }

        // The catalogue-minus-found offsets of the matched stars the builder fitted cleanly: their medians and spread, and a
        // per-tile map of them (OffsetMap).
        private OffsetMap OffsetField((double X, double Y)[] positions, int[] catFit, List<Fit> fits, out double medianDx, out double medianDy, out double spread)
        {
            var tilesX = (_width + OffsetMap.TilePx - 1) / OffsetMap.TilePx;
            var tilesY = (_height + OffsetMap.TilePx - 1) / OffsetMap.TilePx;
            var perTile = new List<(double Dx, double Dy)>[tilesX * tilesY];
            var dxs = new List<double>();
            var dys = new List<double>();
            for (var k = 0; k < positions.Length; k++)
            {
                if (catFit[k] is var i and >= 0 && fits[i] is { Outcome: StarFitOutcome.Subtracted, Saturated: false, Significance: >= 20 } f)
                {
                    var (dx, dy) = (positions[k].X - f.X, positions[k].Y - f.Y);
                    dxs.Add(dx);
                    dys.Add(dy);
                    var t = Math.Clamp((int)(f.Y / OffsetMap.TilePx), 0, tilesY - 1) * tilesX + Math.Clamp((int)(f.X / OffsetMap.TilePx), 0, tilesX - 1);
                    (perTile[t] ??= new List<(double, double)>()).Add((dx, dy));
                }
            }
            var tiles = new (double Dx, double Dy)[tilesX * tilesY];
            if (dxs.Count < 8)
            {
                medianDx = medianDy = 0;
                spread = double.NaN;
                return new OffsetMap(tilesX, tilesY, tiles);
            }
            medianDx = StatisticsHelper.MedianFast(CollectionsMarshal.AsSpan([.. dxs]));
            medianDy = StatisticsHelper.MedianFast(CollectionsMarshal.AsSpan([.. dys]));
            var deviations = new List<double>(dxs.Count);
            for (var k = 0; k < dxs.Count; k++)
            {
                deviations.Add(Math.Sqrt((dxs[k] - medianDx) * (dxs[k] - medianDx) + (dys[k] - medianDy) * (dys[k] - medianDy)));
            }
            spread = StatisticsHelper.MedianFast(CollectionsMarshal.AsSpan(deviations));
            var tx = new List<double>();
            var ty = new List<double>();
            for (var y = 0; y < tilesY; y++)
            {
                for (var x = 0; x < tilesX; x++)
                {
                    tx.Clear();
                    ty.Clear();
                    for (var j = Math.Max(0, y - 1); j <= Math.Min(tilesY - 1, y + 1); j++)
                    {
                        for (var i = Math.Max(0, x - 1); i <= Math.Min(tilesX - 1, x + 1); i++)
                        {
                            if (perTile[j * tilesX + i] is { } list)
                            {
                                foreach (var (dx, dy) in list)
                                {
                                    tx.Add(dx);
                                    ty.Add(dy);
                                }
                            }
                        }
                    }
                    tiles[y * tilesX + x] = tx.Count >= 8
                        ? (StatisticsHelper.MedianFast(CollectionsMarshal.AsSpan(tx)), StatisticsHelper.MedianFast(CollectionsMarshal.AsSpan(ty)))
                        : (medianDx, medianDy);
                }
            }
            return new OffsetMap(tilesX, tilesY, tiles);
        }

        private (int X, int Y) Pixel(double x, double y)
            => ((int)Math.Clamp(Math.Round(x), 0, _width - 1), (int)Math.Clamp(Math.Round(y), 0, _height - 1));

        private void ForDisc(double cx, double cy, double radius, Action<int, int> body)
        {
            var r2 = radius * radius;
            var x0 = Math.Max(0, (int)Math.Floor(cx - radius));
            var x1 = Math.Min(_width - 1, (int)Math.Ceiling(cx + radius));
            var y0 = Math.Max(0, (int)Math.Floor(cy - radius));
            var y1 = Math.Min(_height - 1, (int)Math.Ceiling(cy + radius));
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r2 && !IsAbsent(x, y))
                    {
                        body(x, y);
                    }
                }
            }
        }

        // The surface plus grain, unless that stands more than the hole fill's margin above the data the plate was made from
        // (a star only adds light): then the data, as HoleFill does.
        private void Paint(int channel, int at, double surface, double sigma, double grain, float[][] ceiling)
        {
            var value = surface + sigma * grain;
            var top = ceiling[channel][at];
            _work[channel][at] = float.IsFinite(top) && value > top + HoleFill.CeilingSigma * sigma ? top : (float)value;
        }

        private static double Grain(float[][] fields, double rho, int channel, int x, int y)
        {
            var at = (y % GrainTilePx) * GrainTilePx + (x % GrainTilePx);
            return Math.Sqrt(rho) * fields[0][at] + Math.Sqrt(1.0 - rho) * fields[1 + channel][at];
        }


        // A subtracted star's footprint: its model and the field's halo down to the brush's fraction of the noise, its own
        // profile for a giant, its companion's offset for a blend.
        private double FitFootprint(in Fit f, StarlessBrush brush, float[]? halo, int haloReach)
        {
            var (cx, cy) = Pixel(f.X, f.Y);
            var radius = Footprint(f.Amplitude, f.Width, f.Beta, cx, cy, brush, halo, haloReach);
            if (f.Profile is { } profile)
            {
                // A giant's own profile, in the luminance, read with the same disc criterion.
                var noise = brush.NoiseFraction * Math.Max(_holeNoise[cy * _width + cx], 1e-12);
                var reach = 0;
                for (var b = profile[0].Length - 1; b > f.PlateauRadius; b--)
                {
                    var value = 0.0;
                    foreach (var p in profile)
                    {
                        value += p[b];
                    }
                    if (value / profile.Length * DiscSpread(b) >= noise)
                    {
                        reach = b + 1;
                        break;
                    }
                }
                radius = Math.Max(radius, Math.Max(reach, f.PlateauRadius + 1.5 * LumPsf(f.Width, f.Beta).Fwhm));
            }
            if (f.CompanionAmplitudes is not null)
            {
                radius += Math.Sqrt((f.CompanionX - f.X) * (f.CompanionX - f.X) + (f.CompanionY - f.Y) * (f.CompanionY - f.Y));
            }
            return Math.Min(radius, MaxFootprintPx);
        }

        // How much a level left over a disc of this radius stands out as one extended source, per unit of that level over the
        // pixel noise: root(pi r^2), since the eye sums the disc.
        private static double DiscSpread(double radius) => Math.Sqrt(Math.PI) * Math.Max(radius, 1.0);

        // A footprint's radius: out to where the star's light at the edge, which the ring's plane inherits as a level over the
        // whole disc, would stand under NoiseFraction of the pixel noise summed over that disc (DiscSpread). The light is the
        // larger of the star's own model (Moffat and the field's table) and the field's measured halo, carried past its
        // measured reach by its own power law. Cut where the halo fell to a quarter of the noise PER PIXEL, the disc kept
        // +0.12 to +0.24 sigma over 3 to 25 px round the October Sculptor master's stars of 5,000 sigma and more: invisible
        // pixel by pixel, a blob to the eye.
        private double Footprint(double amplitude, double width, double beta, int cx, int cy, StarlessBrush brush, float[]? halo, int haloReach)
        {
            var psf = LumPsf(width > 0 ? width : 1.0, beta);
            var noise = brush.NoiseFraction * Math.Max(_holeNoise[cy * _width + cx], 1e-12);
            var radius = 1.5 * psf.Fwhm;
            if (!(amplitude * DiscSpread(radius) > noise))
            {
                return radius;
            }
            var tableReach = _correction is { } t ? t.Reach * psf.Alpha / t.ReferenceAlpha : 0.0;
            var slope = halo is null ? 0.0 : HaloSlope(halo, haloReach);
            for (var r = 0.5; r <= MaxFootprintPx; r += 0.5)
            {
                var model = psf.At(r * r) + (_correction?.At(r, psf.Alpha) ?? 0.0);
                if (halo is not null && haloReach > 0)
                {
                    var measured = r <= haloReach
                        ? halo[(int)r]
                        : halo[haloReach] * Math.Pow(r / haloReach, -slope);
                    model = Math.Max(model, measured);
                }
                if (amplitude * model * DiscSpread(r) >= noise)
                {
                    radius = Math.Max(radius, r);
                }
                else if (r > tableReach && r > haloReach)
                {
                    // Past both, the light times the radius only falls (a Moffat's wing, a power law of slope 2 or more).
                    break;
                }
            }
            return Math.Min(radius, MaxFootprintPx);
        }

        // The halo's power-law slope over the outer half of its measured reach (from 3 FWHM), by least squares in log-log,
        // clamped to 2 (a scattering halo) to 6 (a beta-3 Moffat's wing).
        private double HaloSlope(float[] halo, int reach)
        {
            var from = Math.Max((int)Math.Ceiling(3.0 * LumPsf(1.0).Fwhm), reach / 2);
            double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (var r = Math.Max(from, 1); r <= reach; r++)
            {
                if (halo[r] > 0)
                {
                    var (lx, ly) = (Math.Log(r), Math.Log(halo[r]));
                    n++;
                    sx += lx;
                    sy += ly;
                    sxx += lx * lx;
                    sxy += lx * ly;
                }
            }
            var det = n * sxx - sx * sx;
            return n >= 3 && det > 0 ? Math.Clamp(-(n * sxy - sx * sy) / det, 2.0, 6.0) : 6.0;
        }

        // The field's measured profile, halo included, peak 1, in 1 px rings out to HaloMaxPx, read on the INPUT (other sources'
        // cores masked) against the sky just beyond: the median over the brightest unsaturated stars with no bright neighbour,
        // made non-increasing and cut where it falls into its own noise; then carried on past that by the same median over the
        // saturated stars, whose halos stand clear of the noise much farther out, scaled onto it where both are measured (a
        // saturated star's peak is fitted from its wings, so its normalisation is not the unsaturated ones'). The unsaturated
        // stars alone reached 19 px on the October Sculptor master, and stars of a few thousand sigma kept a halo past it.
        private (float[]? Halo, int Reach, int Stars) HaloProfile(List<Fit> fits, FitGrid grid, BitMatrix? protect)
        {
            var fwhmPx = LumPsf(1.0).Fwhm;
            var lum = new float[_width * _height];
            for (var c = 0; c < _channels; c++)
            {
                var plane = _original[c];
                for (var i = 0; i < lum.Length; i++)
                {
                    lum[i] += plane[i] / _channels;
                }
            }
            var unsaturated = HaloPicks(fits, grid, protect, saturated: false);
            if (unsaturated.Count < 8)
            {
                return (null, 0, unsaturated.Count);
            }
            var (median, error) = StackedProfile(fits, grid, unsaturated, lum);
            var halo = new float[HaloMaxPx + 1];
            var reach = 0;
            for (var b = 0; b < HaloMaxPx && double.IsFinite(median[b]); b++)
            {
                var value = b == 0 ? median[b] : Math.Min(median[b], halo[b - 1]);
                if (b > 2.0 * fwhmPx && (value <= 0 || median[b] < 2.0 * error[b]))
                {
                    break;
                }
                halo[b] = (float)Math.Max(value, 0.0);
                reach = b;
            }
            var saturated = HaloPicks(fits, grid, protect, saturated: true);
            if (saturated.Count >= 5 && reach > 2)
            {
                var (far, farError) = StackedProfile(fits, grid, saturated, lum);
                var ratios = new List<double>();
                for (var b = Math.Max((int)Math.Ceiling(2.0 * fwhmPx), reach / 2); b <= reach; b++)
                {
                    if (far[b] > 2.0 * farError[b] && halo[b] > 0)
                    {
                        ratios.Add(halo[b] / far[b]);
                    }
                }
                if (ratios.Count >= 3)
                {
                    var scale = StatisticsHelper.MedianFast(CollectionsMarshal.AsSpan(ratios));
                    for (var b = reach + 1; b < HaloMaxPx && double.IsFinite(far[b]); b++)
                    {
                        var value = Math.Min(scale * far[b], halo[b - 1]);
                        if (value <= 0 || far[b] < 2.0 * farError[b])
                        {
                            break;
                        }
                        halo[b] = (float)value;
                        reach = b;
                    }
                }
            }
            return (halo, reach, unsaturated.Count + saturated.Count);
        }

        // Stars for the halo: subtracted with the field's model (no profile of their own), unsaturated or saturated as asked,
        // far enough from the frame's edge and every protected source to read HaloMaxPx and a sky beyond, and with no source of
        // a third of their peak within half that; the brightest HaloProfileStars.
        private List<int> HaloPicks(List<Fit> fits, FitGrid grid, BitMatrix? protect, bool saturated)
        {
            const int outer = HaloMaxPx + 10;
            var order = new List<int>();
            for (var i = 0; i < fits.Count; i++)
            {
                var f = fits[i];
                if (f.Outcome == StarFitOutcome.Subtracted && f.Saturated == saturated && f.Profile is null && f.Amplitude > 0
                    && f.X > outer && f.Y > outer && f.X < _width - 1 - outer && f.Y < _height - 1 - outer
                    && !(protect is { } p && p[(int)f.Y, (int)f.X]))
                {
                    order.Add(i);
                }
            }
            order.Sort((a, b) => fits[b].Amplitude.CompareTo(fits[a].Amplitude));
            var picks = new List<int>();
            foreach (var i in order)
            {
                if (picks.Count >= HaloProfileStars)
                {
                    break;
                }
                var f = fits[i];
                if (!grid.AnyBrighterWithin(f.X, f.Y, HaloMaxPx / 2.0, 0.3 * f.Amplitude, except: i))
                {
                    picks.Add(i);
                }
            }
            return picks;
        }

        // Per 1 px ring out to HaloMaxPx, the median over the picked stars of their ring's median less their sky (HaloMaxPx to
        // 10 px beyond), over their fitted peak; and that median's standard error. NaN where under five stars have the ring.
        private (double[] Median, double[] Error) StackedProfile(List<Fit> fits, FitGrid grid, List<int> picks, float[] lum)
        {
            var fwhmPx = LumPsf(1.0).Fwhm;
            const int outer = HaloMaxPx + 10;
            var profiles = new double[picks.Count][];
            ParallelFor.Run(picks.Count, s =>
            {
                var f = fits[picks[s]];
                var neighbours = grid.Within(f.X, f.Y, outer + 3.0 * fwhmPx, except: picks[s]);
                var bins = new List<float>[HaloMaxPx + 1];
                var sky = new List<float>();
                var (cx, cy) = ((int)Math.Round(f.X), (int)Math.Round(f.Y));
                for (var y = cy - outer; y <= cy + outer; y++)
                {
                    for (var x = cx - outer; x <= cx + outer; x++)
                    {
                        var r = Math.Sqrt((x - f.X) * (x - f.X) + (y - f.Y) * (y - f.Y));
                        if (r > outer || IsAbsent(x, y))
                        {
                            continue;
                        }
                        var masked = false;
                        if (r > 3.0 * fwhmPx)
                        {
                            foreach (var k in neighbours)
                            {
                                var g = fits[k];
                                if ((x - g.X) * (x - g.X) + (y - g.Y) * (y - g.Y) < 9.0 * fwhmPx * fwhmPx)
                                {
                                    masked = true;
                                    break;
                                }
                            }
                        }
                        if (masked)
                        {
                            continue;
                        }
                        var v = lum[y * _width + x];
                        if (r >= HaloMaxPx)
                        {
                            sky.Add(v);
                        }
                        else
                        {
                            (bins[(int)r] ??= new List<float>()).Add(v);
                        }
                    }
                }
                var profile = new double[HaloMaxPx + 1];
                if (sky.Count < 50)
                {
                    profiles[s] = Array.Empty<double>();
                    return;
                }
                var skyLevel = StatisticsHelper.MedianFast(CollectionsMarshal.AsSpan(sky));
                for (var b = 0; b < HaloMaxPx; b++)
                {
                    profile[b] = bins[b] is { Count: > 0 } list
                        ? (StatisticsHelper.MedianFast(CollectionsMarshal.AsSpan(list)) - skyLevel) / f.Amplitude
                        : double.NaN;
                }
                profiles[s] = profile;
            });
            var medians = new double[HaloMaxPx + 1];
            var errors = new double[HaloMaxPx + 1];
            Array.Fill(medians, double.NaN);
            Array.Fill(errors, double.NaN);
            var column = new List<double>(picks.Count);
            for (var b = 0; b < HaloMaxPx; b++)
            {
                column.Clear();
                foreach (var p in profiles)
                {
                    if (p.Length > b && double.IsFinite(p[b]))
                    {
                        column.Add(p[b]);
                    }
                }
                if (column.Count < 5)
                {
                    break;
                }
                var (median, mad) = StatisticsHelper.MedianAndMad(CollectionsMarshal.AsSpan(column));
                medians[b] = median;
                errors[b] = 1.2533 * 1.4826 * mad / Math.Sqrt(column.Count);
            }
            return (medians, errors);
        }

        // A plane per channel by least squares in the ring outside the footprint (no footprint's pixel, no absent one),
        // clipped once at three robust sigmas; widened up to RingWidenings times until it holds MinRingSamples.
        private RingSurface FitRing(BrushEntry e, float[][] plate, BitMatrix footprints, float ringPx)
        {
            var invalid = new RingSurface(false, [], [], [], []);
            var width = (double)ringPx;
            var xs = new List<int>();
            var ys = new List<int>();
            for (var attempt = 0; attempt <= RingWidenings; attempt++, width *= 2)
            {
                xs.Clear();
                ys.Clear();
                var (r0, r1) = (e.Radius, e.Radius + width);
                for (var y = Math.Max(0, (int)Math.Floor(e.Y - r1)); y <= Math.Min(_height - 1, (int)Math.Ceiling(e.Y + r1)); y++)
                {
                    for (var x = Math.Max(0, (int)Math.Floor(e.X - r1)); x <= Math.Min(_width - 1, (int)Math.Ceiling(e.X + r1)); x++)
                    {
                        var d2 = (x - e.X) * (x - e.X) + (y - e.Y) * (y - e.Y);
                        if (d2 > r0 * r0 && d2 <= r1 * r1 && !footprints[y, x] && !IsAbsent(x, y))
                        {
                            xs.Add(x);
                            ys.Add(y);
                        }
                    }
                }
                if (xs.Count >= MinRingSamples)
                {
                    break;
                }
            }
            if (xs.Count < MinRingSamples)
            {
                return invalid;
            }
            var a = new double[_channels];
            var bx = new double[_channels];
            var by = new double[_channels];
            var spread = new double[_channels];
            var keep = new bool[xs.Count];
            var residuals = new float[xs.Count];
            for (var c = 0; c < _channels; c++)
            {
                Array.Fill(keep, true);
                if (!PlaneFit(plate[c], e, xs, ys, keep, out a[c], out bx[c], out by[c]))
                {
                    return invalid;
                }
                for (var k = 0; k < xs.Count; k++)
                {
                    residuals[k] = (float)(plate[c][ys[k] * _width + xs[k]] - (a[c] + bx[c] * (xs[k] - e.X) + by[c] * (ys[k] - e.Y)));
                }
                float[] deviations = [.. residuals];
                var (median, mad) = StatisticsHelper.MedianAndMad(deviations.AsSpan());
                var limit = 3.0 * 1.4826 * Math.Max(mad, 1e-12);
                for (var k = 0; k < xs.Count; k++)
                {
                    keep[k] = Math.Abs(residuals[k] - median) <= limit;
                }
                if (!PlaneFit(plate[c], e, xs, ys, keep, out a[c], out bx[c], out by[c]))
                {
                    return invalid;
                }
                // The scatter of every ring pixel about the final plane, the clipped ones included: a ring that is not a
                // plane shows here.
                for (var k = 0; k < xs.Count; k++)
                {
                    residuals[k] = (float)(plate[c][ys[k] * _width + xs[k]] - (a[c] + bx[c] * (xs[k] - e.X) + by[c] * (ys[k] - e.Y)));
                }
                spread[c] = 1.4826 * StatisticsHelper.MedianAndMad(residuals.AsSpan()).Mad;
            }
            return new RingSurface(true, a, bx, by, spread);
        }

        private bool PlaneFit(float[] plane, BrushEntry e, List<int> xs, List<int> ys, bool[] keep, out double a, out double bx, out double by)
        {
            double s = 0, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0, sv = 0, sxv = 0, syv = 0;
            for (var k = 0; k < xs.Count; k++)
            {
                if (!keep[k])
                {
                    continue;
                }
                var (dx, dy) = (xs[k] - e.X, ys[k] - e.Y);
                double v = plane[ys[k] * _width + xs[k]];
                s++;
                sx += dx;
                sy += dy;
                sxx += dx * dx;
                sxy += dx * dy;
                syy += dy * dy;
                sv += v;
                sxv += dx * v;
                syv += dy * v;
            }
            a = bx = by = 0;
            if (s < 3)
            {
                return false;
            }
            // Normal equations of v = a + bx dx + by dy, by Cramer's rule.
            var det = s * (sxx * syy - sxy * sxy) - sx * (sx * syy - sxy * sy) + sy * (sx * sxy - sxx * sy);
            if (Math.Abs(det) < 1e-9)
            {
                a = sv / s;
                return true;
            }
            a = (sv * (sxx * syy - sxy * sxy) - sx * (sxv * syy - sxy * syv) + sy * (sxv * sxy - sxx * syv)) / det;
            bx = (s * (sxv * syy - sxy * syv) - sv * (sx * syy - sxy * sy) + sy * (sx * syv - sxv * sy)) / det;
            by = (s * (sxx * syv - sxv * sxy) - sx * (sx * syv - sxv * sy) + sv * (sx * sxy - sxx * sy)) / det;
            return true;
        }

        // The grain's shape: NoiseField.Warped at the resampling width whose band1/band0 ratio matches the plate's own in its
        // quietest blocks (as the synthetic cluster scenes match it), drawn as one shared and one field per channel mixed by
        // the channels' measured noise correlation. Unit variance per pixel; tiled every GrainTilePx.
        private (double Resample, double Ratio, double Rho, float[][] Fields) GrainFields(float[][] plate, BitMatrix footprints, BitMatrix inpainted, int seed)
        {
            const int block = GrainCalibrationBlockPx;
            var gw = _width / block;
            var gh = _height / block;
            var candidates = new List<(int X, int Y, float Level, double Taken)>();
            for (var by = 0; by < gh; by++)
            {
                for (var bx = 0; bx < gw; bx++)
                {
                    var taken = 0;
                    var absent = false;
                    for (var y = by * block; y < (by + 1) * block && !absent; y++)
                    {
                        for (var x = bx * block; x < (bx + 1) * block; x++)
                        {
                            if (IsAbsent(x, y) || !float.IsFinite(plate[0][y * _width + x]))
                            {
                                absent = true;
                                break;
                            }
                            taken += footprints[y, x] || inpainted[y, x] ? 1 : 0;
                        }
                    }
                    if (!absent)
                    {
                        candidates.Add((bx * block, by * block, _sky[(by * block + block / 2) * _width + bx * block + block / 2], (double)taken / (block * block)));
                    }
                }
            }
            var chosen = new List<(int X, int Y, float Level, double Taken)>();
            foreach (var limit in (ReadOnlySpan<double>)[0.05, 0.15, 0.3, 1.0])
            {
                chosen = candidates.FindAll(b => b.Taken <= limit);
                if (chosen.Count >= 4)
                {
                    break;
                }
            }
            chosen.Sort((a, b) => a.Level.CompareTo(b.Level));
            if (chosen.Count > 12)
            {
                chosen.RemoveRange(12, chosen.Count - 12);
            }
            var ratios = new List<double>();
            var correlations = new List<double>();
            var cut = new float[block * block];
            var diffs = new float[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                diffs[c] = new float[block * (block - 1)];
            }
            foreach (var (x0, y0, _, _) in chosen)
            {
                for (var c = 0; c < _channels; c++)
                {
                    for (var y = 0; y < block; y++)
                    {
                        plate[c].AsSpan((y0 + y) * _width + x0, block).CopyTo(cut.AsSpan(y * block, block));
                        for (var x = 0; x + 1 < block; x++)
                        {
                            diffs[c][y * (block - 1) + x] = cut[y * block + x + 1] - cut[y * block + x];
                        }
                    }
                    var bands = NoiseField.BandSigmasOf(cut, block, block);
                    if (bands[0] > 0)
                    {
                        ratios.Add(bands[1] / bands[0]);
                    }
                }
                for (var c = 0; c < _channels; c++)
                {
                    for (var d = c + 1; d < _channels; d++)
                    {
                        correlations.Add(RobustCorrelation(diffs[c], diffs[d]));
                    }
                }
            }
            var ratio = ratios.Count > 0 ? StatisticsHelper.MedianFast(CollectionsMarshal.AsSpan(ratios)) : double.NaN;
            var rho = correlations.Count > 0 ? Math.Clamp(StatisticsHelper.MedianFast(CollectionsMarshal.AsSpan(correlations)), 0.0, 0.95) : 0.0;
            // The shapes on offer: Warped at a resampling width of 0 to 2 px, and, under bilinear's own ratio (a drizzle at a
            // pixfrac under 1 correlates less than bilinear resampling does: the October Sculptor master reads 0.33), Warped at 0
            // blended with white noise. The share of the warped field is the resampling width's stand-in there, reported as a
            // negative width.
            var (resample, warpedShare) = (0.0, 1.0);
            if (double.IsFinite(ratio))
            {
                var nearest = double.PositiveInfinity;
                var shapes = new List<(double Sigma, double Share)>();
                for (var q = 0; q <= 8; q++)
                {
                    shapes.Add((q * 0.25, 1.0));
                }
                for (var q = 0; q < 10; q++)
                {
                    shapes.Add((0.0, q * 0.1));
                }
                foreach (var (s, share) in shapes)
                {
                    var field = GrainShape(256, s, share, new Random(17), new Random(23));
                    var bands = NoiseField.BandSigmasOf(field, 256, 256);
                    if (Math.Abs(bands[1] / bands[0] - ratio) < nearest)
                    {
                        nearest = Math.Abs(bands[1] / bands[0] - ratio);
                        (resample, warpedShare) = (s, share);
                    }
                }
            }
            var fields = new float[1 + _channels][];
            for (var f = 0; f < fields.Length; f++)
            {
                fields[f] = GrainShape(GrainTilePx, resample, warpedShare, new Random(seed * 31 + 1000 + f), new Random(seed * 31 + 2000 + f));
            }
            return (warpedShare < 1.0 ? warpedShare - 1.0 : resample, ratio, rho, fields);
        }

        // A unit-variance field: NoiseField.Warped at sigma, blended with white noise to the given share of its variance.
        private static float[] GrainShape(int size, double sigma, double warpedShare, Random warped, Random white)
        {
            var field = NoiseField.Warped(size, size, GrainRealisations, warped, sigma);
            if (warpedShare >= 1.0)
            {
                return field;
            }
            var w = NoiseField.White(size, size, white);
            var (a, b) = (Math.Sqrt(warpedShare), Math.Sqrt(1.0 - warpedShare));
            for (var i = 0; i < field.Length; i++)
            {
                field[i] = (float)(a * field[i] + b * w[i]);
            }
            return field;
        }

        // Pearson's correlation of two noise samples after clipping each at five robust sigmas.
        private static double RobustCorrelation(float[] a, float[] b)
        {
            var (ma, sa) = MedianSigma(a);
            var (mb, sb) = MedianSigma(b);
            double sab = 0, saa = 0, sbb = 0;
            for (var i = 0; i < a.Length; i++)
            {
                var (u, v) = (a[i] - ma, b[i] - mb);
                if (Math.Abs(u) > 5 * sa || Math.Abs(v) > 5 * sb)
                {
                    continue;
                }
                sab += u * v;
                saa += u * u;
                sbb += v * v;
            }
            return saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : 0.0;

            static (double Median, double Sigma) MedianSigma(float[] values)
            {
                float[] scratch = [.. values];
                var (median, mad) = StatisticsHelper.MedianAndMad(scratch.AsSpan());
                return (median, Math.Max(1.4826 * mad, 1e-12));
            }
        }

        // The noise against the level, per channel, as the synthetic cluster scenes draw it: the sky's own pixel noise, read in
        // the darkest quarter of the LevelBlockPx blocks mostly clear of every footprint and fill, growing as the root of the level
        // over the sky's, as shot noise does. Read off the data instead, the noise's rise with the level is the crowding's:
        // on Omega Cen's outskirts unresolved stars raise a block's spread with its level, a fit of variance against level
        // took that for noise, and the core's grain came out 350 against a sky of 8 (red), every channel its own, a core in
        // colour noise. The noise map round a footprint is no better in a crowded field: it is the crowding's texture.
        private LevelModel[] LevelModels(float[][] plate, BitMatrix footprints, BitMatrix inpainted)
        {
            const int block = LevelBlockPx;
            var models = new LevelModel[_channels];
            var gw = _width / block;
            var gh = _height / block;
            var values = new float[block * block];
            for (var c = 0; c < _channels; c++)
            {
                var samples = new List<(double Level, double Sigma)>();
                for (var by = 0; by < gh; by++)
                {
                    for (var bx = 0; bx < gw; bx++)
                    {
                        // A block counts when three quarters of it is clear, read on its clear pixels: a block wholly clear
                        // of footprints and fills is rare in a crowded field (none on the ASI585MC Tarantula), where the
                        // model then had nothing to read.
                        var m = 0;
                        for (var y = by * block; y < (by + 1) * block; y++)
                        {
                            for (var x = bx * block; x < (bx + 1) * block; x++)
                            {
                                var v = plate[c][y * _width + x];
                                if (!IsAbsent(x, y) && !footprints[y, x] && !inpainted[y, x] && float.IsFinite(v))
                                {
                                    values[m++] = v;
                                }
                            }
                        }
                        if (m < 3 * block * block / 4)
                        {
                            continue;
                        }
                        var (level, pixelMad) = StatisticsHelper.MedianAndMad(values.AsSpan(0, m));
                        if (float.IsFinite(level) && pixelMad > 0)
                        {
                            samples.Add((level, 1.4826 * pixelMad));
                        }
                    }
                }
                models[c] = LevelModel.Fit(samples);
            }
            return models;
        }
    }

    // The pixel noise at a level: the sky's, times root(level over the sky's level) above it. A sky whose level is not well
    // clear of zero (a master with its background taken out) says nothing of the photons under it, so its noise stays flat;
    // a channel with too few clear blocks answers the noise map's value instead.
    private readonly record struct LevelModel(double SkyLevel, double SkySigma)
    {
        public double Sigma(double level, double fallback)
            => !(SkySigma > 0) ? fallback
                : SkyLevel > 10.0 * SkySigma ? SkySigma * Math.Sqrt(Math.Max(level, SkyLevel) / SkyLevel)
                : SkySigma;

        public static LevelModel Fit(List<(double Level, double Sigma)> samples)
        {
            if (samples.Count < 16)
            {
                return new LevelModel(0, 0);
            }
            samples.Sort((a, b) => a.Level.CompareTo(b.Level));
            var quarter = Math.Max(4, samples.Count / 4);
            var levels = new double[quarter];
            var sigmas = new double[quarter];
            for (var i = 0; i < quarter; i++)
            {
                (levels[i], sigmas[i]) = samples[i];
            }
            return new LevelModel(StatisticsHelper.MedianFast(levels.AsSpan()), StatisticsHelper.MedianFast(sigmas.AsSpan()));
        }
    }

    // The catalogue's offset from what the builder found, per 256 px tile: the median over the matched stars in the tile and
    // its eight neighbours where they number at least eight, else the frame's median.
    private sealed class OffsetMap(int tilesX, int tilesY, (double Dx, double Dy)[] tiles)
    {
        public const int TilePx = 256;

        public (double Dx, double Dy) At(double x, double y)
            => tiles[Math.Clamp((int)(y / TilePx), 0, tilesY - 1) * tilesX + Math.Clamp((int)(x / TilePx), 0, tilesX - 1)];
    }

    // The fits in cells of the match radius, for the nearest-match query and the halo's neighbour tests.
    private sealed class FitGrid
    {
        private readonly Dictionary<long, List<int>> _cells = new();
        private readonly List<Fit> _fits;
        private readonly double _cell;

        public FitGrid(List<Fit> fits, double cell)
        {
            _fits = fits;
            _cell = Math.Max(4.0, cell);
            for (var i = 0; i < fits.Count; i++)
            {
                if (fits[i].Outcome is StarFitOutcome.NoFit or StarFitOutcome.Merged)
                {
                    continue;
                }
                var key = Key((int)Math.Floor(fits[i].X / _cell), (int)Math.Floor(fits[i].Y / _cell));
                if (!_cells.TryGetValue(key, out var list))
                {
                    _cells[key] = list = new List<int>();
                }
                list.Add(i);
            }
        }

        private static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        public int Nearest(double x, double y, double radius)
        {
            var best = -1;
            var bestD2 = radius * radius;
            foreach (var i in Within(x, y, radius, except: -1))
            {
                var f = _fits[i];
                var d2 = (f.X - x) * (f.X - x) + (f.Y - y) * (f.Y - y);
                if (d2 <= bestD2)
                {
                    bestD2 = d2;
                    best = i;
                }
            }
            return best;
        }

        public bool AnyBrighterWithin(double x, double y, double radius, double amplitude, int except)
        {
            foreach (var i in Within(x, y, radius, except))
            {
                if (_fits[i].Amplitude > amplitude)
                {
                    return true;
                }
            }
            return false;
        }

        public List<int> Within(double x, double y, double radius, int except)
        {
            var result = new List<int>();
            var (c0x, c1x) = ((int)Math.Floor((x - radius) / _cell), (int)Math.Floor((x + radius) / _cell));
            var (c0y, c1y) = ((int)Math.Floor((y - radius) / _cell), (int)Math.Floor((y + radius) / _cell));
            for (var cy = c0y; cy <= c1y; cy++)
            {
                for (var cx = c0x; cx <= c1x; cx++)
                {
                    if (!_cells.TryGetValue(Key(cx, cy), out var list))
                    {
                        continue;
                    }
                    foreach (var i in list)
                    {
                        var f = _fits[i];
                        if (i != except && (f.X - x) * (f.X - x) + (f.Y - y) * (f.Y - y) <= radius * radius)
                        {
                            result.Add(i);
                        }
                    }
                }
            }
            return result;
        }
    }
}
