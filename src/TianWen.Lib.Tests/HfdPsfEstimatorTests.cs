using Shouldly;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for <see cref="HfdPsfEstimator.EncodeRadiusToPsf01"/>. The
/// FindStarsAsync integration path is exercised end-to-end in the AI
/// enhancement integration suite (Phase 4+).
/// </summary>
public class HfdPsfEstimatorTests
{
    [Theory]
    [InlineData(1.0f, 0.0f)]    // radius=1px -> log2(1)=0 -> psf01=0
    [InlineData(2.0f, 1f / 3f)] // log2(2)/log2(8) = 1/3
    [InlineData(4.0f, 2f / 3f)] // log2(4)/log2(8) = 2/3
    [InlineData(8.0f, 1.0f)]    // log2(8)/log2(8) = 1
    public void EncodeRadiusToPsf01_KnownPoints(float radius, float expected)
    {
        HfdPsfEstimator.EncodeRadiusToPsf01(radius).ShouldBe(expected, 1e-5f);
    }

    [Theory]
    [InlineData(0.5f, 0.0f)]    // below min clamps to 0
    [InlineData(0.0f, 0.0f)]
    [InlineData(20f, 1.0f)]     // above max clamps to 1
    [InlineData(100f, 1.0f)]
    public void EncodeRadiusToPsf01_ClampsOutsideTrainingRange(float radius, float expected)
    {
        HfdPsfEstimator.EncodeRadiusToPsf01(radius).ShouldBe(expected, 1e-5f);
    }

    [Fact]
    public void EncodeRadiusToPsf01_DefaultRadiusYieldsApproximatelyHalf()
    {
        // SAS Pro's default fallback radius is 3.0 px. log2(3) / log2(8) ≈ 0.528.
        HfdPsfEstimator.EncodeRadiusToPsf01(HfdPsfEstimator.DefaultRadiusPx)
            .ShouldBe(0.528f, 1e-2f);
    }
}
