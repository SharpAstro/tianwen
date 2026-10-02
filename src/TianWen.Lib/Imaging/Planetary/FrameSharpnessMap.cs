using System;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Builds a per-pixel local-sharpness map of a frame: smoothed Sobel gradient energy on the luminance
/// proxy, normalised so its mean over the frame is ~1. Used as the spatially-varying weight in the per-AP
/// "best-of" integration -- where a frame is locally sharp its map is &gt; 1 and it contributes more;
/// where it is locally soft the map is &lt; 1 and it contributes less. Normalising to unit mean makes the
/// weight a <i>relative</i> local sharpness, so a globally brighter frame is not preferred outright (the
/// global quality weight, applied separately, carries the frame's overall rank).
/// </summary>
public static class FrameSharpnessMap
{
    /// <summary>
    /// Returns a full-frame (Height x Width) map of normalised local sharpness. <paramref name="smoothRadius"/>
    /// box-blurs the raw gradient energy so the weight is regional (not single-pixel noisy).
    /// </summary>
    public static float[,] Build(Image frame, int smoothRadius = 3)
    {
        ArgumentNullException.ThrowIfNull(frame);
        int w = frame.Width, h = frame.Height, channels = frame.ChannelCount;
        var inv = 1f / channels;

        // Every pass but the mean runs in bands of rows (ParallelFor.RunBands): each pixel is computed from the plane before
        // it exactly as in one walk, so the map is the same bit for bit, and the blur's 49 taps a pixel were 17 % of a
        // 3,000-frame stack's time in one walk (2026-10-02). Luminance proxy, the channels added in order at each pixel.
        // The planes resolved once, before the bands, never by each band (residency is the frame's to resolve).
        var planes = frame.ResidentPlanes();
        var luma = new float[h, w];
        ParallelFor.RunBands(h, (yStart, yEnd) =>
        {
            for (var c = 0; c < channels; c++)
            {
                var plane = planes[c];
                for (var y = yStart; y < yEnd; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        luma[y, x] += plane[y, x] * inv;
                    }
                }
            }
        });

        // Raw Sobel gradient energy.
        var energy = new float[h, w];
        ParallelFor.RunBands(Math.Max(0, h - 2), (bandStart, bandEnd) =>
        {
            for (var y = bandStart + 1; y < bandEnd + 1; y++)
            {
                for (var x = 1; x < w - 1; x++)
                {
                    var gx = (luma[y - 1, x + 1] + (2f * luma[y, x + 1]) + luma[y + 1, x + 1])
                           - (luma[y - 1, x - 1] + (2f * luma[y, x - 1]) + luma[y + 1, x - 1]);
                    var gy = (luma[y + 1, x - 1] + (2f * luma[y + 1, x]) + luma[y + 1, x + 1])
                           - (luma[y - 1, x - 1] + (2f * luma[y - 1, x]) + luma[y - 1, x + 1]);
                    energy[y, x] = (gx * gx) + (gy * gy);
                }
            }
        });

        // Box-blur to a regional sharpness.
        var map = new float[h, w];
        ParallelFor.RunBands(h, (yStart, yEnd) =>
        {
            for (var y = yStart; y < yEnd; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    float acc = 0;
                    var cnt = 0;
                    var y0 = Math.Max(0, y - smoothRadius);
                    var y1 = Math.Min(h - 1, y + smoothRadius);
                    var x0 = Math.Max(0, x - smoothRadius);
                    var x1 = Math.Min(w - 1, x + smoothRadius);
                    for (var yy = y0; yy <= y1; yy++)
                    {
                        for (var xx = x0; xx <= x1; xx++)
                        {
                            acc += energy[yy, xx];
                            cnt++;
                        }
                    }

                    map[y, x] = acc / cnt;
                }
            }
        });

        // Then normalise to unit mean, its sum taken in one walk in row order, as it always was: a sum is the one step whose
        // bits depend on the order it is taken in.
        double sum = 0;
        var n = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                sum += map[y, x];
                n++;
            }
        }

        var mean = n > 0 ? sum / n : 0;
        if (mean > 0)
        {
            var scale = (float)(1.0 / mean);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    map[y, x] *= scale;
                }
            }
        }
        else
        {
            // No gradient anywhere: uniform weight so the frame still contributes by its global quality.
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    map[y, x] = 1f;
                }
            }
        }

        return map;
    }
}
