using Shouldly;
using System;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

public class ProportionalGuideControllerTests
{
    private static GuiderCalibrationResult MakeCalibration(
        double cameraAngleRad = 0,
        double raRatePixPerSec = 5.0,
        double decRatePixPerSec = 5.0)
    {
        return new GuiderCalibrationResult(
            CameraAngleRad: cameraAngleRad,
            RaRatePixPerSec: raRatePixPerSec,
            DecRatePixPerSec: decRatePixPerSec,
            RaDisplacementPx: 15.0,
            DecDisplacementPx: 15.0,
            TotalCalibrationTimeSec: 6.0);
    }

    [Fact]
    public void GivenZeroErrorWhenComputeThenNoCorrection()
    {
        var controller = new ProportionalGuideController();
        var cal = MakeCalibration();

        var correction = controller.Compute(cal, 0, 0);

        correction.HasRaCorrection.ShouldBeFalse();
        correction.HasDecCorrection.ShouldBeFalse();
    }

    [Fact]
    public void GivenPureRaErrorWhenAlignedCameraThenRaCorrectionOnly()
    {
        var controller = new ProportionalGuideController { AggressivenessRa = 1.0 };
        var cal = MakeCalibration(cameraAngleRad: 0);

        // 1 pixel RA error at 5 px/s rate → -200ms correction (opposes the error)
        var correction = controller.Compute(cal, 1.0, 0);

        correction.HasRaCorrection.ShouldBeTrue();
        correction.RaPulseMs.ShouldBe(-200, 1.0);
        // With aligned camera, pure X error should produce no Dec correction
        correction.DecPulseMs.ShouldBe(0);
    }

    [Fact]
    public void GivenLargeErrorWhenComputeThenClampedToMaxPulse()
    {
        var controller = new ProportionalGuideController
        {
            AggressivenessRa = 1.0,
            MaxPulseMs = 1000
        };
        var cal = MakeCalibration(raRatePixPerSec: 1.0); // slow rate = long pulses

        // 10 pixels at 1 px/s = 10000ms → should be clamped to -1000ms (opposing the error)
        var correction = controller.Compute(cal, 10.0, 0);

        correction.RaPulseMs.ShouldBe(-1000);
    }

    [Fact]
    public void GivenSmallErrorWhenComputeThenDeadZoneSuppresses()
    {
        var controller = new ProportionalGuideController
        {
            AggressivenessRa = 1.0,
            MinPulseMs = 50
        };
        var cal = MakeCalibration(raRatePixPerSec: 5.0);

        // 0.1 pixels at 5 px/s = 20ms → below 50ms dead zone
        var correction = controller.Compute(cal, 0.1, 0);

        correction.HasRaCorrection.ShouldBeFalse();
    }

    [Fact]
    public void GivenAggressivenessWhenComputeThenScalesPulse()
    {
        var controller50 = new ProportionalGuideController { AggressivenessRa = 0.5 };
        var controller100 = new ProportionalGuideController { AggressivenessRa = 1.0 };
        var cal = MakeCalibration();

        var corr50 = controller50.Compute(cal, 2.0, 0);
        var corr100 = controller100.Compute(cal, 2.0, 0);

        // 50% aggressiveness should produce half the pulse
        Math.Abs(corr50.RaPulseMs).ShouldBe(Math.Abs(corr100.RaPulseMs) * 0.5, 1.0);
    }

    [Fact]
    public void GivenDecDisabledWhenComputeThenNoDecPulse()
    {
        var controller = new ProportionalGuideController { DecGuideEnabled = false };
        var cal = MakeCalibration();

        var correction = controller.Compute(cal, 0, 5.0);

        correction.HasDecCorrection.ShouldBeFalse();
    }

    [Fact]
    public void GivenRotatedCameraWhenComputeThenBothAxesCorrected()
    {
        var controller = new ProportionalGuideController
        {
            AggressivenessRa = 1.0,
            AggressivenessDec = 1.0
        };
        // 45° camera rotation
        var cal = MakeCalibration(cameraAngleRad: Math.PI / 4);

        // Pure X error of 1px → decomposed into both RA and Dec
        var correction = controller.Compute(cal, 1.0, 0);

        correction.HasRaCorrection.ShouldBeTrue();
        correction.HasDecCorrection.ShouldBeTrue();
    }

    [Fact]
    public void GivenNegativeErrorWhenComputeThenPositivePulse()
    {
        var controller = new ProportionalGuideController { AggressivenessRa = 1.0 };
        var cal = MakeCalibration(cameraAngleRad: 0);

        var correction = controller.Compute(cal, -1.0, 0);

        correction.RaPulseMs.ShouldBeGreaterThan(0, "negative error should produce positive (opposing) pulse");
    }

    [Fact]
    public void GivenCorrectionWhenCheckDurationsThenPositive()
    {
        var controller = new ProportionalGuideController { AggressivenessRa = 1.0, AggressivenessDec = 1.0 };
        var cal = MakeCalibration();

        var correction = controller.Compute(cal, -2.0, 1.5);

        correction.RaPulseDuration.TotalMilliseconds.ShouldBeGreaterThan(0);
        correction.DecPulseDuration.TotalMilliseconds.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// At any camera angle, a star that drifted in the +X direction on the sensor
    /// should produce a correction that opposes the drift — i.e., the RA correction
    /// should be in the opposite sign to the RA error projection.
    ///
    /// This catches the sign bug where angle=π causes corrections to amplify the error.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(Math.PI / 4)]
    [InlineData(Math.PI / 2)]
    [InlineData(Math.PI)]
    [InlineData(-Math.PI / 2)]
    [InlineData(-Math.PI / 4)]
    public void GivenAnyAngleWhenStarDriftsPositiveXThenRaCorrectionOpposesDrift(double cameraAngleRad)
    {
        var controller = new ProportionalGuideController
        {
            AggressivenessRa = 1.0,
            AggressivenessDec = 1.0,
            MinPulseMs = 0 // disable dead zone so small corrections aren't suppressed
        };
        var cal = MakeCalibration(cameraAngleRad: cameraAngleRad);

        // Star drifted +5px in X (pure X drift, no Y)
        var correction = controller.Compute(cal, 5.0, 0);

        // The RA error in mount coordinates
        var (raErr, _) = cal.TransformToMountAxes(5.0, 0);

        // Key invariant: the correction should OPPOSE the error.
        // Positive raErr means star moved in the calibration-West direction → need East (negative pulse).
        // Negative raErr means star moved opposite to calibration-West → need West (positive pulse).
        if (Math.Abs(raErr) > 0.01) // skip when projection is near zero (e.g. angle=π/2)
        {
            // Correction sign should be opposite to error sign
            (correction.RaPulseMs * raErr).ShouldBeLessThanOrEqualTo(0,
                $"At angle={cameraAngleRad:F2}rad, raErr={raErr:F2}, but raPulseMs={correction.RaPulseMs:F2} — correction should oppose error");
        }
    }
}
