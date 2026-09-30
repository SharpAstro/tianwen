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

    /// <summary>
    /// <see cref="ExtractLuma"/> centred on <c>(centerX, centerY)</c> exactly, the sub-pixel part resampled by Lanczos-3: a
    /// frame's patch cut at its sub-pixel global shift lies where the reference's patch does, so a match reads only what moved
    /// beyond that shift. Cut at the rounded shift instead, a match had to recover the shift's own fraction, and along a
    /// planet's belts, where a patch holds nothing to place it by, it could not: the residual locked to the whole pixel and so
    /// did every frame's mesh (docs/plans/planetary-restoration.md, R5a). A whole-pixel centre is <see cref="ExtractLuma"/>
    /// exactly. The fraction is one for the whole patch, so each axis' six weights are found once and applied separably;
    /// <paramref name="scratch"/> holds <see cref="ScratchLength"/> samples. Samples off the frame are zero.
    /// </summary>
    public static void ExtractLumaAt(Image frame, double centerX, double centerY, int size, float[] dst, float[] scratch)
    {
        var (originX, originY) = (centerX - (size / 2), centerY - (size / 2));
        var (baseX, baseY) = ((int)Math.Floor(originX), (int)Math.Floor(originY));
        var (fx, fy) = ((float)(originX - baseX), (float)(originY - baseY));
        if (fx == 0f && fy == 0f)
        {
            ExtractLuma(frame, baseX + (size / 2), baseY + (size / 2), size, dst);
            return;
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(scratch.Length, ScratchLength(size));

        // The luma at every whole pixel the taps reach, from two left of the patch to three right of it (and so for rows).
        var span = size + 5;
        int w = frame.Width, h = frame.Height, channels = frame.ChannelCount;
        var inv = 1f / channels;
        var block = scratch.AsSpan(0, span * span);
        for (var r = 0; r < span; r++)
        {
            var sy = baseY - 2 + r;
            for (var c = 0; c < span; c++)
            {
                var sx = baseX - 2 + c;
                var v = 0f;
                if (sx >= 0 && sx < w && sy >= 0 && sy < h)
                {
                    for (var ch = 0; ch < channels; ch++)
                    {
                        v += frame[ch, sy, sx];
                    }
                    v *= inv;
                }
                block[(r * span) + c] = v;
            }
        }

        // Each axis' weights, normalised so a flat patch stays flat.
        Span<float> wx = stackalloc float[6];
        Span<float> wy = stackalloc float[6];
        Image.Lanczos3Weights(fx, wx);
        Image.Lanczos3Weights(fy, wy);
        Normalise(wx);
        Normalise(wy);
        var rows = scratch.AsSpan(span * span, span * size);
        for (var r = 0; r < span; r++)
        {
            var source = block.Slice(r * span, span);
            for (var tx = 0; tx < size; tx++)
            {
                var v = 0f;
                for (var i = 0; i < 6; i++)
                {
                    v += wx[i] * source[tx + i];
                }
                rows[(r * size) + tx] = v;
            }
        }
        for (var ty = 0; ty < size; ty++)
        {
            for (var tx = 0; tx < size; tx++)
            {
                var v = 0f;
                for (var j = 0; j < 6; j++)
                {
                    v += wy[j] * rows[((ty + j) * size) + tx];
                }
                dst[(ty * size) + tx] = v;
            }
        }

        static void Normalise(Span<float> weights)
        {
            var sum = 0f;
            foreach (var weight in weights)
            {
                sum += weight;
            }
            for (var i = 0; i < weights.Length; i++)
            {
                weights[i] /= sum;
            }
        }
    }

    /// <summary>The scratch <see cref="ExtractLumaAt"/> takes for a patch of <paramref name="size"/>.</summary>
    public static int ScratchLength(int size) => ((size + 5) * (size + 5)) + ((size + 5) * size);
}
