using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The stretch statistics of a float frame whose sky sits a few histogram bins above zero
/// (<see cref="Image.GetPedestralMedianAndMADScaledToUnit"/>, <see cref="Image.GetStats"/>). A unit-scaled frame is binned
/// at 1/65535, and an enhanced narrowband master (eta Carinae 2026-02-20, pier A) put its sky at about 1e-4 with a MAD of
/// 0.2 to 0.5 of a bin: every channel's MAD came out at the half-bin floor and every median at a whole bin, and the
/// Unlinked stretch built on them rendered the card green. The rows below are that master's three channels, measured
/// exactly on its pixels.
/// </summary>
[Collection("Imaging")]
public class FaintFrameStretchStatsTests
{
    private const float Bin = 1f / 65535f;

    // The pier A master's channels after the enhance: exact median and MAD (red, green, blue).
    public static TheoryData<float, float> EtaCarinaeChannels => new()
    {
        { 7.7525e-5f, 7.8640e-6f },
        { 1.7424e-4f, 5.0211e-6f },
        { 9.9635e-5f, 3.0368e-6f },
    };

    [Theory]
    [MemberData(nameof(EtaCarinaeChannels))]
    public void AFaintFloatSkyReadsItsOwnMedianAndMad(float median, float mad)
    {
        var (image, exactMedian, exactMad) = Sky(median, mad, BitDepth.Float32, seed: 1);

        var (pedestal, readMedian, readMad) = image.GetPedestralMedianAndMADScaledToUnit(0);

        // A fine bin is 1/256 of a coarse one, so the median lands within a fraction of that and the MAD within a few
        // percent. The coarse walk was off by up to half a coarse bin in the median and floored every MAD at 0.5 bins.
        (pedestal + readMedian).ShouldBe(exactMedian, tolerance: Bin / 64f);
        readMad.ShouldBe(exactMad, tolerance: exactMad * 0.03f);
    }

    [Fact]
    public void TheThreeChannelsKeepTheirOwnNoise()
    {
        // The defect in one line: the three MADs read alike (the floor), where the pixels say blue's is 2.6 times
        // smaller than red's.
        var (red, _, _) = Sky(7.7525e-5f, 7.8640e-6f, BitDepth.Float32, seed: 2);
        var (blue, _, _) = Sky(9.9635e-5f, 3.0368e-6f, BitDepth.Float32, seed: 3);

        var redMad = red.GetPedestralMedianAndMADScaledToUnit(0).MAD;
        var blueMad = blue.GetPedestralMedianAndMADScaledToUnit(0).MAD;

        (redMad / blueMad).ShouldBe(7.8640f / 3.0368f, tolerance: 0.15f);
    }

    [Fact]
    public void GetStatsReadsTheSameRefinedStatistics()
    {
        var (image, _, _) = Sky(9.9635e-5f, 3.0368e-6f, BitDepth.Float32, seed: 4);

        var (_, stretch) = image.GetStats(0);
        var (pedestal, median, mad) = image.GetPedestralMedianAndMADScaledToUnit(0);

        stretch.Pedestal.ShouldBe(pedestal);
        stretch.Median.ShouldBe(median);
        stretch.Mad.ShouldBe(mad);
    }

    [Fact]
    public void AFrameWhoseNoiseSpansManyBinsIsReadAsBefore()
    {
        // The same samples as a float frame and as an integer source: an integer source is never refined, so it is the
        // coarse walk, and a MAD of about 40 bins is read by the coarse walk alone either way.
        var (asFloat, _, _) = Sky(0.02f, 6.0e-4f, BitDepth.Float32, seed: 5);
        var asInteger = Rewrap(asFloat, BitDepth.Int16);

        asFloat.GetPedestralMedianAndMADScaledToUnit(0).ShouldBe(asInteger.GetPedestralMedianAndMADScaledToUnit(0));
    }

    [Fact]
    public void AnIntegerSourceKeepsTheCoarseWalk()
    {
        // Samples on the 16-bit lattice, 40 % at 6 and 30 % either side: the coarse walk interpolates the MAD to
        // 0.667 bins, and a zoom would read the lattice back as 1. An integer source's samples are never finer than a
        // bin, so it is not zoomed.
        const int Width = 1000, Height = 1000;
        var plane = new float[Height, Width];
        var rng = new Random(6);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var u = rng.NextDouble();
                plane[y, x] = (u < 0.4 ? 6 : u < 0.7 ? 5 : 7) * Bin;
            }
        }
        var image = new Image([plane], BitDepth.Int16, 1f, 0f, 0f, default, samplesAreUnitReferred: true);

        var (_, median, mad) = image.GetPedestralMedianAndMADScaledToUnit(0);

        median.ShouldBe(6 * Bin, tolerance: Bin / 1000f);
        (mad / Bin).ShouldBe(0.667f, tolerance: 0.01f);
    }

    [Fact]
    public void AConstantFloatPlaneKeepsTheHalfBinFloor()
    {
        // Nothing to resolve: a zoom finds the samples in one fine bin, and the coarse floor stands.
        var plane = new float[256, 256];
        for (var y = 0; y < 256; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                plane[y, x] = 1e-4f;
            }
        }
        var image = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, default);

        var (_, median, mad) = image.GetPedestralMedianAndMADScaledToUnit(0);

        median.ShouldBe(1e-4f, tolerance: Bin);
        mad.ShouldBe(0.5f * Bin, tolerance: Bin / 1000f);
    }

    // A 1024 x 1024 Gaussian sky with the given median and MAD, its minimum as the image's MinValue as a real frame
    // carries it, and the exact median and MAD of the samples drawn.
    private static (Image Image, float Median, float Mad) Sky(float median, float mad, BitDepth bitDepth, int seed)
    {
        const int Size = 1024;
        var sigma = mad * 1.4826;
        var plane = new float[Size, Size];
        var flat = new float[Size * Size];
        var rng = new Random(seed);
        var min = float.MaxValue;
        for (var i = 0; i < flat.Length; i++)
        {
            // Box-Muller.
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            var v = (float)Math.Max(0, median + (sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)));
            plane[i / Size, i % Size] = v;
            flat[i] = v;
            min = Math.Min(min, v);
        }

        Array.Sort(flat);
        var exactMedian = flat[flat.Length / 2];
        for (var i = 0; i < flat.Length; i++)
        {
            flat[i] = Math.Abs(flat[i] - exactMedian);
        }
        Array.Sort(flat);
        var exactMad = flat[flat.Length / 2];

        return (new Image([plane], bitDepth, 1f, min, 0f, default), exactMedian, exactMad);
    }

    private static Image Rewrap(Image image, BitDepth bitDepth)
    {
        var (_, width, height) = image.Shape;
        var plane = new float[height, width];
        var source = image.GetChannelSpan(0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                plane[y, x] = source[(y * width) + x];
            }
        }
        return new Image([plane], bitDepth, 1f, image.MinValue, 0f, default, samplesAreUnitReferred: true);
    }
}
