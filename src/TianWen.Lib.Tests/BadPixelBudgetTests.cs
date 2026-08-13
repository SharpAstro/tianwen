using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the two-phase shape of <see cref="BadPixelDetection.BuildMaskFromDark"/>: estimate the
    /// noise scale once, then choose the threshold against a defect budget.
    ///
    /// <para>Measured motivation, from 15 archived Astro Pixel Processor maps for one ASI533 (see
    /// stats/bad-pixel-map-survey-2026-08-13.md): against a consensus defect set of 18,393 px, a
    /// FIXED sigma 8 recovered 32.95% of it on one master dark and 74.77% on another from the same
    /// sensor at a different gain, because sigma multiplies a quantized MAD that differs between
    /// them. Walking down to a budget recovers 85.99% and 89.42% from the same caller sigma.</para>
    ///
    /// <para>The synthetic dark below is built to have exactly that shape: a quantized bias floor
    /// (the reason real MADs land on values like 4.0 and 2.0 ADU), a warm population a fixed high
    /// sigma steps over, and a bright population it catches.</para>
    /// </summary>
    public class BadPixelBudgetTests
    {
        private const int Size = 512;
        private const int TotalPx = Size * Size;
        private const float BiasLow = 100f;
        private const float BiasHigh = 104f;
        private const int BrightCount = 500;   // unmistakable, found at any sigma
        private const int WarmCount = 800;     // above the floor, missed by a high fixed sigma

        /// <summary>
        /// Quantized bias floor plus two defect populations. Deterministic (a stride pattern, no
        /// RNG) so a failure is reproducible rather than a seed away.
        /// </summary>
        private static Image SyntheticDark(bool includeWarm = true)
        {
            var data = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    // Two-level floor: the quantization that makes a real cooled-CMOS dark's MAD
                    // collapse to 0 and take the non-zero-tail fallback.
                    data[y, x] = ((x + y) % 5) < 3 ? BiasLow : BiasHigh;
                }
            }

            // Spread the defects so the stride-8 statistics sample cannot systematically miss or
            // over-represent them.
            var placed = 0;
            for (var i = 0; placed < BrightCount && i < TotalPx; i += 97)
            {
                data[i / Size, i % Size] = 300f;
                placed++;
            }
            if (includeWarm)
            {
                placed = 0;
                for (var i = 3; placed < WarmCount && i < TotalPx; i += 61)
                {
                    var y = i / Size;
                    var x = i % Size;
                    if (data[y, x] > BiasHigh) { continue; }
                    data[y, x] = 120f;
                    placed++;
                }
            }

            var meta = new ImageMeta("synthetic", DateTime.UnixEpoch, TimeSpan.FromSeconds(120),
                FrameType.Dark, "", 3.76f, 3.76f, 130, -1, Filter.Unknown, 1, 1,
                -10f, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
            return new Image([data], BitDepth.Float32, 300f, BiasLow, 0f, meta);
        }

        private static int Count(BitMatrix[]? mask)
            => BadPixelDetection.CountMaskedPixels(mask, Size, Size);

        /// <summary>The regression the budget exists for: a caller passing a conservative sigma
        /// still gets the warm population, because the threshold descends until the count reaches
        /// the budget instead of stopping wherever a quantized MAD happens to put it.</summary>
        [Fact]
        public void BudgetWalksDownToCatchDefectsAFixedHighSigmaStepsOver()
        {
            var dark = SyntheticDark();

            var fixedSigma = Count(BadPixelDetection.BuildMaskFromDark(
                dark, sigmaThreshold: 8f, targetMaskedFraction: 0f));
            var budgeted = Count(BadPixelDetection.BuildMaskFromDark(
                dark, sigmaThreshold: 8f, targetMaskedFraction: 0.01f));

            fixedSigma.ShouldBe(BrightCount,
                "sigma 8 over this floor sits above the warm population entirely");
            budgeted.ShouldBe(BrightCount + WarmCount,
                "the walk should descend to just above the bias floor and take both populations");
        }

        /// <summary>The budget is a ceiling on the descent, not a target to reach: the walk stops
        /// BEFORE the step that would exceed it, so the bias floor is never swallowed even though
        /// the budget is far from full.</summary>
        [Fact]
        public void TheWalkStopsBeforeSwallowingTheBiasFloor()
        {
            var masked = Count(BadPixelDetection.BuildMaskFromDark(
                SyntheticDark(), sigmaThreshold: 8f, targetMaskedFraction: 0.01f));

            masked.ShouldBeLessThan((int)(TotalPx * 0.01f),
                "must stay inside the budget");
            masked.ShouldBeLessThan(TotalPx / 10,
                "the 40% of pixels sitting at the upper bias level are not defects");
        }

        /// <summary>Opting out reproduces the caller's sigma verbatim, so an existing caller can
        /// pin the old behaviour and the two paths stay distinguishable.</summary>
        [Fact]
        public void BudgetZeroUsesTheCallerSigmaVerbatim()
        {
            var dark = SyntheticDark(includeWarm: false);

            Count(BadPixelDetection.BuildMaskFromDark(dark, 8f, targetMaskedFraction: 0f))
                .ShouldBe(BrightCount);
            Count(BadPixelDetection.BuildMaskFromDark(dark, 20f, targetMaskedFraction: 0f))
                .ShouldBe(BrightCount, "the bright population is far above even sigma 20 here");
        }

        /// <summary>
        /// The property the old combined loop violated. With the noise scale estimated once and
        /// then held, the threshold is a monotone function of sigma, so a lower sigma can only add
        /// pixels. In the old shape a lower sigma re-entered the estimate, collapsed the MAD and
        /// produced a mask that was not a superset of anything -- it was the whole frame.
        /// </summary>
        [Theory]
        [InlineData(8f, 4f)]
        [InlineData(4f, 2f)]
        [InlineData(2f, 1f)]
        public void LoweringSigmaOnlyEverAddsPixels(float high, float low)
        {
            var dark = SyntheticDark();
            var atHigh = BadPixelDetection.BuildMaskFromDark(dark, high, targetMaskedFraction: 0f);
            var atLow = BadPixelDetection.BuildMaskFromDark(dark, low, targetMaskedFraction: 0f);
            atHigh.ShouldNotBeNull();
            atLow.ShouldNotBeNull();

            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    if (atHigh[0][y, x])
                    {
                        atLow[0][y, x].ShouldBeTrue($"({x},{y}) flagged at sigma {high} must stay flagged at {low}");
                    }
                }
            }
            Count(atLow).ShouldBeGreaterThanOrEqualTo(Count(atHigh));
        }

        /// <summary>A non-positive sigma still means "masking disabled", unchanged.</summary>
        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        public void NonPositiveSigmaDisablesMasking(float sigma)
        {
            BadPixelDetection.BuildMaskFromDark(SyntheticDark(), sigma).ShouldBeNull();
        }
    }
}
