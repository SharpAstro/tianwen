using System;
using System.Drawing;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// The largest axis-aligned rectangle containing no pixel that no frame covered, i.e. what is left of
    /// a stacked master once its canvas ring is discarded. The whole frame when there is nothing to
    /// discard, and <see cref="Rectangle.Empty"/> for an image with no covered pixel at all.
    /// </summary>
    /// <remarks>
    /// <para><b>A pixel counts as absent when it is exactly zero in EVERY channel, or NaN in any of
    /// them.</b> The two halves come from different producers: an integration leaves the canvas at exact
    /// zero where no frame reached (TianWen and Astro Pixel Processor both do), while a tool that flags
    /// absence explicitly writes NaN. Zero has to hold in every channel, because a zero in one channel of
    /// three is a dead pixel or a genuinely black one; a NaN in any channel makes the pixel unusable
    /// whatever the others say.</para>
    /// <para><b>The largest RECTANGLE, not the bounding box of the covered pixels</b>, and a real file is
    /// what settles that rather than the argument. On a 3073 x 3085 Astro Pixel Processor composite,
    /// 97,589 pixels (1.029%) are exact zero in all three channels and the absent ones <b>touch every
    /// edge</b>: they span rows 0 to 3084 and columns 0 to 3072. The bounding box of the covered pixels is
    /// therefore the entire frame, ring included. The answer here is 2987 x 3061 at (25, 8), 96.45% of the
    /// frame.</para>
    /// <para>One pass of the standard largest-rectangle-under-a-histogram scan: per row, each column's run
    /// of covered pixels is a bar, and a monotonic stack finds the widest rectangle under those bars.
    /// O(width x height) time and O(width) extra space. Channels are read one at a time down the outer
    /// loop, so every read is sequential and nothing the size of the image is allocated.</para>
    /// <para>Not computed at load: this reads every channel of every pixel, which is tens of milliseconds
    /// on a full-frame master, so it belongs behind an action rather than on the path every document
    /// takes.</para>
    /// </remarks>
    public Rectangle LargestCoveredRectangle()
    {
        var width = Width;
        var height = Height;
        var channels = ChannelCount;
        if (width <= 0 || height <= 0 || channels <= 0)
        {
            return Rectangle.Empty;
        }

        var heights = new int[width];
        var stack = new int[width];
        var anyNonZero = new bool[width];
        var anyNaN = new bool[width];
        var best = Rectangle.Empty;
        var bestArea = 0L;

        for (var y = 0; y < height; y++)
        {
            Array.Clear(anyNonZero);
            Array.Clear(anyNaN);

            var rowStart = y * width;
            for (var c = 0; c < channels; c++)
            {
                var plane = GetChannelSpan(c).Slice(rowStart, width);
                for (var x = 0; x < width; x++)
                {
                    var v = plane[x];
                    if (float.IsNaN(v))
                    {
                        anyNaN[x] = true;
                    }
                    else if (v != 0f)
                    {
                        anyNonZero[x] = true;
                    }
                }
            }

            for (var x = 0; x < width; x++)
            {
                heights[x] = anyNaN[x] || !anyNonZero[x] ? 0 : heights[x] + 1;
            }

            // Monotonic stack of indices with strictly increasing heights. The sentinel pass at x == width
            // drains it, so a run reaching the right edge is measured like any other, and the left bound
            // comes from the stack rather than from mutating heights (which has to survive to the next row).
            var top = 0;
            for (var x = 0; x <= width; x++)
            {
                var h = x < width ? heights[x] : 0;
                while (top > 0 && heights[stack[top - 1]] >= h)
                {
                    var barHeight = heights[stack[--top]];
                    var left = top > 0 ? stack[top - 1] + 1 : 0;
                    var barWidth = x - left;
                    var area = (long)barWidth * barHeight;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = new Rectangle(left, y - barHeight + 1, barWidth, barHeight);
                    }
                }

                if (x < width)
                {
                    stack[top++] = x;
                }
            }
        }

        return best;
    }
}
