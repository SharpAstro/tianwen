using System;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Shared luminance-tile extraction for the planetary aligner: fills a square, power-of-two tile of the
/// channel-mean luminance proxy, centred on an integer-rounded point and zero-padded where it runs off
/// the frame. Used by both the global aligner and the per-alignment-point matcher (one source of truth
/// for tile extraction, so the phase-correlation inputs are produced identically everywhere).
/// </summary>
internal static class PlanetaryTile
{
    /// <summary>
    /// Fills <paramref name="dst"/> (length <c>size*size</c>, row-major) with a luminance tile centred on
    /// the integer-rounded <c>(centerX, centerY)</c>; samples outside the frame are zero.
    /// </summary>
    public static void ExtractLuma(Image frame, double centerX, double centerY, int size, float[] dst)
    {
        var originX = (int)Math.Round(centerX) - (size / 2);
        var originY = (int)Math.Round(centerY) - (size / 2);
        int w = frame.Width, h = frame.Height, channels = frame.ChannelCount;
        var inv = 1f / channels;

        for (var ty = 0; ty < size; ty++)
        {
            var sy = originY + ty;
            var dstRow = ty * size;
            for (var tx = 0; tx < size; tx++)
            {
                var sx = originX + tx;
                var v = 0f;
                if (sx >= 0 && sx < w && sy >= 0 && sy < h)
                {
                    for (var c = 0; c < channels; c++)
                    {
                        v += frame[c, sy, sx];
                    }

                    v *= inv;
                }

                dst[dstRow + tx] = v;
            }
        }
    }
}
