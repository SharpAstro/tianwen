using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for <see cref="Image.ReplaceNonFiniteWithChannelMean"/>, the NaN guard
/// the SharpenPipeline applies at its input boundary so drizzle coverage holes
/// don't poison the AI enhancers (SAS or RC-Astro) to all-NaN output.
/// </summary>
public class ImageNonFiniteFillTests
{
    private static Image Mono(float[,] channel)
        => new([channel], BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta { SensorType = SensorType.Monochrome });

    [Fact]
    public void CleanImage_ReturnsSameInstance_NoCopy()
    {
        var img = Mono(new float[,] { { 0.1f, 0.2f }, { 0.3f, 0.4f } });
        img.ReplaceNonFiniteWithChannelMean().ShouldBeSameAs(img);
    }

    [Fact]
    public void NonFinite_FilledWithChannelMean_FiniteUntouched()
    {
        // finite samples 0.2, 0.4, 0.6 -> mean 0.4; the NaN at [h0,w1] is filled.
        var img = Mono(new float[,] { { 0.2f, float.NaN }, { 0.4f, 0.6f } });

        var result = img.ReplaceNonFiniteWithChannelMean();

        result.ShouldNotBeSameAs(img);
        foreach (var v in result.GetChannelSpan(0))
        {
            float.IsFinite(v).ShouldBeTrue();
        }
        result[0, 0, 1].ShouldBe(0.4f, 1e-5f); // filled with mean
        result[0, 0, 0].ShouldBe(0.2f);        // finite untouched
        result[0, 1, 0].ShouldBe(0.4f);
        result[0, 1, 1].ShouldBe(0.6f);
    }

    [Fact]
    public void InfinitiesAreTreatedAsNonFinite()
    {
        var img = Mono(new float[,] { { 0.5f, float.PositiveInfinity }, { float.NegativeInfinity, 0.7f } });

        var result = img.ReplaceNonFiniteWithChannelMean();

        // finite samples 0.5, 0.7 -> mean 0.6
        result[0, 0, 1].ShouldBe(0.6f, 1e-5f);
        result[0, 1, 0].ShouldBe(0.6f, 1e-5f);
        foreach (var v in result.GetChannelSpan(0))
        {
            float.IsFinite(v).ShouldBeTrue();
        }
    }

    [Fact]
    public void AllNonFiniteChannel_FilledWithZero()
    {
        var img = Mono(new float[,] { { float.NaN, float.PositiveInfinity }, { float.NaN, float.NegativeInfinity } });

        var result = img.ReplaceNonFiniteWithChannelMean();

        foreach (var v in result.GetChannelSpan(0))
        {
            v.ShouldBe(0f);
        }
    }
}
