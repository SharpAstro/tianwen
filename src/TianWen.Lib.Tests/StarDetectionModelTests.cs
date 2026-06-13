using Shouldly;
using TianWen.Lib.Astrometry;
using Xunit;

namespace TianWen.Lib.Tests
{
    public class StarDetectionModelTests
    {
        // Golden values pinned from the pre-extraction formula (apertureScaleFactor 16 = a 200 mm
        // aperture: (200/50)^2). If the extraction ever drifts these move, which is the point.
        [Theory]
        [InlineData(16.0, 0.01, 7.87)]   // very short exposure -> bright cutoff
        [InlineData(16.0, 5.0, 14.62)]   // 5 s -> deep cutoff (the fake's clear-frame regime)
        public void DetectabilityMagCutoff_MatchesLegacyFormula(double apertureScale, double exposureSeconds, double expected)
        {
            var cutoff = StarDetectionModel.DetectabilityMagCutoff(apertureScale, exposureSeconds);
            cutoff.ShouldBe(expected, 0.01);
        }

        [Fact]
        public void DetectabilityMagCutoff_LongerExposureAndBiggerApertureGoFainter()
        {
            var baseline = StarDetectionModel.DetectabilityMagCutoff(1.0, 1.0);
            StarDetectionModel.DetectabilityMagCutoff(1.0, 10.0).ShouldBeGreaterThan(baseline); // longer exposure
            StarDetectionModel.DetectabilityMagCutoff(16.0, 1.0).ShouldBeGreaterThan(baseline); // bigger aperture
        }

        [Fact]
        public void DetectabilityMagCutoff_HigherSnrThresholdIsStricter()
        {
            // A higher SNR floor (the scout's 10 vs the default 5) can only make the cutoff brighter.
            var snr5 = StarDetectionModel.DetectabilityMagCutoff(16.0, 5.0, snrThreshold: 5.0);
            var snr10 = StarDetectionModel.DetectabilityMagCutoff(16.0, 5.0, snrThreshold: 10.0);
            snr10.ShouldBeLessThan(snr5);
        }
    }
}
