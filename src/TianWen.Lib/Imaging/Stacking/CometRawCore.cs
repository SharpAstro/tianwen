using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// A small comet-aligned median stack of the RAW frames' central pixels: the one place the body's
/// nucleus survives, because no star remover ever ran there.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> The comet model is a crop of a comet layer stacked from star-removed
/// plates, and a star remover takes a comet's central condensation along with the stars: it is
/// compact and star-like. So the model is short of exactly that flux while every frame still has it,
/// and whatever the amplitude fit does about it, the difference ends up in the star layer. With the
/// amplitude read off an annulus (correctly), the condensation stays in every frame and integrates
/// into a thin line along the track: on 10P/Tempel 2, +2 to +3.5 sigma over |perp| &lt; 4 px along the
/// 45 px of travel. The core has to come from frames the remover never touched.</para>
///
/// <para><b>Why a median over frames is enough here.</b> On the comet grid the body is fixed and every
/// star trails through. A star occupies a given cell for only the frames in which its trail crosses
/// it (a few pixels of trail width against tens of pixels of travel), so per cell it is a minority of
/// the samples and the median never sees it. The window is deliberately small, tens of pixels, so the
/// stack is cheap, but the frames still have to be read and calibrated once each.</para>
///
/// <para><b>Units.</b> The frames are calibrated ADU, unnormalised, and the model is in the comet
/// layer's normalised pixels; the two meet in <see cref="CometModel.SpliceCore"/> through a gain and
/// an offset fitted where both are trusted. Nothing here has to know either scale.</para>
///
/// <para><b>The deposit is forward, per photosite, into the nearest cell</b>, the drizzle idea with a
/// unit drop: a CFA mosaic cannot be sampled bilinearly in one colour, and with dozens of dithered
/// frames every cell of every colour fills. The grid is built so that the body sits exactly on the
/// centre cell, whatever its sub-pixel position on the comet grid, which is what lets the splice line
/// the two up by offset alone.</para>
/// </remarks>
internal static class CometRawCore
{
    /// <summary>Half-size of the window, in comet-grid pixels: several times the nucleus, enough
    /// annulus beyond it for the gain fit to have something to stand on.</summary>
    public const int DefaultRadiusPx = 40;

    /// <summary>A cell answers a value only with at least this many samples behind its median. A colour
    /// lands on a given cell in only a fraction of the dithered frames (a quarter, for R or B on
    /// RGGB), so this is deliberately low; a cell still short of it is filled from its neighbours.</summary>
    private const int MinSamplesPerCell = 3;

    /// <summary>
    /// Stacks the window around the body from every frame. Returns planes of size
    /// <c>2 * radiusPx + 1</c> with the body at <c>(radiusPx, radiusPx)</c>, NaN where a cell had
    /// too few samples; or null when nothing could be stacked.
    /// </summary>
    /// <param name="frames">Each matched frame's RAW light and its STAR solution (source pixels onto the
    /// reference star grid).</param>
    /// <param name="ratePxPerHour">The body's canvas rate on that grid.</param>
    /// <param name="reference">The reference frame's meta, for the drift epochs.</param>
    /// <param name="bodyOnGrid">Where the body sits on the comet-aligned reference grid.</param>
    /// <param name="channels">Planes to produce; CFA colours beyond this are folded into the last.</param>
    /// <param name="calibrator">The real calibrator for RAW lights (never the starless no-op).</param>
    public static async Task<float[][,]?> StackAsync(
        IReadOnlyList<(FrameInfo Light, Matrix3x2 StarTransform)> frames,
        Vector2 ratePxPerHour,
        ImageMeta reference,
        Vector2 bodyOnGrid,
        int channels,
        int radiusPx,
        Calibrator calibrator,
        ILogger logger,
        CancellationToken ct)
    {
        if (frames.Count == 0 || channels <= 0 || radiusPx < 4)
        {
            return null;
        }
        var size = 2 * radiusPx + 1;
        var samples = new List<float>[channels][,];
        for (var c = 0; c < channels; c++)
        {
            samples[c] = new List<float>[size, size];
        }

        var used = 0;
        foreach (var (light, starTransform) in frames)
        {
            ct.ThrowIfCancellationRequested();
            var toCometGrid = CometCompose.ToCometGrid(
                starTransform, ratePxPerHour, CometCompose.DriftHours(light.Meta, reference));
            if (!Matrix3x2.Invert(toCometGrid, out var gridToSource))
            {
                continue;
            }
            var raw = await light.LoadFullAsync(ct);
            var calibrated = calibrator.Apply(raw);
            try
            {
                if (calibrated.ChannelCount != 1)
                {
                    // A colour-decoded or 3-plane frame has no photosite pattern to deposit by.
                    continue;
                }
                var pattern = calibrated.ImageMeta.SensorType.GetBayerPatternMatrix(
                    calibrated.ImageMeta.BayerOffsetX, calibrated.ImageMeta.BayerOffsetY);
                var plane = calibrated.GetChannelArray(0);

                // Source AABB of the window: the four corners pushed back, since the affine may rotate.
                var minX = float.MaxValue;
                var minY = float.MaxValue;
                var maxX = float.MinValue;
                var maxY = float.MinValue;
                foreach (var (ox, oy) in new[] { (-radiusPx, -radiusPx), (radiusPx, -radiusPx), (-radiusPx, radiusPx), (radiusPx, radiusPx) })
                {
                    var p = Vector2.Transform(bodyOnGrid + new Vector2(ox, oy), gridToSource);
                    minX = MathF.Min(minX, p.X);
                    minY = MathF.Min(minY, p.Y);
                    maxX = MathF.Max(maxX, p.X);
                    maxY = MathF.Max(maxY, p.Y);
                }
                var x0 = Math.Max(0, (int)MathF.Floor(minX) - 1);
                var y0 = Math.Max(0, (int)MathF.Floor(minY) - 1);
                var x1 = Math.Min(calibrated.Width - 1, (int)MathF.Ceiling(maxX) + 1);
                var y1 = Math.Min(calibrated.Height - 1, (int)MathF.Ceiling(maxY) + 1);
                if (x1 <= x0 || y1 <= y0)
                {
                    continue;
                }
                var deposited = 0;
                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        var v = plane[y, x];
                        if (!float.IsFinite(v))
                        {
                            continue;
                        }
                        var g = Vector2.Transform(new Vector2(x, y), toCometGrid) - bodyOnGrid;
                        var gx = (int)MathF.Round(g.X) + radiusPx;
                        var gy = (int)MathF.Round(g.Y) + radiusPx;
                        if ((uint)gx >= (uint)size || (uint)gy >= (uint)size)
                        {
                            continue;
                        }
                        var c = Math.Min(pattern[y & 1, x & 1], channels - 1);
                        (samples[c][gy, gx] ??= new List<float>(frames.Count)).Add(v);
                        deposited++;
                    }
                }
                if (deposited > 0)
                {
                    used++;
                }
            }
            finally
            {
                calibrated.Release();
            }
        }

        if (used == 0)
        {
            logger.LogWarning("  [comet] raw core: the body's window landed on no frame, so the nucleus cannot be restored");
            return null;
        }

        var planes = new float[channels][,];
        var filled = 0;
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var list = samples[c][y, x];
                    if (list is null || list.Count < MinSamplesPerCell)
                    {
                        plane[y, x] = float.NaN;
                        continue;
                    }
                    list.Sort();
                    plane[y, x] = list[list.Count / 2];
                    filled++;
                }
            }
            planes[c] = plane;
        }

        // A cell too thin to answer takes the median of its finite 3x3 neighbours, once. The splice
        // reads the core bilinearly and a single NaN would void the four cells around it, leaving the
        // model's own (nucleus-less) value in a hole that is then subtracted from every frame.
        var neighbourFilled = 0;
        foreach (var plane in planes)
        {
            var copy = (float[,])plane.Clone();
            var around = new List<float>(9);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (float.IsFinite(copy[y, x]))
                    {
                        continue;
                    }
                    around.Clear();
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var yy = y + dy;
                            var xx = x + dx;
                            if ((uint)yy < (uint)size && (uint)xx < (uint)size && float.IsFinite(copy[yy, xx]))
                            {
                                around.Add(copy[yy, xx]);
                            }
                        }
                    }
                    if (around.Count >= 3)
                    {
                        around.Sort();
                        plane[y, x] = around[around.Count / 2];
                        neighbourFilled++;
                    }
                }
            }
        }
        logger.LogInformation(
            "  [comet] raw core: {Size}x{Size} px median stack of {Frames} frames around the body, {Filled}/{Cells} cells from their own samples, {Neighbours} from neighbours",
            size, size, used, filled, size * size * channels, neighbourFilled);
        return planes;
    }
}
