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
public readonly record struct FillProbeBand(
    int Radius, int Holes, int SmoothHoles, int StructuredHoles, float StructureErrorSmooth, float StructureErrorStructured, float GrainRatio);

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
        var (channels, width, height) = image.Shape;
        var absent = image.AbsentPixels();
        var fwhm = plate.Statistics.FwhmPx[^1];
        var original = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            original[c] = image.GetChannelSpan(c).ToArray();
        }
        var lum = Luminance(original, width * height);
        var excluded = new BitMatrix(height, width);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                excluded[y, x] = plate.Subtracted[y, x] || plate.Inpainted[y, x] || (absent is { } a && a[y, x]);
            }
        }
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
                filled[c] = (float[])original[c].Clone();
            }
            HoleFill.Fill(filled, width, height, holes, absent, fwhm, seed + radius, cancellationToken);
            var filledLum = Luminance(filled, width * height);

            var smooth = new List<float>();
            var structured = new List<float>();
            var grain = new List<float>();
            foreach (var (cx, cy) in centres)
            {
                var (error, structure, ratio) = Compare(lum, filledLum, rms, cx, cy, radius, width);
                (structure > 1f ? structured : smooth).Add(error);
                if (float.IsFinite(ratio))
                {
                    grain.Add(ratio);
                }
            }
            bands.Add(new FillProbeBand(radius, centres.Count, smooth.Count, structured.Count, Median(smooth), Median(structured), Median(grain)));
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
    // carried above its noise, and the filled hole's pixel-to-pixel scatter over the original's.
    private static (float Error, float Structure, float GrainRatio) Compare(float[] original, float[] filled, float[] rms, int cx, int cy, int radius, int width)
    {
        double diff2 = 0, sum = 0, sum2 = 0, sigma2 = 0, dOrig = 0, dFill = 0;
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
        return (error, structure, grain);
    }

    private static float Median(List<float> values)
    {
        var finite = values.Where(float.IsFinite).OrderBy(static v => v).ToArray();
        return finite.Length == 0 ? float.NaN : finite[finite.Length / 2];
    }
}
