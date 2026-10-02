using System;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// Fills holes in a plane with a push-pull pyramid: weighted 2x2 averages down (a hole and an absent pixel weigh
/// nothing), bilinear interpolation back up into whatever a level still lacks. A hole is filled from the data
/// nearest it at every scale, so a hole on a nebula takes the nebula's level and slope across it, and a hole of any
/// size is filled in one O(N) pass. Only hole pixels are written; known and absent pixels keep their values.
/// </summary>
internal static class PushPullFill
{
    public static void Fill(Span<float> plane, int width, int height, BitMatrix holes, BitMatrix? absent)
    {
        var values = new List<float[]>();
        var weights = new List<float[]>();
        var sizes = new List<(int W, int H)>();

        var n = width * height;
        var v0 = new float[n];
        var w0 = new float[n];
        var anyHole = false;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var known = !holes[y, x] && !(absent is { } a && a[y, x]) && float.IsFinite(plane[i]);
                v0[i] = known ? plane[i] : 0f;
                w0[i] = known ? 1f : 0f;
                anyHole |= holes[y, x];
            }
        }
        if (!anyHole)
        {
            return;
        }
        values.Add(v0);
        weights.Add(w0);
        sizes.Add((width, height));

        // Push: halve until one pixel, or until a level has no pixel left without weight.
        while (true)
        {
            var (pw, ph) = sizes[^1];
            if (pw == 1 && ph == 1 || !HasGap(weights[^1]))
            {
                break;
            }
            var qw = (pw + 1) / 2;
            var qh = (ph + 1) / 2;
            var pv = values[^1];
            var pwt = weights[^1];
            var qv = new float[qw * qh];
            var qwt = new float[qw * qh];
            for (var y = 0; y < qh; y++)
            {
                for (var x = 0; x < qw; x++)
                {
                    double sum = 0, wsum = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        var yy = 2 * y + dy;
                        if (yy >= ph)
                        {
                            continue;
                        }
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var xx = 2 * x + dx;
                            if (xx >= pw)
                            {
                                continue;
                            }
                            var w = pwt[yy * pw + xx];
                            sum += w * pv[yy * pw + xx];
                            wsum += w;
                        }
                    }
                    qv[y * qw + x] = wsum > 0 ? (float)(sum / wsum) : 0f;
                    qwt[y * qw + x] = (float)Math.Min(1.0, wsum);
                }
            }
            values.Add(qv);
            weights.Add(qwt);
            sizes.Add((qw, qh));
        }

        // Pull: each level takes what it lacks from the filled level above it.
        for (var level = values.Count - 2; level >= 0; level--)
        {
            var (pw, ph) = sizes[level];
            var (qw, qh) = sizes[level + 1];
            var pv = values[level];
            var pwt = weights[level];
            var qv = values[level + 1];
            for (var y = 0; y < ph; y++)
            {
                var sy = Math.Clamp((y + 0.5) / 2.0 - 0.5, 0.0, qh - 1);
                var y0 = (int)sy;
                var y1 = Math.Min(y0 + 1, qh - 1);
                var fy = sy - y0;
                for (var x = 0; x < pw; x++)
                {
                    var i = y * pw + x;
                    var w = pwt[i];
                    if (w >= 1f)
                    {
                        continue;
                    }
                    var sx = Math.Clamp((x + 0.5) / 2.0 - 0.5, 0.0, qw - 1);
                    var x0 = (int)sx;
                    var x1 = Math.Min(x0 + 1, qw - 1);
                    var fx = sx - x0;
                    var up = (1 - fy) * ((1 - fx) * qv[y0 * qw + x0] + fx * qv[y0 * qw + x1])
                        + fy * ((1 - fx) * qv[y1 * qw + x0] + fx * qv[y1 * qw + x1]);
                    pv[i] = (float)(w * pv[i] + (1 - w) * up);
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (holes[y, x] && !(absent is { } a && a[y, x]))
                {
                    plane[y * width + x] = v0[y * width + x];
                }
            }
        }
    }

    private static bool HasGap(float[] weights)
    {
        foreach (var w in weights)
        {
            if (w <= 0f)
            {
                return true;
            }
        }
        return false;
    }
}
