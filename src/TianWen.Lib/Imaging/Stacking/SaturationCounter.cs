using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Counts, per master pixel, how many lights had that spot saturated at the ADC ceiling (#1991). Registration and
/// rejection smear a star that was clipped in every sub to a peak well under the master's own, so SPCC's peak gate
/// cannot see it; this count can.
///
/// <para><b>One pass of its own, beside the strategies rather than inside each.</b> The five integration strategies
/// calibrate, demosaic and warp in five different shapes (a tile re-read, a staged canvas, a drizzle deposit), and the
/// ceiling has to be judged on the RAW photosite before any of them runs. A pass that reads each raw light once and
/// carries the clipped photosites through the SAME source-to-canvas transform every strategy uses
/// (<see cref="RawLightSource.TransformToCanvas"/>) gives all of them one answer, linear in frames, with nothing for a
/// parallel strip to disagree about. The price is one extra read of each raw light.</para>
///
/// <para><b>Geometry.</b> A clipped photosite lands on the canvas pixel its centre maps to, and the pixel inherits the
/// mark across the demosaic's one-pixel footprint (a debayered pixel is built from its neighbours; a drizzle deposit
/// spreads over its drop), so the mark is that pixel and its eight neighbours. A frame counts once per master pixel
/// however many of its photosites reach it, so a count never exceeds the frame count.</para>
/// </summary>
internal static class SaturationCounter
{
    /// <summary>
    /// The count image on the canvas, or null when no light had a clipped photosite (nothing is then written).
    /// </summary>
    public static Image? Count(IReadOnlyList<RawLightSource> sources, int canvasWidth, int canvasHeight, ILogger logger, CancellationToken ct)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return null;
        }

        int[]? counts = null;
        byte[]? mark = null;
        var clippedFrames = 0;
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            Image? raw;
            try
            {
                if (!Image.TryReadFitsFile(source.Path, out raw))
                {
                    continue;
                }
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "  [saturation] could not read {Path}", source.Path);
                continue;
            }

            try
            {
                // What the RAW frame states, never the observed peak of a calibrated one.
                var ceiling = CeilingOf(raw);
                if (ceiling is not { } limit || raw.ChannelCount != 1)
                {
                    continue;
                }

                mark ??= new byte[canvasWidth * canvasHeight];
                var any = MarkFrame(raw.GetChannelArray(0), limit, source.TransformToCanvas, canvasWidth, canvasHeight, mark);
                if (!any)
                {
                    continue;
                }

                counts ??= new int[canvasWidth * canvasHeight];
                for (var i = 0; i < mark.Length; i++)
                {
                    counts[i] += mark[i];
                }
                Array.Clear(mark);
                clippedFrames++;
            }
            finally
            {
                raw.Release();
            }
        }

        if (counts is null)
        {
            return null;
        }

        var plane = new float[canvasHeight, canvasWidth];
        var maxCount = 1;
        for (var y = 0; y < canvasHeight; y++)
        {
            for (var x = 0; x < canvasWidth; x++)
            {
                var c = counts[(y * canvasWidth) + x];
                plane[y, x] = c;
                if (c > maxCount)
                {
                    maxCount = c;
                }
            }
        }
        logger.LogInformation("  [saturation] {Frames}/{Total} lights have photosites at the ADC ceiling; peak count {Peak}",
            clippedFrames, sources.Count, maxCount);
        return new Image([plane], BitDepth.Float32, maxCount, 0f, 0f, new ImageMeta { SensorType = SensorType.Monochrome });
    }

    /// <summary>A container-derived threshold is this fraction of the container's full scale: N.I.N.A. and SharpCap
    /// left-align a 14-bit ADC into the 16-bit container (clip 65532; 12-bit 65520; 10-bit 65472) and write no
    /// <c>SATURATE</c> card, so the container's own maximum is never reached. 0.999 (65469 of 65535) admits all of them.</summary>
    internal const float ContainerCeilingFraction = 0.999f;

    /// <summary>The level at or above which a photosite counts as clipped, from what the raw frame states: its declared
    /// full scale exactly, else just under its container's (see <see cref="ContainerCeilingFraction"/>).</summary>
    internal static float? CeilingOf(Image raw)
        => raw.ImageMeta.SensorFullScaleAdu is { } declared and > 0
            ? declared
            : raw.BitDepth.UnsignedFullScale is { } container ? container * ContainerCeilingFraction : null;

    /// <summary>Marks, on a canvas-sized plane, the pixels reached by a photosite at or above
    /// <paramref name="ceiling"/>. True when there was any.</summary>
    internal static bool MarkFrame(float[,] plane, float ceiling, Matrix3x2 toCanvas, int canvasWidth, int canvasHeight, byte[] mark)
    {
        var any = false;
        var h = plane.GetLength(0);
        var w = plane.GetLength(1);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (plane[y, x] < ceiling)
                {
                    continue;
                }
                any = true;
                var p = Vector2.Transform(new Vector2(x, y), toCanvas);
                var cx = (int)MathF.Round(p.X);
                var cy = (int)MathF.Round(p.Y);
                for (var dy = -1; dy <= 1; dy++)
                {
                    var yy = cy + dy;
                    if ((uint)yy >= (uint)canvasHeight)
                    {
                        continue;
                    }
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var xx = cx + dx;
                        if ((uint)xx < (uint)canvasWidth)
                        {
                            mark[(yy * canvasWidth) + xx] = 1;
                        }
                    }
                }
            }
        }
        return any;
    }
}
