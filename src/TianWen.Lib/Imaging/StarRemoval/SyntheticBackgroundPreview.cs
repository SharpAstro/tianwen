using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// <c>tianwen dataset synthetic-background</c>: R2d's generator (<see cref="SyntheticBackground"/>) run over ANY starless
/// plate, a cell centred on each point asked for, written beside the plate's own cutout there, so the synthetic nebulosity
/// can be judged against the real sky it was drawn from (a third-party starless of a field the store does not hold, such
/// as the Pleiades).
/// </summary>
public static class SyntheticBackgroundPreview
{
    /// <summary>One centre's pair: the plate's cutout and the synthetic cell, both FITS on the plate's scale.</summary>
    public readonly record struct Written(int X, int Y, string PlatePath, string SyntheticPath);

    /// <summary>
    /// Builds the generator over <paramref name="platePath"/> (on its master's unit scale) at a PSF of
    /// <paramref name="fwhm"/> px and writes, per centre, <c>plate_&lt;x&gt;_&lt;y&gt;.fits</c> and
    /// <c>synthetic_&lt;x&gt;_&lt;y&gt;.fits</c> into <paramref name="outDir"/>; no centre means the frame's middle. The
    /// synthetic cell carries white noise at the plate's own (<see cref="SyntheticBackground.PlateNoise"/>) unless
    /// <paramref name="noisy"/> is false; the noise read is returned per channel.
    /// </summary>
    public static (ImmutableArray<Written> Written, double[] Noise) Run(
        string platePath, string outDir, double fwhm, IReadOnlyList<(int X, int Y)> centres, int size, int seed, bool noisy)
    {
        if (!Image.TryReadFitsFile(platePath, out var plate))
        {
            throw new InvalidDataException($"not a readable FITS image: {platePath}");
        }
        var absent = plate.AbsentPixels();
        var noise = SyntheticBackground.PlateNoise(plate, absent);
        var background = SyntheticBackground.Build(plate, absent, fwhm);
        Directory.CreateDirectory(outDir);

        var points = centres.Count > 0 ? centres : [(plate.Width / 2, plate.Height / 2)];
        var written = ImmutableArray.CreateBuilder<Written>(points.Count);
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
            written.Add(new Written(cx, cy, platePathOut, syntheticPathOut));
        }
        return (written.MoveToImmutable(), noise);
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
