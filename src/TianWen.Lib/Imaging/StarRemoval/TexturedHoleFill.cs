using System;
using System.Collections.Generic;
using System.Threading;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// R2e's S4 (docs/plans/star-remover-training.md, "S4, pre-registered: the fill as a conditional draw"): R0's fill
/// (<see cref="HoleFill"/>, a push-pull level and the plate's grain) with the steered texture drawn into each hole as a
/// conditional draw. Per hole, the texture T is drawn over the hole and its surroundings by a generator read off the sky
/// round the holes (<see cref="SyntheticBackground"/>, the holes masked), and the hole takes T less T's own push-pull.
/// That residual is zero at the hole's edge and T's own structure deep inside, so the drawn structure fades in from the
/// real sky, at the scales under the hole's size, along the orientation round it. Deterministic: a hole's draw is seeded
/// by its place.
/// </summary>
internal static class TexturedHoleFill
{
    /// <summary>The margin round a hole the texture is drawn over, at least this many pixels, so its push-pull has sky to
    /// interpolate from.</summary>
    public const int MinMarginPx = 8;

    /// <summary>
    /// Fills <paramref name="holes"/> in every plane in place: <see cref="HoleFill.Fill"/>, then each hole's conditional
    /// draw added, no higher than the larger of the fill's own level and <paramref name="ceiling"/> (the data the plate
    /// was made from), since a star only adds light.
    /// </summary>
    public static void Fill(float[][] planes, int width, int height, BitMatrix holes, BitMatrix? absent, double fwhm, int seed,
        float[][]? ceiling, SyntheticBackground.Steering? steering, CancellationToken ct)
    {
        HoleFill.Fill(planes, width, height, holes, absent, fwhm, seed, ceiling, ct);
        if (!holes.Any())
        {
            return;
        }

        // The generator reads the sky round the holes: the holes and the ring are masked out of its amplitude maps and its
        // fine orientation, and its coarse part is the filled plate's, which inside a hole is the push-pull level.
        var masked = new BitMatrix(height, width);
        var data = new float[planes.Length][,];
        for (var c = 0; c < planes.Length; c++)
        {
            var plane = new float[height, width];
            Buffer.BlockCopy(planes[c], 0, plane, 0, width * height * sizeof(float));
            data[c] = plane;
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                masked[y, x] = holes[y, x] || (absent is { } a && a[y, x]);
            }
        }
        var generator = SyntheticBackground.Build(new Image(data, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()), masked, fwhm, steering: steering);

        foreach (var (x0, y0, x1, y1) in Components(holes, width, height))
        {
            ct.ThrowIfCancellationRequested();
            var margin = Math.Max(MinMarginPx, Math.Max(x1 - x0, y1 - y0) + 1);
            var cx0 = x0 - margin;
            var cy0 = y0 - margin;
            var size = Math.Max(x1 - x0, y1 - y0) + 1 + (2 * margin);
            var texture = generator.TextureOnly(cx0, cy0, size, new Random(SyntheticBackgroundPreview.CellSeed(seed, (x0 + x1) / 2, (y0 + y1) / 2)));

            var local = new BitMatrix(size, size);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var (fx, fy) = (cx0 + x, cy0 + y);
                    local[y, x] = fx >= 0 && fy >= 0 && fx < width && fy < height && holes[fy, fx];
                }
            }
            for (var c = 0; c < planes.Length; c++)
            {
                var interpolated = (float[])texture[c].Clone();
                PushPullFill.Fill(interpolated, size, size, local, absent: null);
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        if (!local[y, x])
                        {
                            continue;
                        }
                        var at = ((cy0 + y) * width) + cx0 + x;
                        var value = planes[c][at];
                        var drawn = value + (texture[c][(y * size) + x] - interpolated[(y * size) + x]);
                        if (ceiling is { } top && float.IsFinite(top[c][at]))
                        {
                            drawn = Math.Min(drawn, Math.Max(value, top[c][at]));
                        }
                        planes[c][at] = drawn;
                    }
                }
            }
        }
    }

    // The holes' connected components (8-connected), each as its bounding box.
    private static List<(int X0, int Y0, int X1, int Y1)> Components(BitMatrix holes, int width, int height)
    {
        var seen = new BitMatrix(height, width);
        var boxes = new List<(int, int, int, int)>();
        var stack = new Stack<(int X, int Y)>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!holes[y, x] || seen[y, x])
                {
                    continue;
                }
                var (x0, y0, x1, y1) = (x, y, x, y);
                seen[y, x] = true;
                stack.Push((x, y));
                while (stack.Count > 0)
                {
                    var (px, py) = stack.Pop();
                    (x0, y0, x1, y1) = (Math.Min(x0, px), Math.Min(y0, py), Math.Max(x1, px), Math.Max(y1, py));
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var (nx, ny) = (px + dx, py + dy);
                            if (nx >= 0 && ny >= 0 && nx < width && ny < height && holes[ny, nx] && !seen[ny, nx])
                            {
                                seen[ny, nx] = true;
                                stack.Push((nx, ny));
                            }
                        }
                    }
                }
                boxes.Add((x0, y0, x1, y1));
            }
        }
        return boxes;
    }
}
