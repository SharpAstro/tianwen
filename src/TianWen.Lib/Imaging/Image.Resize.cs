using System;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Returns a bilinearly-resampled copy of this image at the requested
    /// dimensions. NaN pixels are propagated -- if any of the 4 neighbours
    /// is NaN, the output pixel is NaN.
    /// </summary>
    /// <remarks>
    /// <para>Matches OpenCV's <c>cv2.resize(..., interpolation=cv2.INTER_LINEAR)</c>
    /// pixel-centre convention: the source sampling coordinate for output
    /// pixel <c>(y_t, x_t)</c> is <c>((y_t + 0.5) * srcH / newH - 0.5, ...)</c>.
    /// Used by ML preprocessing that needs a fixed-size input plate (e.g.
    /// GraXpert BGE's 240x240 model input).</para>
    ///
    /// <para>WCS / ImageMeta are passed through verbatim. Callers that need
    /// scale-aware WCS (CD matrix scaling) should adjust externally -- this
    /// primitive does not pretend to know about projection geometry.</para>
    /// </remarks>
    public Image BilinearResize(int newWidth, int newHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newHeight);

        var (channels, srcW, srcH) = Shape;
        if (newWidth == srcW && newHeight == srcH)
        {
            // No-op: identity resize. Build a shallow copy of channel data
            // so the result remains an independent Image (callers may .Release
            // intermediates without affecting `this`).
            var same = CreateChannelData(channels, srcH, srcW);
            for (var c = 0; c < channels; c++)
            {
                GetChannelSpan(c).CopyTo(MemoryMarshal.CreateSpan(ref same[c][0, 0], srcW * srcH));
            }
            return new Image(same, BitDepth, MaxValue, MinValue, Pedestal, imageMeta);
        }

        var sx = (double)srcW / newWidth;
        var sy = (double)srcH / newHeight;
        var newData = CreateChannelData(channels, newHeight, newWidth);
        var actualMax = float.NegativeInfinity;
        var actualMin = float.PositiveInfinity;

        for (var c = 0; c < channels; c++)
        {
            var src = GetChannelSpan(c);
            var dst = MemoryMarshal.CreateSpan(ref newData[c][0, 0], newWidth * newHeight);
            for (var y = 0; y < newHeight; y++)
            {
                // Clamp the *sample location* before extracting integer + fractional
                // parts -- matches BORDER_REPLICATE semantics. Clamping y0 after
                // computing dy preserves a stale fractional weight at the edge,
                // which produces a non-monotonic dip on linear inputs at x=0.
                var fy = (y + 0.5) * sy - 0.5;
                if (fy < 0) fy = 0;
                else if (fy > srcH - 1) fy = srcH - 1;
                var y0 = (int)Math.Floor(fy);
                var dy = (float)(fy - y0);
                var y1 = y0 + 1; if (y1 > srcH - 1) y1 = srcH - 1;
                for (var x = 0; x < newWidth; x++)
                {
                    var fx = (x + 0.5) * sx - 0.5;
                    if (fx < 0) fx = 0;
                    else if (fx > srcW - 1) fx = srcW - 1;
                    var x0 = (int)Math.Floor(fx);
                    var dx = (float)(fx - x0);
                    var x1 = x0 + 1; if (x1 > srcW - 1) x1 = srcW - 1;

                    var v00 = src[y0 * srcW + x0];
                    var v01 = src[y0 * srcW + x1];
                    var v10 = src[y1 * srcW + x0];
                    var v11 = src[y1 * srcW + x1];

                    float v;
                    if (float.IsNaN(v00) || float.IsNaN(v01) || float.IsNaN(v10) || float.IsNaN(v11))
                    {
                        v = float.NaN;
                    }
                    else
                    {
                        var top = v00 * (1f - dx) + v01 * dx;
                        var bot = v10 * (1f - dx) + v11 * dx;
                        v = top * (1f - dy) + bot * dy;
                        if (v < actualMin) actualMin = v;
                        if (v > actualMax) actualMax = v;
                    }
                    dst[y * newWidth + x] = v;
                }
            }
        }

        if (float.IsNegativeInfinity(actualMax)) actualMax = MaxValue;
        if (float.IsPositiveInfinity(actualMin)) actualMin = MinValue;
        return new Image(newData, BitDepth, actualMax, actualMin, Pedestal, imageMeta);
    }
}
