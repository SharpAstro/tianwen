using System;
using System.Drawing;
using System.Numerics.Tensors;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Builds a single-channel luminance proxy (the per-pixel mean across all channels) over a region, into
/// a caller-owned buffer. This is the layout-agnostic input shared by the quality estimators, the disk
/// finder, and (later) the feature detector / AP tracker: mono passes through, RGB averages its three
/// planes, split-CFA averages its four sub-planes (the "quadrant sum" luminance proxy the plan calls
/// for). Keeping it one place keeps the proxy definition a single source of truth.
/// </summary>
internal static class LumaProxy
{
    /// <summary>The whole-frame region for <paramref name="frame"/>.</summary>
    public static Rectangle FullFrame(Image frame) => new(0, 0, frame.Width, frame.Height);

    /// <summary>
    /// Fills <paramref name="dst"/> (length <c>region.Width * region.Height</c>, row-major) with the
    /// channel-mean luminance of <paramref name="frame"/> over <paramref name="region"/>. The region must
    /// lie within the frame.
    /// </summary>
    public static void Fill(Image frame, Rectangle region, Span<float> dst)
    {
        var rw = region.Width;
        var rh = region.Height;
        var count = rw * rh;
        var area = dst[..count];
        area.Clear();

        var w = frame.Width;
        var channels = frame.ChannelCount;

        // Accumulate each channel's contribution with the per-channel span hoisted out of the pixel loop.
        for (var ch = 0; ch < channels; ch++)
        {
            var src = frame.GetChannelSpan(ch);
            for (var yy = 0; yy < rh; yy++)
            {
                var srcRow = ((region.Top + yy) * w) + region.Left;
                var dstRow = yy * rw;
                for (var xx = 0; xx < rw; xx++)
                {
                    area[dstRow + xx] += src[srcRow + xx];
                }
            }
        }

        if (channels > 1)
        {
            TensorPrimitives.Multiply(area, 1f / channels, area);
        }
    }
}
