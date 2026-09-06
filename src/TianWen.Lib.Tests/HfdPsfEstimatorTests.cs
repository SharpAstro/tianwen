using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// The range belongs to the MODEL, and the shipped deconvolver runs SAS AI4, so the default must
    /// stay SAS's. A mismatch here is silent at runtime: the graph still runs and is simply told a PSF
    /// about twice the one it was handed.
    /// </summary>
    [Fact]
    public void TheDefaultRangeIsStillTheOneTheShippedModelWasTrainedUnder()
    {
        var estimator = new HfdPsfEstimator();

        estimator.RadiusRange.Min.ShouldBe(HfdPsfEstimator.MinRadiusPx);
        estimator.RadiusRange.Max.ShouldBe(HfdPsfEstimator.MaxRadiusPx);
        estimator.RadiusRange.ShouldNotBe((HfdPsfEstimator.TianWenMinRadiusPx, HfdPsfEstimator.TianWenMaxRadiusPx));
    }

    /// <summary>
    /// Resolving through the container must keep the shipped range, which is the thing the optional
    /// constructor parameters could quietly break: <c>TryAddSingleton&lt;IPsfEstimator, HfdPsfEstimator&gt;</c>
    /// has to pick the defaults rather than fail or fill them with zeroes.
    /// </summary>
    [Fact]
    public void TheContainerBuildsItWithTheShippedRange()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPsfEstimator, HfdPsfEstimator>();

        var resolved = services.BuildServiceProvider().GetRequiredService<IPsfEstimator>();

        resolved.ShouldBeOfType<HfdPsfEstimator>().RadiusRange
            .ShouldBe((HfdPsfEstimator.MinRadiusPx, HfdPsfEstimator.MaxRadiusPx));
    }

    /// <summary>
    /// TianWen's own contract, with the numbers E1 measured. The archive's median master reads 1.31 px
    /// radius, which SAS's range puts near the bottom of its usable span and this one puts mid-scale.
    /// </summary>
    [Theory]
    [InlineData(0.88f, 0.271f)]   // the sharpest master in the bake; NOT clamped, which is the point
    [InlineData(1.31f, 0.463f)]   // the median master
    [InlineData(3.03f, 0.866f)]   // the widest
    [InlineData(3.63f, 0.953f)]   // the widest input the exporter can produce, still inside
    public void TheTianWenContractSpreadsThisArchiveWithoutClamping(float radius, float expected)
    {
        HfdPsfEstimator.EncodeRadiusToPsf01(radius, HfdPsfEstimator.TianWenMinRadiusPx, HfdPsfEstimator.TianWenMaxRadiusPx)
            .ShouldBe(expected, 2e-3f);
    }

    /// <summary>
    /// The arithmetic behind H5's answer, pinned because two parts of it read backwards.
    /// </summary>
    /// <remarks>
    /// <para>psf01 is a ratio of logs, so for any UNCLAMPED pair of radii the spread is
    /// <c>log2(r_hi / r_lo) / log2(max / min)</c>. Only the range's total log SPAN appears, so
    /// widening a range reduces the spread of a fixed set of radii and narrowing it increases them.
    /// Lowering SAS's floor from 1.0 to 0.5 px while keeping the 8 px ceiling takes the span from
    /// 8:1 to 16:1 and costs exactly a quarter of the spread everywhere.</para>
    ///
    /// <para><b>TianWen's own contract has exactly SAS's span, and that is deliberate.</b> Both are
    /// 8:1, so they resolve identically; `[0.5, 4]` differs only in WHERE the window sits. Its whole
    /// measured gain over SAS's range on the real archive (0.330 against 0.293) is unclamping the six
    /// masters that were pinned to SAS's floor, and none of it is extra resolution. Buying resolution
    /// means narrowing the span, which costs clamping at one end or the other, and this test exists so
    /// that "the ceiling was the lever" cannot be believed again: it was the window position.</para>
    /// </remarks>
    [Fact]
    public void TheSpreadFollowsTheRangesLogSpanAndNothingElse()
    {
        const float Lo = 1.0f;
        const float Hi = 3.0f;

        float Spread(float min, float max)
            => HfdPsfEstimator.EncodeRadiusToPsf01(Hi, min, max) - HfdPsfEstimator.EncodeRadiusToPsf01(Lo, min, max);

        var sas = Spread(1.0f, 8.0f);
        var lowerFloor = Spread(0.5f, 8.0f);
        var tianwen = Spread(HfdPsfEstimator.TianWenMinRadiusPx, HfdPsfEstimator.TianWenMaxRadiusPx);
        var narrow = Spread(0.75f, 3.0f);

        // 16:1 against 8:1: exactly three quarters of the resolution, for unclamping a handful of frames.
        (lowerFloor / sas).ShouldBe(0.75f, 1e-4f);
        // 8:1 against 8:1: identical, which is the correction this test was written for.
        tianwen.ShouldBe(sas, 1e-6f);
        // 4:1 against 8:1: half the span, three halves the spread. This is the only way to buy resolution.
        (narrow / sas).ShouldBe(1.5f, 1e-4f);
    }
}
