using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A colour master sharpened on its luminance alone (#1295, <see cref="PlanetarySharpenOptions.LuminanceOnly"/>): every plane is rebuilt from the
/// sharpened luminance with the stack's own colour at each pixel, so a thin structure's colour is the stack's, never one each channel's own
/// sharpening made, and every scale of the detail is the luminance's.
/// </summary>
public class PlanetaryLuminanceSharpeningTests
{
    private const int Size = 64;
    private static readonly MetricDisk Disk = new(32, 32, 16);

    // A disk on a sky with a pedestal per plane: a yellow globe and, across it, a grey band, the colours a ring and a globe have.
    private static Image Stack()
    {
        double[] sky = [0.02, 0.03, 0.025];
        double[] globe = [0.9, 0.75, 0.45];
        double[] band = [0.6, 0.6, 0.58];
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var inside = Math.Clamp(16.5 - Math.Sqrt(((x - 32) * (x - 32)) + ((y - 32) * (y - 32))), 0, 1);
                    var level = Math.Abs(y - 32) < 3 ? band[c] : globe[c];
                    planes[c][y, x] = (float)(sky[c] + (inside * level));
                }
            }
        }
        return new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }

    [Fact]
    public void EveryPlaneTakesTheSharpenedLuminanceAndTheStacksColour()
    {
        var stack = Stack();
        // A "sharpened" luminance: the planes' mean with a ripple across the disk, as sharpening adds detail at every scale.
        var mean = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var m = (stack.GetChannelSpan(0)[(y * Size) + x] + stack.GetChannelSpan(1)[(y * Size) + x] + stack.GetChannelSpan(2)[(y * Size) + x]) / 3;
                mean[y, x] = Disk.RadiiAt(x, y) < 1 ? m * (1 + (0.2f * MathF.Sin(x * 0.7f))) : m;
            }
        }
        var luminance = Image.FromChannel(mean, 1f, 0f);

        var rebuilt = PlanetarySharpening.WithStackColour(stack, luminance, Disk);

        double[] sky = [0.02, 0.03, 0.025];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (Disk.RadiiAt(x, y) > 0.9)
                {
                    continue;
                }
                var i = (y * Size) + x;
                // The stack's colour above each plane's sky is kept: red over green and blue over green as stacked.
                var (r, g, b) = (stack.GetChannelSpan(0)[i] - sky[0], stack.GetChannelSpan(1)[i] - sky[1], stack.GetChannelSpan(2)[i] - sky[2]);
                var (rr, rg, rb) = (rebuilt.GetChannelSpan(0)[i] - sky[0], rebuilt.GetChannelSpan(1)[i] - sky[1], rebuilt.GetChannelSpan(2)[i] - sky[2]);
                (rr / rg).ShouldBe(r / g, 0.02, $"red over green at ({x}, {y})");
                (rb / rg).ShouldBe(b / g, 0.02, $"blue over green at ({x}, {y})");
                // And the planes' mean is the sharpened luminance, the ripple included.
                ((rebuilt.GetChannelSpan(0)[i] + rebuilt.GetChannelSpan(1)[i] + rebuilt.GetChannelSpan(2)[i]) / 3).ShouldBe(mean[y, x], 0.01, $"the mean at ({x}, {y})");
            }
        }
    }
}
