using Shouldly;
using System;
using System.Linq;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// E16b's noise anchor (<see cref="HalfPairNoise"/>): a session's one-sub noise per channel from the scatter of its
/// half pair, each half stretched by its OWN stretch as the bake stores it and taken back to linear before the two
/// are differenced. The truth is the noise the fixture put into each half.
/// </summary>
[Collection("Imaging")]
public class HalfPairNoiseTests(ITestOutputHelper output)
{
    private const int Size = 256;
    private const int Channels = 3;

    [Theory]
    [InlineData(0.05, 0.01)]
    [InlineData(0.20, 0.00)]
    [InlineData(0.50, 0.03)]
    public void UnstretchInvertsTheInputStretch(double balance, double origMin)
    {
        var stretch = new StretchedNoise.ChannelStretch(balance, origMin);
        foreach (var x in new[] { 0.0, 0.001, 0.01, 0.05, 0.2, 0.6, 0.97 })
        {
            var linear = origMin + x;
            var stretched = Image.MidtonesTransferFunction(balance, linear - origMin);
            HalfPairNoise.Unstretch(stretched, stretch).ShouldBe(linear, 1e-9);
        }
    }

    /// <summary>
    /// Two halves of one scene, each with its own noise per channel (green at half red's, as a Bayer drizzle builds
    /// it) and each stretched by its own stretch: the accumulator returns every channel's own one-sub sigma.
    /// </summary>
    [Fact]
    public void EachChannelIsCalibratedFromItsOwnHalfPairScatter()
    {
        const int stackedFrames = 40;
        var oneSub = new[] { 8e-3, 4e-3, 6e-3 };
        var halfDepth = Math.Sqrt(2.0 / stackedFrames);
        var truth = Scene();
        var rng = new Random(7);
        var halfA = Noisy(truth, oneSub, halfDepth, rng);
        var halfB = Noisy(truth, oneSub, halfDepth, rng);
        var master = new float[truth.Length];
        for (var i = 0; i < master.Length; i++)
        {
            master[i] = 0.5f * (halfA[i] + halfB[i]);
        }

        var (aStretched, aStretch) = Stretch(halfA);
        var (bStretched, bStretch) = Stretch(halfB);
        var (mStretched, _) = Stretch(master);
        aStretch.ShouldNotBe(bStretch, "the halves must carry different stretches or the test is not testing the inversion");

        var accumulator = new HalfPairNoise.Accumulator(Channels);
        accumulator.Add(mStretched, master, aStretched, aStretch, bStretched, bStretch, Size);
        output.WriteLine($"quiet pixels: {accumulator.QuietPixels}");
        accumulator.TryCalibrate(0.0, stackedFrames, out var calibrations).ShouldBeTrue();

        for (var c = 0; c < Channels; c++)
        {
            // The fixture's noise is anchored at a level of 0.02 and grows as shot noise does, so the truth at the
            // background the calibration read (the quiet pixels' median, a little above the sky's floor) is scaled.
            var expected = oneSub[c] * Math.Sqrt(calibrations[c].BackgroundAdu / 0.02);
            output.WriteLine($"channel {c}: one sub {calibrations[c].OneSubSigmaAdu:E4} against {expected:E4}, background {calibrations[c].BackgroundAdu:E4}");
            calibrations[c].OneSubSigmaAdu.ShouldBe(expected, expected * 0.03);
        }
    }

    [Fact]
    public void TooFewQuietPixelsGiveNoCalibration()
    {
        var accumulator = new HalfPairNoise.Accumulator(Channels);
        accumulator.TryCalibrate(0.0, 10, out var calibrations).ShouldBeFalse();
        calibrations.ShouldBeNull();
    }

    /// <summary>A sky at 0.02 with a gradient and a bright blob, per channel, CHW.</summary>
    private static float[] Scene()
    {
        var n = Size * Size;
        var chw = new float[Channels * n];
        for (var c = 0; c < Channels; c++)
        {
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var dx = x - 180.0;
                    var dy = y - 170.0;
                    chw[(c * n) + (y * Size) + x] = (float)(0.02 + (0.002 * c) + (0.004 * x / Size) + (0.3 * Math.Exp(-((dx * dx) + (dy * dy)) / (2 * 25.0 * 25.0))));
                }
            }
        }
        return chw;
    }

    private static float[] Noisy(float[] truth, double[] oneSub, double depth, Random rng)
    {
        var n = Size * Size;
        var noisy = (float[])truth.Clone();
        for (var c = 0; c < Channels; c++)
        {
            var calibration = new LinearDegradation.NoiseCalibration(0.0, 0.02, oneSub[c], 1);
            var plane = noisy.AsSpan(c * n, n).ToArray();
            LinearDegradation.AddNoiseInPlace(plane, NoiseField.White(Size, Size, rng), calibration, depth);
            plane.CopyTo(noisy, c * n);
        }
        return noisy;
    }

    /// <summary>The input stretch of a CHW frame, as the exporter applies it, and its parameters.</summary>
    private static (float[] Stretched, StretchedNoise.ChannelStretch[] Stretch) Stretch(float[] chw)
    {
        var n = Size * Size;
        var planes = new float[Channels][,];
        for (var c = 0; c < Channels; c++)
        {
            var p = new float[Size, Size];
            for (var i = 0; i < n; i++)
            {
                p[i / Size, i % Size] = chw[(c * n) + i];
            }
            planes[c] = p;
        }
        var image = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var (origMin, balances) = image.MtfStretchParameters(0.25);
        var stretched = image.MtfStretchWith(origMin, balances);
        var result = new float[Channels * n];
        for (var c = 0; c < Channels; c++)
        {
            // fp16 on the way through, as the bake stores a tile.
            var span = stretched.GetChannelSpan(c);
            for (var i = 0; i < n; i++)
            {
                result[(c * n) + i] = (float)(Half)span[i];
            }
        }
        stretched.Release();
        image.Release();
        return (result, [.. origMin.Zip(balances, static (m, b) => new StretchedNoise.ChannelStretch(b, m))]);
    }
}
