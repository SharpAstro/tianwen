using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class FrameCacheTests
{
    private const long Gb = 1024L * 1024L * 1024L;

    // Anchor: hosts at or below the low anchor get the cautious floor. 16 GB
    // is where the SoL 60s pass-2 OOM bit us; 0.65 leaves ~1 extra GB clear
    // beyond what 0.80 did under the same workload.
    [Theory]
    [InlineData(4L * Gb)]
    [InlineData(8L * Gb)]
    [InlineData(16L * Gb)]
    public void ScaledBudgetFraction_AtOrBelowLowAnchor_ReturnsFloor(long totalRamBytes)
    {
        FrameCache.ScaledBudgetFraction(totalRamBytes).ShouldBe(FrameCache.MinBudgetFraction);
    }

    [Theory]
    [InlineData(128L * Gb)]
    [InlineData(256L * Gb)]
    [InlineData(1024L * Gb)]
    public void ScaledBudgetFraction_AtOrAboveHighAnchor_ReturnsCeiling(long totalRamBytes)
    {
        FrameCache.ScaledBudgetFraction(totalRamBytes).ShouldBe(FrameCache.MaxBudgetFraction);
    }

    // Logarithmic interpolation: doubling RAM should advance a fixed step
    // along the curve. 16 -> 32 GB and 32 -> 64 GB both span one log2 unit,
    // so the fraction delta is constant (~0.10 each step on this curve).
    [Theory]
    [InlineData(32L * Gb, 0.75)]
    [InlineData(64L * Gb, 0.85)]
    public void ScaledBudgetFraction_AtKnownLogStep_MatchesExpected(long totalRamBytes, double expected)
    {
        FrameCache.ScaledBudgetFraction(totalRamBytes).ShouldBe(expected, tolerance: 0.001);
    }

    [Fact]
    public void ScaledBudgetFraction_MonotonicallyIncreasing()
    {
        var prev = FrameCache.ScaledBudgetFraction(0);
        for (var gb = 8L; gb <= 256; gb *= 2)
        {
            var current = FrameCache.ScaledBudgetFraction(gb * Gb);
            current.ShouldBeGreaterThanOrEqualTo(prev, $"at {gb} GB");
            prev = current;
        }
    }

    // Explicit override path still works (tests + benchmarks pin a specific
    // fraction independent of the host).
    [Fact]
    public void DecideCacheCap_ExplicitBudgetFraction_IgnoresScaling()
    {
        // Tiny frames + small frame count -> all frames fit regardless of
        // fraction; we're just exercising the parameter wiring here.
        var cap = FrameCache.DecideCacheCap(frameCount: 10, frameBytes: 1024, budgetFraction: 0.5);
        cap.ShouldBe(10);
    }
}
