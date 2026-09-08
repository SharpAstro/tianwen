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
    /// <para><b>This is the UNION, and the name says intersection.</b> Absence marks where NO frame
    /// reached, so the answer is the largest rectangle inside the area at least one sub covered -- inside
    /// which a band that fewer subs reached survives, with no zeros in it and up to 60% more noise. That
    /// band is <see cref="CoverageEdgeWalk"/>'s business (an estimate, for a frame someone else
    /// produced), or <see cref="LargestCoveredRectangle(Image, double)"/>'s (exact, whenever a coverage
    /// plane exists). Kept as it is because it is the right question for absence itself, and because it
    /// is the starting rectangle both of those refine.</para>
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

        var anyNonZero = new bool[width];
        var anyNaN = new bool[width];

        return LargestRectangle(width, height, (int y, Span<bool> covered) =>
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
                covered[x] = !anyNaN[x] && anyNonZero[x];
            }
        });
    }

    /// <summary>
    /// The largest axis-aligned rectangle in which every pixel was reached by at least
    /// <paramref name="minFraction"/> of the frames, read off a coverage plane rather than guessed from
    /// the pixels. The exact answer to the question <see cref="LargestCoveredRectangle()"/> only
    /// approximates, and the one that wins wherever a coverage plane exists.
    /// </summary>
    /// <param name="coverage">Per-pixel accumulated weight on the same canvas as this image: TianWen's
    /// drizzle strategies emit exactly this as <c>IntegrationResult.RejectionMap</c> (there is no
    /// kappa-sigma rejection to report, so the field carries the weight), written beside the master as
    /// <c>*.rejection.fits</c>. One channel is broadcast to all; otherwise the channel count must match,
    /// because a Bayer-drizzle canvas covers green twice as often as red and the levels are per
    /// channel.</param>
    /// <param name="minFraction">How much of full coverage a region needs, where full is the median of
    /// the central half of that channel's plane. 0.95 keeps the noise within 1/sqrt(0.95) = 1.026x of the
    /// interior's, which is below anything visible under a stretch.</param>
    /// <param name="blockSize">Side of the block the coverage is averaged over before it is compared, and
    /// therefore the granularity of the answer.</param>
    /// <remarks>
    /// <para><b>The comparison is per BLOCK, not per pixel, and that is not a shortcut.</b> A drizzle
    /// canvas hands neighbouring cells slightly different drop counts, so the per-pixel weight inside a
    /// fully covered interior scatters by about 10% either way: measured on the 10P master, the red
    /// channel's interior p0.1 is 0.847 of its median. A per-pixel threshold at 0.95 therefore rejects
    /// pixels everywhere and the largest rectangle collapses to nothing (207 x 404 of a 4215 x 2884
    /// frame, measured, before this was blocked). Over 16 x 16 blocks the same interior reads p0.1 =
    /// 0.990, because whether a REGION is under-exposed is the actual question -- one cell's drop count
    /// is not.</para>
    /// <para><b>Full coverage is the central MEDIAN, not the maximum.</b> A single cell that took more
    /// drops than its neighbours would otherwise set the level and push the threshold above what the
    /// frame actually achieved, which trims a healthy master.</para>
    /// <para><b>Every channel has to pass.</b> A pixel under-covered in one channel of three is
    /// under-covered: it renders as colour noise, which is worse than luminance noise, and the drizzle
    /// canvas ramps the three channels in at slightly different depths.</para>
    /// <para>Measured on TianWen's own 10P master (4215 x 2884, 135 frames, RGGB drizzle): the coverage
    /// plane puts the under-exposed band at 56 px on the top edge, 56 on the bottom, 20 on the right and
    /// 4 on the left, where the union rectangle of <see cref="LargestCoveredRectangle()"/> keeps all four
    /// -- its top row sits at 8% coverage. The stacking pipeline's own <c>_autocrop</c> is unaffected: it
    /// comes from the geometric intersection of the frame footprints and already lands inside 0.99
    /// coverage on all four edges.</para>
    /// </remarks>
    public Rectangle LargestCoveredRectangle(Image coverage, double minFraction = 0.95, int blockSize = 16)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        var width = Width;
        var height = Height;
        var channels = ChannelCount;
        if (width <= 0 || height <= 0 || channels <= 0)
        {
            return Rectangle.Empty;
        }
        if (coverage.Width != width || coverage.Height != height)
        {
            throw new ArgumentException(
                $"Coverage plane is {coverage.Width}x{coverage.Height}, image is {width}x{height}.", nameof(coverage));
        }
        if (coverage.ChannelCount != 1 && coverage.ChannelCount != channels)
        {
            throw new ArgumentException(
                $"Coverage plane has {coverage.ChannelCount} channels, image has {channels}.", nameof(coverage));
        }

        var block = Math.Max(1, blockSize);
        var planes = coverage.ChannelCount;
        var gridWidth = (width + block - 1) / block;
        var gridHeight = (height + block - 1) / block;
        var grids = new float[planes][];
        var thresholds = new float[planes];
        for (var c = 0; c < planes; c++)
        {
            grids[c] = BlockMeans(coverage, c, block, gridWidth, gridHeight);
            thresholds[c] = (float)(minFraction * CentralMedian(grids[c], gridWidth, gridHeight));
        }

        return LargestRectangle(width, height, (int y, Span<bool> covered) =>
        {
            covered.Fill(true);
            var gridRow = (y / block) * gridWidth;
            for (var c = 0; c < planes; c++)
            {
                var threshold = thresholds[c];
                var grid = grids[c];
                for (var x = 0; x < width; x++)
                {
                    // Negated >= so a NaN block counts as uncovered rather than passing.
                    if (!(grid[gridRow + x / block] >= threshold))
                    {
                        covered[x] = false;
                    }
                }
            }
        });
    }

    /// <summary>
    /// Block means of one plane, NaN for a block holding any non-finite weight -- an unknown weight is
    /// not a full one, and the maximal-rectangle scan then treats the block as uncovered.
    /// </summary>
    private static float[] BlockMeans(Image coverage, int channel, int block, int gridWidth, int gridHeight)
    {
        var width = coverage.Width;
        var height = coverage.Height;
        var plane = coverage.GetChannelSpan(channel);
        var grid = new float[gridWidth * gridHeight];

        for (var gy = 0; gy < gridHeight; gy++)
        {
            var y1 = Math.Min(height, (gy + 1) * block);
            for (var gx = 0; gx < gridWidth; gx++)
            {
                var x1 = Math.Min(width, (gx + 1) * block);
                var sum = 0.0;
                var count = 0;
                var bad = false;
                for (var y = gy * block; y < y1 && !bad; y++)
                {
                    var row = y * width;
                    for (var x = gx * block; x < x1; x++)
                    {
                        var v = plane[row + x];
                        if (!float.IsFinite(v))
                        {
                            bad = true;
                            break;
                        }
                        sum += v;
                        count++;
                    }
                }
                grid[gy * gridWidth + gx] = bad || count == 0 ? float.NaN : (float)(sum / count);
            }
        }

        return grid;
    }

    /// <summary>
    /// <see cref="LargestCoveredRectangle()"/> with each edge's under-exposed band walked off as well --
    /// the best answer available for a frame that carries no coverage plane. See
    /// <see cref="CoverageEdgeWalk"/> for what the walk can and cannot see.
    /// </summary>
    public Rectangle SettledCoverageRectangle(CoverageEdgeWalkOptions? options = null)
    {
        var start = LargestCoveredRectangle();
        return start.Width > 0 && start.Height > 0 ? CoverageEdgeWalk.Trim(this, start, options) : start;
    }

    /// <summary>The median of the central half of a block grid: what "full" means for a coverage map.</summary>
    private static double CentralMedian(float[] grid, int gridWidth, int gridHeight)
    {
        var x0 = gridWidth / 4;
        var x1 = Math.Max(x0 + 1, gridWidth - x0);
        var y0 = gridHeight / 4;
        var y1 = Math.Max(y0 + 1, gridHeight - y0);

        var samples = new float[(x1 - x0) * (y1 - y0)];
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = y * gridWidth;
            for (var x = x0; x < x1; x++)
            {
                var v = grid[row + x];
                if (float.IsFinite(v))
                {
                    samples[count++] = v;
                }
            }
        }
        if (count == 0)
        {
            return 0.0;
        }
        Array.Sort(samples, 0, count);
        return samples[count / 2];
    }

    /// <summary>Fills <paramref name="covered"/> with row <paramref name="y"/>'s per-column verdict.</summary>
    private delegate void CoveredRowProbe(int y, Span<bool> covered);

    /// <summary>
    /// The largest-rectangle-under-a-histogram scan, shared by every definition of "covered" -- the
    /// definition is the caller's and only reaches here as a row of booleans.
    /// </summary>
    private static Rectangle LargestRectangle(int width, int height, CoveredRowProbe probe)
    {
        var heights = new int[width];
        var stack = new int[width];
        var covered = new bool[width];
        var best = Rectangle.Empty;
        var bestArea = 0L;

        for (var y = 0; y < height; y++)
        {
            probe(y, covered);

            for (var x = 0; x < width; x++)
            {
                heights[x] = covered[x] ? heights[x] + 1 : 0;
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
