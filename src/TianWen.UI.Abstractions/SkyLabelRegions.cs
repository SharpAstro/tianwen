using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The part of a sky map label a host's photograph leaves uncovered, as at most four rectangles: the label's click
/// region is where it is seen and nowhere else (#1513).
/// </summary>
public static class SkyLabelRegions
{
    /// <summary>
    /// Writes into <paramref name="into"/> the parts of <paramref name="label"/> outside <paramref name="hole"/>: the
    /// whole label where nothing covers it, nothing where the hole covers all of it, else the bands above and below the
    /// hole and the pieces left and right of it within its rows. Returns how many it wrote.
    /// </summary>
    public static int Outside(RectF32 label, RectF32? hole, Span<RectF32> into)
    {
        if (label.Width <= 0f || label.Height <= 0f)
        {
            return 0;
        }

        var (x0, y0, x1, y1) = (label.X, label.Y, label.X + label.Width, label.Y + label.Height);
        if (hole is not { Width: > 0f, Height: > 0f } h
            || h.X >= x1 || h.X + h.Width <= x0 || h.Y >= y1 || h.Y + h.Height <= y0)
        {
            into[0] = label;
            return 1;
        }

        var (hx0, hy0, hx1, hy1) = (h.X, h.Y, h.X + h.Width, h.Y + h.Height);
        var count = 0;
        if (hy0 > y0)
        {
            into[count++] = new RectF32(x0, y0, label.Width, hy0 - y0);
        }
        if (hy1 < y1)
        {
            into[count++] = new RectF32(x0, hy1, label.Width, y1 - hy1);
        }

        var (bandTop, bandBottom) = (MathF.Max(y0, hy0), MathF.Min(y1, hy1));
        if (hx0 > x0)
        {
            into[count++] = new RectF32(x0, bandTop, hx0 - x0, bandBottom - bandTop);
        }
        if (hx1 < x1)
        {
            into[count++] = new RectF32(hx1, bandTop, x1 - hx1, bandBottom - bandTop);
        }
        return count;
    }
}
