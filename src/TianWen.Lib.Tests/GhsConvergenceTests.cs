using System;
using System.Collections.Immutable;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Validation tests for <see cref="Image.ConvergeGhsStretchFactor"/>
/// (PLAN-ghs.md Phase 5). Covers: median convergence on diverse synthetic
/// histograms, determinism, safe fallback for empty input, non-default
/// target respected, and that the log-slope R^2 metric is computed without
/// throwing.
/// </summary>
[Collection("Imaging")]
public class GhsConvergenceTests
{
    /// <summary>
    /// Builds a synthetic histogram approximating a linear astro frame:
    /// most pixels at the sky background, sparse bright tail. Returns a
    /// minimal <see cref="ImageHistogram"/> with the fields the
    /// convergence helper actually reads.
    /// </summary>
    private static ImageHistogram SyntheticHistogram(int bgBin, int tailStart, int tailEnd, uint bgCount, uint tailDensity)
    {
        var bins = new uint[65536];
        var total = 0u;
        bins[bgBin] = bgCount; total += bgCount;
        for (var i = tailStart; i <= tailEnd; i++) { bins[i] = tailDensity; total += tailDensity; }
        return new ImageHistogram(
            Channel: 0,
            Histogram: ImmutableArray.Create(bins),
            Mean: bgBin, Total: total, Threshold: 0f, ThresholdPct: 91,
            RescaledMaxValue: 65535f, Median: bgBin, MAD: 1f, IgnoreBlack: false);
    }

    [Fact]
    public void DarkSkyHistogram_ConvergesMedianToQuarter()
    {
        // Background-dominated histogram: bg peak at bin 3000 (value
        // ~0.046, typical post-calibration linear bg), sparse 1% tail
        // bins 10000-50000. SP is set to the lift-off (~0.003), well
        // BELOW the bg peak, matching Paul's case-1 recipe. The bg bin
        // MUST hold > halfTotal so the input median actually IS the bg,
        // mirroring real-astro stats. Convergence should bisect LnD
        // until the bg peak lifts to ~0.25.
        var hist = SyntheticHistogram(bgBin: 3000, tailStart: 10000, tailEnd: 50000,
            bgCount: 1_000_000, tailDensity: 1);

        var result = Image.ConvergeGhsStretchFactor(
            hist, b: 8.0, sp: 0.003, lp: 0.0, hp: 0.8,
            targetMedian: 0.25, medianTolerance: 0.01);

        result.ConvergedMedian.ShouldBeTrue(
            $"bisection should hit median 0.25; got median={result.PostStretchMedian:F4} at LnD={result.LnD:F3}");
        result.PostStretchMedian.ShouldBeInRange(0.24, 0.26);
        // LnD should land in a sensible range -- not at the bisection bounds
        // (which would mean the target was unreachable).
        result.LnD.ShouldBeInRange(0.1, 8.0);
        result.LnD.ShouldBeGreaterThan(0.15);  // not pegged at lo
        result.LnD.ShouldBeLessThan(7.99);     // not pegged at hi
        // Log-slope R^2 is computed for any input that produces a
        // non-degenerate post-stretch histogram; it should be finite.
        double.IsFinite(result.LogSlopeRSquared).ShouldBeTrue(
            $"R^2 should be finite for this input; got {result.LogSlopeRSquared}");
    }

    [Fact]
    public void Deterministic_SameInputProducesSameOutput()
    {
        // ConvergeGhsStretchFactor has no random state -- two invocations
        // with identical input must produce identical output. Catches
        // accidental introduction of RNG / time-based / hash-order
        // dependencies.
        var hist = SyntheticHistogram(bgBin: 500, tailStart: 15000, tailEnd: 45000,
            bgCount: 500_000, tailDensity: 1);

        var a = Image.ConvergeGhsStretchFactor(hist, b: 8.0, sp: 0.008);
        var b = Image.ConvergeGhsStretchFactor(hist, b: 8.0, sp: 0.008);

        a.LnD.ShouldBe(b.LnD);
        a.PostStretchMedian.ShouldBe(b.PostStretchMedian);
        a.LogSlopeRSquared.ShouldBe(b.LogSlopeRSquared);
        a.ConvergedMedian.ShouldBe(b.ConvergedMedian);
    }

    [Fact]
    public void EmptyHistogram_ReturnsSafeFallback()
    {
        // Total = 0 has no signal for the bisection; the helper must
        // return defaults (LnD = 1.30, NaN metrics, ConvergedMedian =
        // false) without throwing.
        var bins = new uint[65536];
        var hist = new ImageHistogram(
            Channel: 0, Histogram: ImmutableArray.Create(bins),
            Mean: 0f, Total: 0f, Threshold: 0f, ThresholdPct: 91,
            RescaledMaxValue: 65535f, Median: 0f, MAD: 0f, IgnoreBlack: false);

        var result = Image.ConvergeGhsStretchFactor(hist, sp: 0.05);

        result.LnD.ShouldBe(1.30);
        result.ConvergedMedian.ShouldBeFalse();
        double.IsNaN(result.PostStretchMedian).ShouldBeTrue();
        double.IsNaN(result.LogSlopeRSquared).ShouldBeTrue();
    }

    [Fact]
    public void NonDefaultTargetMedian_Respected()
    {
        // Pass a non-default target (0.35) and a tight tolerance;
        // converged median should respect the new target, not the
        // 0.25 default. Catches hardcoded constants leaking into
        // the bisection.
        var hist = SyntheticHistogram(bgBin: 3000, tailStart: 10000, tailEnd: 50000,
            bgCount: 1_000_000, tailDensity: 1);

        var result = Image.ConvergeGhsStretchFactor(
            hist, b: 8.0, sp: 0.003, hp: 0.8,
            targetMedian: 0.35, medianTolerance: 0.005);

        result.PostStretchMedian.ShouldBeInRange(0.345, 0.355,
            $"target=0.35 should land in [0.345, 0.355]; got {result.PostStretchMedian:F4}");
    }

    [Fact]
    public void BrighterBackground_RequiresLowerLnD()
    {
        // When the input bg is already brighter (bin 6000 ~ 0.092 vs
        // bin 3000 ~ 0.046), less lift is needed to hit the same target
        // median. SP fixed at the lift-off (~0.003) for both runs so we
        // isolate the bg-position effect from any SP-driven curve change.
        // Bisection should converge to a SMALLER LnD on the brighter input.
        var dimHist = SyntheticHistogram(bgBin: 3000, tailStart: 10000, tailEnd: 50000,
            bgCount: 1_000_000, tailDensity: 1);
        var brightHist = SyntheticHistogram(bgBin: 6000, tailStart: 10000, tailEnd: 50000,
            bgCount: 1_000_000, tailDensity: 1);

        var dimResult = Image.ConvergeGhsStretchFactor(dimHist, b: 8.0, sp: 0.003);
        var brightResult = Image.ConvergeGhsStretchFactor(brightHist, b: 8.0, sp: 0.003);

        // Both should converge.
        dimResult.ConvergedMedian.ShouldBeTrue();
        brightResult.ConvergedMedian.ShouldBeTrue();
        // The brighter input should need LESS lift -- LnD smaller.
        brightResult.LnD.ShouldBeLessThan(dimResult.LnD,
            $"brighter input needed LnD={brightResult.LnD:F3}; dimmer needed LnD={dimResult.LnD:F3} -- monotonicity is wrong");
    }
}
