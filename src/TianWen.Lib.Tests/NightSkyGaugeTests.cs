using Shouldly;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests
{
    public class NightSkyGaugeTests
    {
        // Cumulative histogram: bins[b] = stars with V <= (b+1)*0.5, here 10 per half-mag bin
        // (cumulative 10, 20, ... 300). Predicted at mag 11 = bins[(int)(11/0.5)-1] = bins[21] = 220.
        private static int[] LinearBins()
        {
            var bins = new int[CatalogStarCounterBinCount];
            for (var b = 0; b < bins.Length; b++)
            {
                bins[b] = (b + 1) * 10;
            }
            return bins;
        }

        private const int CatalogStarCounterBinCount = 30;

        [Fact]
        public void FromCounts_ClearSky_EfficiencyNearOne_AndEffectiveLimitInverted()
        {
            var g = NightSkyGauge.FromCounts(detectedAtZenith: 210, LinearBins(), theoreticalLimitMag: 11.0, minPredictedToTrust: 10);

            g.Valid.ShouldBeTrue();
            g.CatalogPredictedAtZenith.ShouldBe(220);          // bins[21]
            g.Efficiency.ShouldBe(210.0 / 220.0, 0.001);
            g.EffectiveLimitMag.ShouldBe(10.5, 0.001);          // first bin whose cumulative >= 210 is bins[20]=210 -> (20+1)*0.5
        }

        [Fact]
        public void FromCounts_Hazy_LowEfficiency()
        {
            var g = NightSkyGauge.FromCounts(detectedAtZenith: 30, LinearBins(), theoreticalLimitMag: 11.0, minPredictedToTrust: 10);
            g.Valid.ShouldBeTrue();
            g.Efficiency.ShouldBeLessThan(0.2);                 // 30/220 -> serious haze/cloud territory
        }

        [Fact]
        public void FromCounts_TooFewPredicted_IsInvalid()
        {
            // theoreticalLimit 2.0 -> predicted = bins[3] = 40, below the trust floor of 50.
            var g = NightSkyGauge.FromCounts(detectedAtZenith: 20, LinearBins(), theoreticalLimitMag: 2.0, minPredictedToTrust: 50);
            g.Valid.ShouldBeFalse();
        }

        [Fact]
        public void FromCounts_DetectedExceedsPrediction_EfficiencyClampsToOne()
        {
            var g = NightSkyGauge.FromCounts(detectedAtZenith: 500, LinearBins(), theoreticalLimitMag: 11.0, minPredictedToTrust: 10);
            g.Efficiency.ShouldBe(1.0);
        }

        [Fact]
        public void None_IsInvalid()
        {
            NightSkyGauge.None.Valid.ShouldBeFalse();
        }
    }
}
