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

        // Luminance proxy.
        var luma = new float[h, w];
        for (var c = 0; c < channels; c++)
        {
            var span = frame.GetChannelSpan(c);
            for (var y = 0; y < h; y++)
            {
                var row = y * w;
                for (var x = 0; x < w; x++)
                {
                    luma[y, x] += span[row + x] * inv;
                }
            }
        }

        // Raw Sobel gradient energy.
        var energy = new float[h, w];
        for (var y = 1; y < h - 1; y++)
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

        // Box-blur to a regional sharpness, then normalise to unit mean.
        var map = new float[h, w];
        double sum = 0;
        var n = 0;
        for (var y = 0; y < h; y++)
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

                var v = acc / cnt;
                map[y, x] = v;
                sum += v;
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
