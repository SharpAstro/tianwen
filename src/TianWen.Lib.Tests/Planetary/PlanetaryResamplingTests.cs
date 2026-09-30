using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The kernel a planetary stack resamples its frames by (docs/plans/planetary-restoration.md, R5 part 3): frames of a known
/// pattern at sub-pixel phases spread evenly, each folded back by its own shift, so whatever the stack loses is the kernel's.
/// </summary>
public class PlanetaryResamplingTests
{
    private const int Width = 64;
    private const int Height = 8;
    private const int Phases = 16;
    private const double Frequency = 0.25;
    private const double Amplitude = 0.25;

    [Theory]
    [InlineData(WarpInterpolation.Bilinear, 0.8106)]
    [InlineData(WarpInterpolation.Lanczos3, 1.011)]
    [InlineData(WarpInterpolation.Lanczos3Clamped, 1.011)]
    public void AStackKeepsWhatItsKernelPassesOfAQuarterCyclePattern(WarpInterpolation interpolation, double expectedTransfer)
    {
        // Averaged over the phases, bilinear's triangle passes sinc squared of the frequency, (sin(pi/4) / (pi/4))^2 = 0.8106,
        // and Lanczos-3 a shade over one (1.011, its passband's ripple), the numbers the plan pre-registered.
        var channelAccum = new[] { new float[Height, Width] };
        var weightAccum = new float[Height, Width];
        for (var f = 0; f < Phases; f++)
        {
            var shift = (f + 0.5) / Phases;
            Frame(shift).AccumulateTranslatedInto(channelAccum, weightAccum, (float)shift, 0f, 1f, interpolation);
        }

        var (cos, sin) = (0.0, 0.0);
        var (from, to) = (8, 56);
        for (var x = from; x < to; x++)
        {
            var value = (channelAccum[0][4, x] / weightAccum[4, x]) - 0.5;
            cos += value * Math.Cos(2 * Math.PI * Frequency * x);
            sin += value * Math.Sin(2 * Math.PI * Frequency * x);
        }
        var transfer = 2 * cos / (to - from) / Amplitude;
        transfer.ShouldBe(expectedTransfer, 0.01);
        (2 * sin / (to - from) / Amplitude).ShouldBe(0, 0.01, "the stack is not displaced");
    }

    [Fact]
    public void BilinearByDefaultIsTheStackItAlwaysWas()
    {
        var frame = Frame(0.37);
        var (byDefault, bilinear) = (new[] { new float[Height, Width] }, new[] { new float[Height, Width] });
        var (weightDefault, weightBilinear) = (new float[Height, Width], new float[Height, Width]);
        frame.AccumulateTranslatedInto(byDefault, weightDefault, 0.37f, 0.2f, 1f);
        frame.AccumulateTranslatedInto(bilinear, weightBilinear, 0.37f, 0.2f, 1f, WarpInterpolation.Bilinear);
        byDefault[0].ShouldBe(bilinear[0]);
        weightDefault.ShouldBe(weightBilinear);
    }

    // The pattern moved by `shift` px: a frame's pixel x holds the truth at x - shift, so sampling it at x + shift is the truth.
    private static Image Frame(double shift)
    {
        var pixels = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                pixels[y, x] = (float)(0.5 + (Amplitude * Math.Cos(2 * Math.PI * Frequency * (x - shift))));
            }
        }
        return new Image([pixels], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }
}
