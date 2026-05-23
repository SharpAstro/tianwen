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
    /// convergence helper actually reads. Mode (bg peak) ~= median for
    /// this layout, so it's NOT useful for showing the dim-image
    /// diagnosis -- use <see cref="SyntheticHistogramWithDiffuse"/> for
    /// that.
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

    /// <summary>
    /// Builds a synthetic histogram with a bg peak, a diffuse-signal
    /// band immediately above it, and a sparse bright tail. Calibrated
    /// to produce input median &gt; input mode so the median-vs-mode
    /// post-stretch gap is visible -- which is the real-astro
    /// regime (extended nebulae, galaxies, etc.) where the dim-image
    /// observation lives.
    /// </summary>
    private static ImageHistogram SyntheticHistogramWithDiffuse(
        int bgBin, uint bgCount,
        int diffuseStart, int diffuseEnd, uint diffuseDensity,
        int tailStart, int tailEnd, uint tailDensity)
    {
        var bins = new uint[65536];
        var total = 0u;
        bins[bgBin] += bgCount; total += bgCount;
        for (var i = diffuseStart; i <= diffuseEnd; i++) { bins[i] += diffuseDensity; total += diffuseDensity; }
        for (var i = tailStart; i <= tailEnd; i++) { bins[i] += tailDensity; total += tailDensity; }
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
            targetValue: 0.25, tolerance: 0.01);

        result.Converged.ShouldBeTrue(
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
        a.PostStretchMode.ShouldBe(b.PostStretchMode);
        a.LogSlopeRSquared.ShouldBe(b.LogSlopeRSquared);
        a.Converged.ShouldBe(b.Converged);
    }

    [Fact]
    public void EmptyHistogram_ReturnsSafeFallback()
    {
        // Total = 0 has no signal for the bisection; the helper must
        // return defaults (LnD = 1.30, NaN metrics, Converged = false)
        // without throwing.
        var bins = new uint[65536];
        var hist = new ImageHistogram(
            Channel: 0, Histogram: ImmutableArray.Create(bins),
            Mean: 0f, Total: 0f, Threshold: 0f, ThresholdPct: 91,
            RescaledMaxValue: 65535f, Median: 0f, MAD: 0f, IgnoreBlack: false);

        var result = Image.ConvergeGhsStretchFactor(hist, sp: 0.05);

        result.LnD.ShouldBe(1.30);
        result.Converged.ShouldBeFalse();
        double.IsNaN(result.PostStretchMedian).ShouldBeTrue();
        double.IsNaN(result.PostStretchMode).ShouldBeTrue();
        double.IsNaN(result.LogSlopeRSquared).ShouldBeTrue();
    }

    [Fact]
    public void NonDefaultTargetValue_Respected()
    {
        // Pass a non-default target (0.35) and a tight tolerance;
        // converged median should respect the new target, not the
        // 0.25 default. Catches hardcoded constants leaking into
        // the bisection.
        var hist = SyntheticHistogram(bgBin: 3000, tailStart: 10000, tailEnd: 50000,
            bgCount: 1_000_000, tailDensity: 1);

        var result = Image.ConvergeGhsStretchFactor(
            hist, b: 8.0, sp: 0.003, hp: 0.8,
            targetValue: 0.35, tolerance: 0.005);

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
        dimResult.Converged.ShouldBeTrue();
        brightResult.Converged.ShouldBeTrue();
        // The brighter input should need LESS lift -- LnD smaller.
        brightResult.LnD.ShouldBeLessThan(dimResult.LnD,
            $"brighter input needed LnD={brightResult.LnD:F3}; dimmer needed LnD={dimResult.LnD:F3} -- monotonicity is wrong");
    }

    [Fact]
    public void ModeTarget_ConvergesBgPeakNotMedian()
    {
        // The mode-target path bisects until the bg peak lifts to the
        // requested value (Paul's recipe), not the median. Use the
        // richer "with-diffuse" histogram so input median > input mode
        // -- otherwise mode and median collapse to the same bin in
        // bg-dominated synthetics and the test can't distinguish the
        // two targets. Mode-target should land the bg peak at ~0.25
        // and push the median above that.
        var hist = SyntheticHistogramWithDiffuse(
            bgBin: 3000, bgCount: 400_000,
            diffuseStart: 4500, diffuseEnd: 8000, diffuseDensity: 150,
            tailStart: 8001, tailEnd: 50000, tailDensity: 2);

        var result = Image.ConvergeGhsStretchFactor(
            hist, b: 8.0, sp: 0.003, lp: 0.0, hp: 0.8,
            targetValue: 0.25, tolerance: 0.01,
            target: Image.GhsConvergeTarget.Mode);

        result.Converged.ShouldBeTrue(
            $"bisection should hit mode 0.25; got mode={result.PostStretchMode:F4} at LnD={result.LnD:F3}");
        result.PostStretchMode.ShouldBeInRange(0.24, 0.26);
        // Telemetric median is still computed and should sit ABOVE the
        // mode (the lifted bg peak), since diffuse signal pushes the
        // 50th percentile higher than the bg peak.
        result.PostStretchMedian.ShouldBeGreaterThan(result.PostStretchMode,
            $"median {result.PostStretchMedian:F3} should be above mode {result.PostStretchMode:F3} for a hist with diffuse signal");
    }

    [Fact]
    public void Diagnostic_MedianTarget_LeavesBgPeakBelowTarget()
    {
        // Diagnostic for the "GHS output looks dim" observation. With
        // a realistic linear astro histogram (bg peak + diffuse signal
        // band + sparse tail, so input median > input mode), converging
        // the median to 0.25 lands the bg peak (mode) well below 0.25.
        // The result feels dim because the bulk of the visible area
        // sits at the bg peak, not the median. Mode-target is the fix;
        // this test pins the diagnosis so a future change to the
        // bisection or mode helper can't quietly undo it.
        var hist = SyntheticHistogramWithDiffuse(
            bgBin: 3000, bgCount: 400_000,
            diffuseStart: 4500, diffuseEnd: 8000, diffuseDensity: 150,
            tailStart: 8001, tailEnd: 50000, tailDensity: 2);

        var result = Image.ConvergeGhsStretchFactor(
            hist, b: 8.0, sp: 0.003, lp: 0.0, hp: 0.8,
            targetValue: 0.25, tolerance: 0.01,
            target: Image.GhsConvergeTarget.Median);

        result.Converged.ShouldBeTrue();
        result.PostStretchMedian.ShouldBeInRange(0.24, 0.26);
        // The bg peak should land BELOW the median target -- this is
        // the dim-image diagnosis. Strict ordering is the signal we
        // care about; the exact gap depends on curve shape + diffuse
        // band density and may shift with future BuildGhsLut tweaks.
        result.PostStretchMode.ShouldBeLessThan(result.PostStretchMedian,
            $"median-target convergence leaves the bg peak at {result.PostStretchMode:F3}, below median {result.PostStretchMedian:F3}; use Mode target for Paul's recipe");
    }
}
