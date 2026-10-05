using System;
using System.Collections.Generic;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// The smooth part of each frame's departure from the others: a sky that drifts through the night, a gradient that turns,
/// a transparency that scales the nebula. A pixel's scatter over the frames holds all of it, and none of it is the noise
/// a master's standard error is for: interleaved halves average the same drift, so it cancels from the pairs it predicts
/// (E16c S4's pilot, 2026-10-05: an unnormalised integration over-read the pairs 12 to 25 percent, the sky alone
/// drifting 0.7 to 2.1 times one sub's noise; the drizzle, which shifts each frame's whole sky, still 5 to 12 percent).
/// </summary>
/// <remarks>
/// <para><b>What it measures.</b> Each frame's median per <see cref="BlockPx"/> block and channel, taken while the warped
/// frame is still in memory, and per block the median of those over the frames as the reference. A frame's drift is its
/// block median less the reference, interpolated bilinearly between block centres. A block median reads the scene the
/// frames share and cancels it in the difference; its own noise is a 32nd of a frame's.</para>
/// <para><b>It reaches the standard error and nothing else.</b> The master is the frames as the strategy combines them;
/// only the scatter its error is read from is taken about each frame's drift (<see cref="StandardErrorPlane"/>).</para>
/// <para>A block under a quarter covered reads NaN, and a frame's drift there is zero: no correction, rather than one
/// from the canvas's edge.</para>
/// <para><b>A drizzle never warps a frame</b>, so its blocks are taken on the raw mosaic per colour, in the frame's own
/// coordinates (<see cref="MeasureCfaBlocks"/>), and read at the canvas grid's block centres through the frame's
/// transform (<see cref="ToCanvas"/>); each deposit then takes its frame's drift where it lands (<see cref="At"/>).</para>
/// </remarks>
internal sealed class FrameDrift
{
    /// <summary>The block side, the shipped noise estimator's too (<c>StretchedNoise.EstimateBlockPx</c>).</summary>
    internal const int BlockPx = 32;

    private readonly float[][][] _drift; // [frame][channel][gy * gridWidth + gx]
    private readonly int _gridWidth;
    private readonly int _gridHeight;

    private FrameDrift(float[][][] drift, int gridWidth, int gridHeight)
    {
        _drift = drift;
        _gridWidth = gridWidth;
        _gridHeight = gridHeight;
    }

    internal int GridWidth => _gridWidth;

    /// <summary>The grid for a frame <paramref name="width"/> by <paramref name="height"/>.</summary>
    internal static (int Width, int Height) GridOf(int width, int height)
        => ((width + BlockPx - 1) / BlockPx, (height + BlockPx - 1) / BlockPx);

    /// <summary>One frame's block medians per channel, <c>[channel][gy * gridWidth + gx]</c>; NaN where a block is under a
    /// quarter finite.</summary>
    internal static float[][] MeasureBlocks(Image frame)
    {
        var (channels, width, height) = frame.Shape;
        var (gw, gh) = GridOf(width, height);
        var blocks = new float[channels][];
        var buffer = new float[BlockPx * BlockPx];
        for (var c = 0; c < channels; c++)
        {
            var plane = frame.GetChannelArray(c);
            var grid = new float[gw * gh];
            for (var gy = 0; gy < gh; gy++)
            {
                var y0 = gy * BlockPx;
                var y1 = Math.Min(height, y0 + BlockPx);
                for (var gx = 0; gx < gw; gx++)
                {
                    var x0 = gx * BlockPx;
                    var x1 = Math.Min(width, x0 + BlockPx);
                    var n = 0;
                    for (var y = y0; y < y1; y++)
                    {
                        for (var x = x0; x < x1; x++)
                        {
                            var v = plane[y, x];
                            if (!float.IsNaN(v))
                            {
                                buffer[n++] = v;
                            }
                        }
                    }
                    grid[(gy * gw) + gx] = 4 * n >= (y1 - y0) * (x1 - x0) && n > 0
                        ? StatisticsHelper.MedianFast(buffer.AsSpan(0, n))
                        : float.NaN;
                }
            }
            blocks[c] = grid;
        }
        return blocks;
    }

    /// <summary>One raw mosaic's block medians per colour of <paramref name="pattern"/> (0 red, 1 green, 2 blue), in its
    /// own coordinates: <c>[colour][gy * gridWidth + gx]</c>, NaN where a block holds under a quarter of its colour's
    /// photosites finite.</summary>
    internal static float[][] MeasureCfaBlocks(Image raw, int[,] pattern)
    {
        var width = raw.Width;
        var height = raw.Height;
        var (gw, gh) = GridOf(width, height);
        var plane = raw.GetChannelArray(0);
        var blocks = new float[3][];
        var buffer = new float[BlockPx * BlockPx];
        for (var colour = 0; colour < 3; colour++)
        {
            var grid = new float[gw * gh];
            for (var gy = 0; gy < gh; gy++)
            {
                var y0 = gy * BlockPx;
                var y1 = Math.Min(height, y0 + BlockPx);
                for (var gx = 0; gx < gw; gx++)
                {
                    var x0 = gx * BlockPx;
                    var x1 = Math.Min(width, x0 + BlockPx);
                    var n = 0;
                    var sites = 0;
                    for (var y = y0; y < y1; y++)
                    {
                        for (var x = x0; x < x1; x++)
                        {
                            if (pattern[y & 1, x & 1] != colour)
                            {
                                continue;
                            }
                            sites++;
                            var v = plane[y, x];
                            if (!float.IsNaN(v))
                            {
                                buffer[n++] = v;
                            }
                        }
                    }
                    grid[(gy * gw) + gx] = n > 0 && 4 * n >= sites
                        ? StatisticsHelper.MedianFast(buffer.AsSpan(0, n))
                        : float.NaN;
                }
            }
            blocks[colour] = grid;
        }
        return blocks;
    }

    /// <summary>
    /// A frame's own-coordinate blocks (<see cref="MeasureCfaBlocks"/>) read at the canvas grid's block centres: each
    /// centre taken back through <paramref name="toCanvas"/> and the source grid interpolated there. NaN where the centre
    /// falls outside the frame or between blocks that read NaN.
    /// </summary>
    internal static float[][] ToCanvas(float[][] sourceBlocks, int sourceWidth, int sourceHeight, Matrix3x2 toCanvas, int canvasWidth, int canvasHeight)
    {
        if (!Matrix3x2.Invert(toCanvas, out var toSource))
        {
            throw new ArgumentException("the frame's transform to the canvas has no inverse", nameof(toCanvas));
        }
        var (sgw, sgh) = GridOf(sourceWidth, sourceHeight);
        var (cgw, cgh) = GridOf(canvasWidth, canvasHeight);
        var result = new float[sourceBlocks.Length][];
        for (var c = 0; c < sourceBlocks.Length; c++)
        {
            var source = sourceBlocks[c];
            var grid = new float[cgw * cgh];
            for (var gy = 0; gy < cgh; gy++)
            {
                for (var gx = 0; gx < cgw; gx++)
                {
                    var centre = Vector2.Transform(new Vector2((gx + 0.5f) * BlockPx, (gy + 0.5f) * BlockPx), toSource);
                    grid[(gy * cgw) + gx] = centre.X < 0 || centre.Y < 0 || centre.X >= sourceWidth || centre.Y >= sourceHeight
                        ? float.NaN
                        : Bilinear(source, sgw, sgh, centre.X, centre.Y);
                }
            }
            result[c] = grid;
        }
        return result;
    }

    /// <summary>
    /// Every frame's drift from its block medians, in the units the integrator combines in: each frame's blocks go through
    /// <paramref name="toCombined"/> (its normalisation, where the strategy normalises) before the reference is taken.
    /// Null when a frame has no blocks, so a strategy that measured none reads the scatter as before.
    /// </summary>
    internal static FrameDrift? From(
        IReadOnlyList<float[][]?> blocks, int width, int height, Func<int, int, float, float>? toCombined = null)
    {
        if (blocks.Count == 0)
        {
            return null;
        }
        var measured = new float[blocks.Count][][];
        for (var f = 0; f < measured.Length; f++)
        {
            if (blocks[f] is not { } frameBlocks)
            {
                return null;
            }
            measured[f] = frameBlocks;
        }
        var (gw, gh) = GridOf(width, height);
        var frames = measured.Length;
        var channels = measured[0].Length;
        var cells = gw * gh;
        var drift = new float[frames][][];
        for (var f = 0; f < frames; f++)
        {
            drift[f] = new float[channels][];
            for (var c = 0; c < channels; c++)
            {
                var source = measured[f][c];
                if (source.Length != cells)
                {
                    throw new ArgumentException($"frame {f} has {source.Length} blocks in channel {c}, the grid {cells}", nameof(blocks));
                }
                var grid = new float[cells];
                for (var i = 0; i < cells; i++)
                {
                    grid[i] = toCombined is null || float.IsNaN(source[i]) ? source[i] : toCombined(f, c, source[i]);
                }
                drift[f][c] = grid;
            }
        }

        var column = new float[frames];
        for (var c = 0; c < channels; c++)
        {
            for (var i = 0; i < cells; i++)
            {
                var n = 0;
                for (var f = 0; f < frames; f++)
                {
                    var v = drift[f][c][i];
                    if (!float.IsNaN(v))
                    {
                        column[n++] = v;
                    }
                }
                var reference = n > 0 ? StatisticsHelper.MedianFast(column.AsSpan(0, n)) : float.NaN;
                for (var f = 0; f < frames; f++)
                {
                    var v = drift[f][c][i];
                    drift[f][c][i] = float.IsNaN(v) || float.IsNaN(reference) ? 0f : v - reference;
                }
            }
        }
        return new FrameDrift(drift, gw, gh);
    }

    /// <summary>
    /// Every frame's drift along row <paramref name="y"/> at the block columns, interpolated between block centres in y:
    /// <paramref name="row"/> is <c>[frame * GridWidth + gx]</c>. <see cref="Remove"/> then interpolates in x.
    /// </summary>
    internal void Row(int channel, int y, Span<float> row)
    {
        var (g0, g1, t) = Lerp(y, _gridHeight);
        for (var f = 0; f < _drift.Length; f++)
        {
            var grid = _drift[f][channel];
            var a = g0 * _gridWidth;
            var b = g1 * _gridWidth;
            var dst = row.Slice(f * _gridWidth, _gridWidth);
            for (var gx = 0; gx < _gridWidth; gx++)
            {
                dst[gx] = grid[a + gx] + (t * (grid[b + gx] - grid[a + gx]));
            }
        }
    }

    /// <summary>Frame <paramref name="frame"/>'s drift at canvas pixel (<paramref name="x"/>, <paramref name="y"/>), for a
    /// deposit, which lands one pixel at a time and in no row order.</summary>
    internal float At(int frame, int channel, int x, int y)
    {
        var (gy0, gy1, ty) = Lerp(y, _gridHeight);
        var (gx0, gx1, tx) = Lerp(x, _gridWidth);
        var grid = _drift[frame][channel];
        var top = grid[(gy0 * _gridWidth) + gx0] + (tx * (grid[(gy0 * _gridWidth) + gx1] - grid[(gy0 * _gridWidth) + gx0]));
        var bottom = grid[(gy1 * _gridWidth) + gx0] + (tx * (grid[(gy1 * _gridWidth) + gx1] - grid[(gy1 * _gridWidth) + gx0]));
        return top + (ty * (bottom - top));
    }

    /// <summary>The mean of <paramref name="frames"/>' drift grids per channel: the drift a target's frames average, which
    /// is linear in the grid, so <see cref="AtGrid"/> reads it at a pixel as the mean of the frames' drifts there.</summary>
    internal float[][] MeanGrid(IReadOnlyList<int> frames)
    {
        var channels = _drift[0].Length;
        var mean = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            var sum = new double[_gridWidth * _gridHeight];
            foreach (var f in frames)
            {
                var grid = _drift[f][c];
                for (var i = 0; i < sum.Length; i++)
                {
                    sum[i] += grid[i];
                }
            }
            var plane = new float[sum.Length];
            for (var i = 0; i < sum.Length; i++)
            {
                plane[i] = frames.Count > 0 ? (float)(sum[i] / frames.Count) : 0f;
            }
            mean[c] = plane;
        }
        return mean;
    }

    /// <summary>A grid on this drift's blocks (<see cref="MeanGrid"/>) read at canvas pixel (<paramref name="x"/>,
    /// <paramref name="y"/>).</summary>
    internal float AtGrid(float[][] grid, int channel, int x, int y)
    {
        var (gy0, gy1, ty) = Lerp(y, _gridHeight);
        var (gx0, gx1, tx) = Lerp(x, _gridWidth);
        var g = grid[channel];
        var top = g[(gy0 * _gridWidth) + gx0] + (tx * (g[(gy0 * _gridWidth) + gx1] - g[(gy0 * _gridWidth) + gx0]));
        var bottom = g[(gy1 * _gridWidth) + gx0] + (tx * (g[(gy1 * _gridWidth) + gx1] - g[(gy1 * _gridWidth) + gx0]));
        return top + (ty * (bottom - top));
    }

    /// <summary>The scatter's samples about each frame's drift at (<paramref name="x"/>, the row's y), NaN kept.</summary>
    internal void Remove(ReadOnlySpan<float> column, ReadOnlySpan<float> row, int x, Span<float> adjusted)
    {
        var (g0, g1, t) = Lerp(x, _gridWidth);
        for (var f = 0; f < column.Length; f++)
        {
            var r = row.Slice(f * _gridWidth, _gridWidth);
            adjusted[f] = column[f] - (r[g0] + (t * (r[g1] - r[g0])));
        }
    }

    // A grid read at a point in pixels; NaN if any of the four blocks around it is.
    private static float Bilinear(float[] grid, int gw, int gh, float x, float y)
    {
        var (gx0, gx1, tx) = Lerp(x, gw);
        var (gy0, gy1, ty) = Lerp(y, gh);
        var top = grid[(gy0 * gw) + gx0] + (tx * (grid[(gy0 * gw) + gx1] - grid[(gy0 * gw) + gx0]));
        var bottom = grid[(gy1 * gw) + gx0] + (tx * (grid[(gy1 * gw) + gx1] - grid[(gy1 * gw) + gx0]));
        return top + (ty * (bottom - top));
    }

    private static (int G0, int G1, float T) Lerp(int pixel, int cells) => Lerp(pixel + 0.5f, cells);

    // Block centres sit at (g + 0.5) * BlockPx; outside the first and last centre the drift is held, not extrapolated.
    // The position is in pixel EDGE coordinates (a pixel's centre is at its index plus a half).
    private static (int G0, int G1, float T) Lerp(float position, int cells)
    {
        var u = (position / BlockPx) - 0.5f;
        if (u <= 0f || cells == 1)
        {
            return (0, 0, 0f);
        }
        var g0 = (int)u;
        if (g0 >= cells - 1)
        {
            return (cells - 1, cells - 1, 0f);
        }
        return (g0, g0 + 1, u - g0);
    }
}
