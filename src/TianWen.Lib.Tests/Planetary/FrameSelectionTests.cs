using System;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;
using static TianWen.Lib.Tests.PlanetaryMetricsTests;

namespace TianWen.Lib.Tests;

/// <summary>
/// What R4 of docs/plans/planetary-restoration.md measures frame selection with: the FFT band estimator and its noise read off
/// the frequency plane's corners, and the cross-correlation registrar that puts a frame on the truth.
/// </summary>
public class FrameSelectionTests
{
    private static readonly PixelRect Whole = new PixelRect(0, 0, Size, Size);

    [Fact]
    public void TheRegistrarFindsASubpixelShiftAndUndoesIt()
    {
        // The truth moved (2.3, -1.6) px and made noisy: the registrar reads the move back, and its move puts the frame on the truth.
        var truth = Blur(Banded(), 1.5);
        var frame = Noisy(PlanetaryMetrics.Shift(truth, Size, Size, 2.3, -1.6), 0.02, new Random(11));
        var (moved, dx, dy) = new CorrelationRegistrar(truth, Size).Register(frame);
        TestContext.Current.TestOutputHelper?.WriteLine($"read ({dx:0.0000}, {dy:0.0000})");
        dx.ShouldBe(2.3, 0.01);
        dy.ShouldBe(-1.6, 0.01);
        var fidelity = PlanetaryMetrics.Fidelity(moved, truth, Size, Size, Disk);
        fidelity[2].Transfer.ShouldBe(1, 0.05);
    }

    [Fact]
    public void TheCornersReadAWhiteNoisesVariance()
    {
        // A flat frame of white noise: the corners read its variance, and a band debiased by them holds nothing.
        var random = new Random(5);
        var frame = Frame(Noisy(Flat(1), 0.1, random));
        var powers = FftHighBandEstimator.Measure(frame, Whole, [new FrequencyBand(0.125, 0.25)]) ?? throw new InvalidOperationException("no powers");
        TestContext.Current.TestOutputHelper?.WriteLine($"corner noise {powers.CornerNoise:E4}, band raw {powers.Detail(0, false, false):E4}, debiased {powers.Detail(0, true, false):E4}");
        powers.CornerNoise.ShouldBe(0.01, 0.0005);
        Math.Abs(powers.Detail(0, debias: true, normalizeBrightness: false)).ShouldBeLessThan(0.05 * powers.Detail(0, debias: false, normalizeBrightness: false));
    }

    [Fact]
    public void ABlurLowersABandAndDebiasingTakesMoreNoiseOut()
    {
        var band = new FrequencyBand(0.0625, 0.125);
        var sharp = Blur(Banded(), 1.0);
        var soft = Blur(Banded(), 2.0);
        var estimator = new FftHighBandEstimator(band);
        estimator.Score(Frame(sharp), Whole).ShouldBeGreaterThan(estimator.Score(Frame(soft), Whole));

        // The same frame, noisier: the finest band's raw power rises with the noise, its debiased power hardly moves.
        var fine = new FrequencyBand(0.25, 0.5);
        var quiet = FftHighBandEstimator.Measure(Frame(Noisy(sharp, 0.01, new Random(1))), Whole, [fine]) ?? throw new InvalidOperationException("no powers");
        var loud = FftHighBandEstimator.Measure(Frame(Noisy(sharp, 0.05, new Random(2))), Whole, [fine]) ?? throw new InvalidOperationException("no powers");
        var rawRise = loud.Detail(0, debias: false, normalizeBrightness: true) - quiet.Detail(0, debias: false, normalizeBrightness: true);
        var debiasedRise = loud.Detail(0, debias: true, normalizeBrightness: true) - quiet.Detail(0, debias: true, normalizeBrightness: true);
        TestContext.Current.TestOutputHelper?.WriteLine($"raw rise {rawRise:E3}, debiased rise {debiasedRise:E3}");
        rawRise.ShouldBeGreaterThan(0);
        Math.Abs(debiasedRise).ShouldBeLessThan(0.1 * rawRise);
    }

    private static Image Frame(float[] plane)
    {
        var pixels = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                pixels[y, x] = plane[(y * Size) + x];
            }
        }
        return Image.FromChannel(pixels);
    }

    private static float[] Flat(float level)
    {
        var plane = new float[Size * Size];
        Array.Fill(plane, level);
        return plane;
    }
}
