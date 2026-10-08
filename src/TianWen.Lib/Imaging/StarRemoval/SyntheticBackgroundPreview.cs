using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// <c>tianwen dataset synthetic-background</c>: R2d's generator (<see cref="SyntheticBackground"/>) run over ANY starless
/// plate, a cell centred on each point asked for, written beside the plate's own cutout there, so the synthetic nebulosity
/// can be judged against the real sky it was drawn from (a third-party starless of a field the store does not hold, such
/// as the Pleiades). With a measure, R2e's S3: both cells read by S1's measure (<see cref="SkyTexture.Measure"/>), so the
/// synthetic's structure is compared with the plate's where it was drawn.
/// </summary>
public static class SyntheticBackgroundPreview
{
    /// <summary>One centre's pair: the plate's cutout and the synthetic cell, both FITS on the plate's scale, and when measured,
    /// each one's luminance read by S1's measure.</summary>
    public sealed record Written(int X, int Y, string PlatePath, string SyntheticPath,
        SkyTexture.Measurement? Plate = null, SkyTexture.Measurement? Synthetic = null);

    /// <summary>
    /// Builds the generator over <paramref name="platePath"/> (on its master's unit scale) at a PSF of
    /// <paramref name="fwhm"/> px, steered by <paramref name="steering"/> when one is given, and writes, per centre,
    /// <c>plate_&lt;x&gt;_&lt;y&gt;.fits</c> and <c>synthetic_&lt;x&gt;_&lt;y&gt;.fits</c> into <paramref name="outDir"/>.
    /// The centres are <paramref name="centres"/>, then the <paramref name="textured"/> cells of the frame's grid that hold
    /// the most texture to draw (<see cref="MostTextured"/>); none at all means the frame's middle. The synthetic cell
    /// carries white noise at the plate's own (<see cref="SyntheticBackground.PlateNoise"/>) unless <paramref name="noisy"/>
    /// is false; the noise read is returned per channel. With <paramref name="measure"/> both cells' luminance is read by
    /// S1's measure and every pair goes into <c>structure.jsonl</c> beside them.
    /// </summary>
    public static (ImmutableArray<Written> Written, double[] Noise) Run(
        string platePath, string outDir, double fwhm, IReadOnlyList<(int X, int Y)> centres, int size, int seed, bool noisy,
        SyntheticBackground.Steering? steering = null, int textured = 0, bool measure = false)
    {
        if (!Image.TryReadFitsFile(platePath, out var plate))
        {
            throw new InvalidDataException($"not a readable FITS image: {platePath}");
        }
        var absent = plate.AbsentPixels();
        var noise = SyntheticBackground.PlateNoise(plate, absent);
        var background = SyntheticBackground.Build(plate, absent, fwhm, steering: steering);
        Directory.CreateDirectory(outDir);

        var points = new List<(int X, int Y)>(centres);
        points.AddRange(MostTextured(background, absent, size, textured));
        if (points.Count == 0)
        {
            points.Add((plate.Width / 2, plate.Height / 2));
        }
        var written = ImmutableArray.CreateBuilder<Written>(points.Count);
        var report = measure ? Path.Combine(outDir, "structure.jsonl") : null;
        if (report is not null)
        {
            File.Delete(report);
        }
        for (var k = 0; k < points.Count; k++)
        {
            var (cx, cy) = points[k];
            var synthetic = background.Preview(cx, cy, size, noise, noisy, new Random(HashCode.Combine(seed, cx, cy)));
            var plateCut = new float[plate.ChannelCount][];
            for (var c = 0; c < plate.ChannelCount; c++)
            {
                var source = plate.GetChannelSpan(c);
                var cut = new float[size * size];
                for (var y = 0; y < size; y++)
                {
                    var fy = Math.Clamp(cy - (size / 2) + y, 0, plate.Height - 1);
                    for (var x = 0; x < size; x++)
                    {
                        cut[(y * size) + x] = source[(fy * plate.Width) + Math.Clamp(cx - (size / 2) + x, 0, plate.Width - 1)];
                    }
                }
                plateCut[c] = cut;
            }
            var platePathOut = Path.Combine(outDir, $"plate_{cx}_{cy}.fits");
            var syntheticPathOut = Path.Combine(outDir, $"synthetic_{cx}_{cy}.fits");
            Write(plateCut, size, plate, platePathOut);
            Write(synthetic, size, plate, syntheticPathOut);
            var row = new Written(cx, cy, platePathOut, syntheticPathOut);
            if (report is not null)
            {
                row = row with
                {
                    Plate = SkyTexture.Measure(Luminance(plateCut), size, size, absent: null, fwhm),
                    Synthetic = SkyTexture.Measure(Luminance(synthetic), size, size, absent: null, fwhm),
                };
                File.AppendAllText(report, JsonSerializer.Serialize(row, SyntheticBackgroundPreviewJsonContext.Default.Written) + "\n");
            }
            written.Add(row);
        }
        return (written.MoveToImmutable(), noise);
    }

    /// <summary>
    /// The centres of the <paramref name="count"/> cells of a <paramref name="size"/> px grid over the frame that hold the most
    /// texture to draw (the generator's amplitude over every replaced scale and channel, read every 4 px), none touching the
    /// canvas ring <paramref name="absent"/>: where a synthetic texture differs most from noise, and so where S3 reads it.
    /// </summary>
    public static IEnumerable<(int X, int Y)> MostTextured(SyntheticBackground background, BitMatrix? absent, int size, int count)
    {
        if (count <= 0)
        {
            return [];
        }
        var cells = new List<(double Amplitude, int X, int Y)>();
        for (var y0 = 0; y0 + size <= background.Height; y0 += size)
        {
            for (var x0 = 0; x0 + size <= background.Width; x0 += size)
            {
                var sum = 0.0;
                var touches = false;
                for (var y = y0; y < y0 + size && !touches; y += 4)
                {
                    for (var x = x0; x < x0 + size; x += 4)
                    {
                        if (absent is { } a && a[y, x])
                        {
                            touches = true;
                            break;
                        }
                        for (var c = 0; c < background.Channels; c++)
                        {
                            for (var j = 0; j < background.FirstKept; j++)
                            {
                                sum += background.Amplitude(c, j, x, y);
                            }
                        }
                    }
                }
                if (!touches)
                {
                    cells.Add((sum, x0 + (size / 2), y0 + (size / 2)));
                }
            }
        }
        return [.. cells.OrderByDescending(static c => c.Amplitude).Take(count).Select(static c => (c.X, c.Y))];
    }

    private static float[] Luminance(float[][] planes)
    {
        var luminance = new float[planes[0].Length];
        foreach (var plane in planes)
        {
            for (var i = 0; i < luminance.Length; i++)
            {
                luminance[i] += plane[i] / planes.Length;
            }
        }
        return luminance;
    }

    private static void Write(float[][] planes, int size, Image like, string path)
    {
        var data = new float[planes.Length][,];
        for (var c = 0; c < planes.Length; c++)
        {
            var plane = new float[size, size];
            Buffer.BlockCopy(planes[c], 0, plane, 0, size * size * sizeof(float));
            data[c] = plane;
        }
        new Image(data, BitDepth.Float32, 1f, 0f, like.Pedestal, like.ImageMeta).WriteToFitsFile(path);
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(SyntheticBackgroundPreview.Written))]
internal sealed partial class SyntheticBackgroundPreviewJsonContext : JsonSerializerContext
{
}
