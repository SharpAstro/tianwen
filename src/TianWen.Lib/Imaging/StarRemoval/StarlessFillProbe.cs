using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using TianWen.Lib.Imaging.Sources;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>What the fill got wrong at one hole size, measured where the pixels were known.</summary>
/// <param name="Radius">The holes' radius, pixels.</param>
/// <param name="Holes">Holes cut and filled.</param>
/// <param name="SmoothHoles">Holes whose pixels carried no structure above the noise.</param>
/// <param name="StructuredHoles">Holes over structure (its amplitude above the noise's).</param>
/// <param name="StructureErrorSmooth">Median over the smooth holes of the signal the fill got wrong, in local sigma:
/// the RMS of filled minus original with both noises taken out (the fill's added grain and the original's noise, one
/// sigma each), so a fill that predicts the hole perfectly reads 0.</param>
/// <param name="StructureErrorStructured">The same over the holes on structure.</param>
/// <param name="GrainRatio">Median ratio of the filled hole's pixel-to-pixel scatter to the original's: 1 is the plate's
/// grain, below 1 a fill smoother than the sky around it.</param>
/// <param name="LevelStructured">Median over the structured holes of the mean residual, filled minus original, in local
/// sigma (R2e's S4: a fill that adds no bias reads 0).</param>
/// <param name="EnergyStructured">Median over the structured holes of the band energy at <paramref name="ReadScalePx"/>
/// inside the hole's interior (where its own edge is out of the scale's kernel), filled over original; NaN for a hole too
/// small to have one.</param>
/// <param name="AlignmentStructured">Median over the structured holes of the alignment between the fill's structure at
/// <paramref name="ReadScalePx"/> in the interior and the original's in the ring round the hole (one to two radii), +1
/// running the same way, -1 across: whether the fill carries the sky's direction into the hole.</param>
/// <param name="AlignmentTruth">The same read on the original's interior: how far the sky's own direction carries.</param>
/// <param name="ReadScalePx">The starlet scale the energy and alignment are read at, pixels; 0 where the hole is too small.</param>
public readonly record struct FillProbeBand(
    int Radius, int Holes, int SmoothHoles, int StructuredHoles, float StructureErrorSmooth, float StructureErrorStructured, float GrainRatio,
    float LevelStructured = float.NaN, float EnergyStructured = float.NaN, float AlignmentStructured = float.NaN, float AlignmentTruth = float.NaN,
    int ReadScalePx = 0);

/// <summary>
/// Measures the starless plate's fill on pixels whose truth is known (docs/plans/star-remover-training.md, R0, "Fill
/// error on known pixels"): discs cut at random star-free places of the plate, nebulae included, filled by the same
/// <see cref="HoleFill"/> the builder uses, and compared with what was there. A saturated core is the hole that matters,
/// so the report also gives the sizes the builder's own holes had (<see cref="CoreRadii"/>).
/// </summary>
public static class StarlessFillProbe
{
    /// <summary>The hole radii the report measures at, pixels.</summary>
    public static ImmutableArray<int> DefaultRadii { get; } = ImmutableArray.Create(3, 6, 12, 24);

    /// <summary>Cuts up to <paramref name="holesPerRadius"/> non-overlapping discs of each radius where the plate has no star,
    /// no fill and no absent pixel within three pixels, fills them, and compares.</summary>
    public static ImmutableArray<FillProbeBand> Measure(
        StarlessPlate plate, IReadOnlyList<int> radii, int holesPerRadius = 100, int seed = 1, CancellationToken cancellationToken = default)
    {
        var image = plate.Plate;
        var (_, width, height) = image.Shape;
        var absent = image.AbsentPixels();
        var excluded = new BitMatrix(height, width);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                excluded[y, x] = plate.Subtracted[y, x] || plate.Inpainted[y, x] || (absent is { } a && a[y, x]);
            }
        }
        return Measure(image, excluded, plate.Statistics.FwhmPx[^1], radii, holesPerRadius, seed, textured: null, cancellationToken);
    }

    /// <summary>
    /// The same probe on any plate: <paramref name="excluded"/> the pixels no hole may come near (a plate's subtracted and
    /// filled pixels and its ring), <paramref name="fwhm"/> its PSF width. With <paramref name="textured"/> the holes are
    /// filled by R2e's S4 (<see cref="TexturedHoleFill"/>, that steer); the holes, the grain and the seed are the same
    /// either way, so the two fills are compared on the same holes.
    /// </summary>
    public static ImmutableArray<FillProbeBand> Measure(
        Image image, BitMatrix excluded, double fwhm, IReadOnlyList<int> radii, int holesPerRadius, int seed,
        SyntheticBackground.Steering? textured, CancellationToken cancellationToken)
    {
        var (channels, width, height) = image.Shape;
        var absent = image.AbsentPixels();
        var original = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            original[c] = image.GetChannelSpan(c).ToArray();
        }
        var lum = Luminance(original, width * height);
        var sky = BackgroundMap.Estimate(lum, width, height, excluded, new BackgroundMapOptions(BlockSize: PointSourceFinder.SkyBlockFor(fwhm)));
        var rms = new float[width * height];
        sky.FillRms(rms);

        var bands = ImmutableArray.CreateBuilder<FillProbeBand>(radii.Count);
        var rng = new Random(seed);
        foreach (var radius in radii)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var centres = PlaceHoles(excluded, width, height, radius, holesPerRadius, rng);
            if (centres.Count == 0)
            {
                bands.Add(new FillProbeBand(radius, 0, 0, 0, float.NaN, float.NaN, float.NaN));
                continue;
            }
            var holes = new BitMatrix(height, width);
            foreach (var (cx, cy) in centres)
            {
                Stamp(holes, cx, cy, radius, width, height);
            }
            var filled = new float[channels][];
            for (var c = 0; c < channels; c++)
            {
                filled[c] = [.. original[c]];
            }
            if (textured is { } steering)
            {
                TexturedHoleFill.Fill(filled, width, height, holes, absent, fwhm, seed + radius, ceiling: null, steering, cancellationToken);
            }
            else
            {
                HoleFill.Fill(filled, width, height, holes, absent, fwhm, seed + radius, ceiling: null, cancellationToken);
            }
            var filledLum = Luminance(filled, width * height);

            // The scale the energy and the direction are read at: the largest whose kernel (2^(j+1) px either way) leaves
            // the hole an interior of at least half its radius; none for a hole too small for one.
            var j = radius >= 24 ? 2 : radius >= 12 ? 1 : -1;
            var interior = j >= 0 ? radius - (2 << j) : 0;
            Structure? truth = null, fill = null;
            if (j >= 0)
            {
                truth = Structure.Of(lum, width, height, j);
                fill = Structure.Of(filledLum, width, height, j);
            }

            var smooth = new List<float>();
            var structured = new List<float>();
            var grain = new List<float>();
            var level = new List<float>();
            var energy = new List<float>();
            var alignment = new List<float>();
            var alignmentTruth = new List<float>();
            foreach (var (cx, cy) in centres)
            {
                var (error, structure, ratio, mean) = Compare(lum, filledLum, rms, cx, cy, radius, width);
                (structure > 1f ? structured : smooth).Add(error);
                if (float.IsFinite(ratio))
                {
                    grain.Add(ratio);
                }
                if (structure <= 1f)
                {
                    continue;
                }
                level.Add(mean);
                if (truth is { } t && fill is { } f && cx - (2 * radius) >= 0 && cy - (2 * radius) >= 0 && cx + (2 * radius) < width && cy + (2 * radius) < height)
                {
                    var (eTruth, ringTruth, inTruth) = t.Read(cx, cy, interior, radius);
                    var (eFill, _, inFill) = f.Read(cx, cy, interior, radius);
                    if (eTruth > 0)
                    {
                        energy.Add((float)(eFill / eTruth));
                    }
                    alignment.Add((float)Structure.Agreement(inFill, ringTruth));
                    alignmentTruth.Add((float)Structure.Agreement(inTruth, ringTruth));
                }
            }
            bands.Add(new FillProbeBand(radius, centres.Count, smooth.Count, structured.Count, Median(smooth), Median(structured), Median(grain),
                Median(level), Median(energy), Median(alignment), Median(alignmentTruth), j >= 0 ? 1 << j : 0));
        }
        return bands.MoveToImmutable();
    }

    /// <summary>The equivalent radii (root of area over pi) of the plate's filled regions of at least
    /// <paramref name="minArea"/> pixels: the sizes the saturated cores took, sorted ascending.</summary>
    public static ImmutableArray<float> CoreRadii(StarlessPlate plate, int minArea = 20)
    {
        var mask = plate.Inpainted;
        var (_, width, height) = plate.Plate.Shape;
        var seen = new BitMatrix(height, width);
        var radii = new List<float>();
        var stack = new Stack<(int X, int Y)>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!mask[y, x] || seen[y, x])
                {
                    continue;
                }
                var area = 0;
                seen[y, x] = true;
                stack.Push((x, y));
                while (stack.Count > 0)
                {
                    var (px, py) = stack.Pop();
                    area++;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var nx = px + dx;
                            var ny = py + dy;
                            if (nx >= 0 && ny >= 0 && nx < width && ny < height && mask[ny, nx] && !seen[ny, nx])
                            {
                                seen[ny, nx] = true;
                                stack.Push((nx, ny));
                            }
                        }
                    }
                }
                if (area >= minArea)
                {
                    radii.Add((float)Math.Sqrt(area / Math.PI));
                }
            }
        }
        radii.Sort();
        return radii.ToImmutableArray();
    }

    private static float[] Luminance(float[][] planes, int n)
    {
        if (planes.Length == 1)
        {
            return planes[0];
        }
        var lum = new float[n];
        for (var i = 0; i < n; i++)
        {
            var sum = 0f;
            foreach (var p in planes)
            {
                sum += p[i];
            }
            lum[i] = sum / planes.Length;
        }
        return lum;
    }

    private static List<(int X, int Y)> PlaceHoles(BitMatrix excluded, int width, int height, int radius, int count, Random rng)
    {
        var margin = radius + 3;
        var centres = new List<(int X, int Y)>();
        if (width <= 2 * margin || height <= 2 * margin)
        {
            return centres;
        }
        for (var attempt = 0; attempt < count * 50 && centres.Count < count; attempt++)
        {
            var cx = rng.Next(margin, width - margin);
            var cy = rng.Next(margin, height - margin);
            if (centres.Any(c => (c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy) < 4 * margin * margin))
            {
                continue;
            }
            var clear = true;
            for (var y = cy - margin; y <= cy + margin && clear; y++)
            {
                for (var x = cx - margin; x <= cx + margin; x++)
                {
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= margin * margin && excluded[y, x])
                    {
                        clear = false;
                        break;
                    }
                }
            }
            if (clear)
            {
                centres.Add((cx, cy));
            }
        }
        return centres;
    }

    private static void Stamp(BitMatrix mask, int cx, int cy, int radius, int width, int height)
    {
        for (var y = Math.Max(0, cy - radius); y <= Math.Min(height - 1, cy + radius); y++)
        {
            for (var x = Math.Max(0, cx - radius); x <= Math.Min(width - 1, cx + radius); x++)
            {
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius)
                {
                    mask[y, x] = true;
                }
            }
        }
    }

    // Over one hole, in local sigma: the signal the fill got wrong (both noises out), how much structure the original
    // carried above its noise, the filled hole's pixel-to-pixel scatter over the original's, and the mean residual.
    private static (float Error, float Structure, float GrainRatio, float Level) Compare(float[] original, float[] filled, float[] rms, int cx, int cy, int radius, int width)
    {
        double diff = 0, diff2 = 0, sum = 0, sum2 = 0, sigma2 = 0, dOrig = 0, dFill = 0;
        var n = 0;
        var nd = 0;
        for (var y = cy - radius; y <= cy + radius; y++)
        {
            for (var x = cx - radius; x <= cx + radius; x++)
            {
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > radius * radius)
                {
                    continue;
                }
                var i = y * width + x;
                var s = Math.Max(rms[i], 1e-12f);
                var o = original[i] / s;
                var f = filled[i] / s;
                diff += f - o;
                diff2 += (f - o) * (f - o);
                sum += o;
                sum2 += o * o;
                sigma2 += 1.0;
                n++;
                if (x < cx + radius && (x + 1 - cx) * (x + 1 - cx) + (y - cy) * (y - cy) <= radius * radius)
                {
                    var o1 = original[i + 1] / s;
                    var f1 = filled[i + 1] / s;
                    dOrig += (o1 - o) * (o1 - o);
                    dFill += (f1 - f) * (f1 - f);
                    nd++;
                }
            }
        }
        var error = (float)Math.Sqrt(Math.Max(0.0, diff2 / n - 2.0));
        var variance = sum2 / n - (sum / n) * (sum / n);
        var structure = (float)Math.Sqrt(Math.Max(0.0, variance - sigma2 / n));
        var grain = nd > 0 && dOrig > 0 ? (float)Math.Sqrt(dFill / dOrig) : float.NaN;
        return (error, structure, grain, (float)(diff / n));
    }

    // One luminance's starlet scale j over the frame, with its structure tensor (a window of twice the scale): what S4's
    // energy and direction reads take per hole.
    private sealed record Structure(float[] Detail, float[] Coherence, float[] Cos2, float[] Sin2, int Width)
    {
        public static Structure Of(float[] luminance, int width, int height, int j)
        {
            var detail = ATrousWaveletTransform.Decompose(luminance, width, height, j + 1).Detail(j).ToArray();
            var (coherence, cos2, sin2, _) = SkyTexture.StructureTensor(detail, width, height, (float)(SkyTexture.TensorWindowScales * (1 << j)));
            return new Structure(detail, coherence, cos2, sin2, width);
        }

        // The energy inside the interior, and the coherence-weighted doubled-angle direction of the ring (one to two radii)
        // and of the interior.
        public (double Energy, (double C, double S) Ring, (double C, double S) Inside) Read(int cx, int cy, int interior, int radius)
        {
            double energy = 0, rc = 0, rs = 0, ic = 0, iS = 0;
            for (var y = cy - (2 * radius); y <= cy + (2 * radius); y++)
            {
                for (var x = cx - (2 * radius); x <= cx + (2 * radius); x++)
                {
                    var r2 = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                    var i = (y * Width) + x;
                    if (r2 <= interior * interior)
                    {
                        energy += Detail[i] * Detail[i];
                        ic += Coherence[i] * Cos2[i];
                        iS += Coherence[i] * Sin2[i];
                    }
                    else if (r2 > radius * radius && r2 <= 4 * radius * radius)
                    {
                        rc += Coherence[i] * Cos2[i];
                        rs += Coherence[i] * Sin2[i];
                    }
                }
            }
            return (energy, (rc, rs), (ic, iS));
        }

        // cos 2 (theta_a - theta_b) of two doubled-angle directions: +1 the same way, -1 across, 0 for no direction.
        public static double Agreement((double C, double S) a, (double C, double S) b)
        {
            var norm = Math.Sqrt(((a.C * a.C) + (a.S * a.S)) * ((b.C * b.C) + (b.S * b.S)));
            return norm > 0 ? ((a.C * b.C) + (a.S * b.S)) / norm : 0;
        }
    }

    private static float Median(List<float> values)
    {
        var finite = values.Where(float.IsFinite).OrderBy(static v => v).ToArray();
        return finite.Length == 0 ? float.NaN : finite[finite.Length / 2];
    }
}
