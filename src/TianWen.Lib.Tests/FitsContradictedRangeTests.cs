using System;
using System.IO;
using nom.tam.fits;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A header's <c>DATAMIN</c> / <c>DATAMAX</c> is a claim about the samples. By default a read BELIEVES it
/// (every file TianWen writes states its range, and checking costs a full pass over the planes); a read
/// that asks for validation takes the observed range when the samples leave the stated one, exactly as
/// it does when the cards are missing (#804).
/// </summary>
/// <remarks>
/// The file this reproduces is a drizzle weight sidecar from 2026-08: <c>DATAMAX = 1</c> over per-pixel
/// weights of 30 to 70, three planes. Read on the card's word, the image was unit-scaled, every sample
/// times 65535 fell past the histogram's bins, and the viewer's open failed with "Median and MAD should
/// have been calculated".
/// </remarks>
[Collection("Imaging")]
public class FitsContradictedRangeTests
{
    // Odd sizes, so every plane ends inside a vector and the scalar tail runs.
    private const int Width = 41, Height = 29, Planes = 3;

    private static float Sample(int c, int y, int x) => (c == 1 ? 64f : 31f) + ((y * 7 + x * 3 + c) % 11) * 0.5f;

    [Theory]
    [InlineData(1.0, 0.0)]      // the issue's cards: a [0, 1] fraction over weights up to 69
    [InlineData(50.0, 0.0)]     // a maximum inside the range the samples actually span
    [InlineData(1000.0, 40.0)]  // a minimum above samples the file holds
    public void WhenValidatingBothReadPathsTakeTheObservedRangeOverAContradictedCard(double dataMax, double dataMin)
    {
        var path = Path.Combine(SharedTestData.CreateTempTestOutputDir(), $"contradicted-{dataMin}-{dataMax}.fits");
        WriteWeightCube(path, dataMin, dataMax);
        var (observedMin, observedMax) = ObservedRange();

        Image.TryReadThroughFitsReader(path, out var viaReader, out _, pooled: false, validateRange: true).ShouldBeTrue("FitsReader takes a plain float cube");
        using (var fits = Image.OpenFits(path))
        {
            Image.TryReadFitsFile(fits, out var viaHdu, out _, pooled: false, validateRange: true).ShouldBeTrue();
            foreach (var (name, image) in new[] { ("FitsReader", viaReader), ("HDU reader", viaHdu) })
            {
                image.MaxValue.ShouldBe(observedMax, $"{name}: the samples' peak, not DATAMAX = {dataMax}");
                image.MinValue.ShouldBe(observedMin, $"{name}: the samples' floor, not DATAMIN = {dataMin}");
            }
        }
    }

    [Fact]
    public void ByDefaultBothReadPathsBelieveTheStatedRange()
    {
        // No validation asked, no pass over the planes: the cards are what the read records, even the
        // issue's contradicted ones. This is the cost the default saves on every file we write.
        var path = Path.Combine(SharedTestData.CreateTempTestOutputDir(), "believed-range.fits");
        WriteWeightCube(path, dataMin: 0.0, dataMax: 1.0);

        Image.TryReadThroughFitsReader(path, out var viaReader, out _, pooled: false).ShouldBeTrue();
        using var fits = Image.OpenFits(path);
        Image.TryReadFitsFile(fits, out var viaHdu, out _).ShouldBeTrue();
        foreach (var (name, image) in new[] { ("FitsReader", viaReader), ("HDU reader", viaHdu) })
        {
            image.MaxValue.ShouldBe(1f, $"{name}: DATAMAX as written");
            image.MinValue.ShouldBe(0f, $"{name}: DATAMIN as written");
        }
    }

    [Fact]
    public void WhenValidatingARangeTheSamplesKeepToIsTrusted()
    {
        // A stated range WIDER than the samples is legitimate (a writer may state the sensor's range),
        // so the cards stand: only a contradiction is overruled.
        var path = Path.Combine(SharedTestData.CreateTempTestOutputDir(), "kept-range.fits");
        WriteWeightCube(path, dataMin: 0.0, dataMax: 100.0);

        Image.TryReadFitsFile(path, out var image, out _, pooled: false, validateRange: true).ShouldBeTrue();
        image.MaxValue.ShouldBe(100f);
        image.MinValue.ShouldBe(0f);
    }

    [Fact]
    public void WhenValidatingAWeightMapUnderAFractionHeaderHasStatistics()
    {
        var path = Path.Combine(SharedTestData.CreateTempTestOutputDir(), "weights-under-fraction-header.fits");
        WriteWeightCube(path, dataMin: 0.0, dataMax: 1.0);

        Image.TryReadFitsFile(path, out var image, out _, pooled: false, validateRange: true).ShouldBeTrue();
        for (var c = 0; c < Planes; c++)
        {
            var (_, stretch) = image.GetStats(c);
            float.IsFinite(stretch.Median).ShouldBeTrue($"channel {c} has a median");
            float.IsFinite(stretch.Mad).ShouldBeTrue($"channel {c} has a MAD");
        }
    }

    private static (float Min, float Max) ObservedRange()
    {
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var c = 0; c < Planes; c++)
        {
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    min = MathF.Min(min, Sample(c, y, x));
                    max = MathF.Max(max, Sample(c, y, x));
                }
            }
        }

        return (min, max);
    }

    /// <summary>The issue's header on synthetic weights: BITPIX -32, three planes, the range cards as given,
    /// and the rejection-map frame type.</summary>
    private static void WriteWeightCube(string path, double dataMin, double dataMax)
    {
        var header = FitsWriter.ImageHeader(-32, Width, Height, Planes);
        header.AddValue("DATAMAX", dataMax, "");
        header.AddValue("DATAMIN", dataMin, "");
        header.AddValue("IMAGETYP", "REJECTION", "Per-pixel rejection-fraction map [0, 1]");
        header.AddValue("STACK_N", 135, "Frames the rejection map was computed against");
        var samples = new float[Width * Height * Planes];
        var i = 0;
        for (var c = 0; c < Planes; c++)
        {
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    samples[i++] = Sample(c, y, x);
                }
            }
        }

        using var writer = FitsWriter.CreateFile(path, header);
        writer.Write<float>(samples);
        writer.Finish();
    }
}
