using Shouldly;
using System;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Guider")]
public class ProportionalGuideControllerTests
{
    private static GuiderCalibrationResult MakeCalibration(
        double cameraAngleRad = 0,
        double raRatePixPerSec = 5.0,
        double decRatePixPerSec = 5.0,
        double decAngleRad = double.NaN)
    {
        return new GuiderCalibrationResult(
            CameraAngleRad: cameraAngleRad,
            // Default: Dec orthogonal at RA + 90deg (matches the classic transform); pass an explicit
            // angle to exercise the measured / non-orthogonal path.
            DecAngleRad: double.IsNaN(decAngleRad) ? cameraAngleRad + Math.PI / 2.0 : decAngleRad,
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

    /// <summary>
    /// Regression for the Dec runaway: the Dec correction direction must follow the MEASURED Dec
    /// axis sense. On a sensor where North is clockwise (-90deg) from West -- the southern-hemisphere
    /// / flipped-sensor case -- a Dec error must pulse the OPPOSITE way to an orthogonal +90deg sensor,
    /// else the correction amplifies the error (errDec -31 -> -92px in the field). With the old fixed
    /// +90deg transform both produced the same (wrong-for-the-south) sign.
    /// </summary>
    [Fact]
    public void GivenFlippedDecSenseWhenComputeThenDecCorrectionReverses()
    {
        var controller = new ProportionalGuideController { AggressivenessDec = 1.0, MinPulseMs = 0 };
        var northCcwCal = MakeCalibration(cameraAngleRad: 0, decAngleRad: Math.PI / 2.0);   // North +90 (CCW)
        var northCwCal = MakeCalibration(cameraAngleRad: 0, decAngleRad: -Math.PI / 2.0);   // North -90 (CW)

        // Pure +Y pixel drift.
        var ccwCorr = controller.Compute(northCcwCal, 0, 5.0);
        var cwCorr = controller.Compute(northCwCal, 0, 5.0);

        ccwCorr.DecPulseMs.ShouldNotBe(0);
        cwCorr.DecPulseMs.ShouldNotBe(0);
        // Same physical drift, opposite measured Dec sense => opposite Dec pulse direction.
        Math.Sign(ccwCorr.DecPulseMs).ShouldBe(-Math.Sign(cwCorr.DecPulseMs));
        // RA is unaffected by the Dec-axis sense for a pure-Y drift on an aligned camera.
        ccwCorr.RaPulseMs.ShouldBe(cwCorr.RaPulseMs, 1e-9);
    }

    /// <summary>
    /// The 2-axis transform must reduce to the classic rotation when Dec is exactly +90deg from RA,
    /// so existing orthogonal calibrations are byte-for-byte unchanged.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(-1.2)]
    public void GivenOrthogonalDecWhenTransformThenMatchesClassicRotation(double cameraAngleRad)
    {
        var cal = MakeCalibration(cameraAngleRad: cameraAngleRad, decAngleRad: cameraAngleRad + Math.PI / 2.0);
        var (raPx, decPx) = cal.TransformToMountAxes(3.0, -2.0);

        var cos = Math.Cos(cameraAngleRad);
        var sin = Math.Sin(cameraAngleRad);
        var expectedRa = 3.0 * cos + -2.0 * sin;
        var expectedDec = -3.0 * sin + -2.0 * cos;

        raPx.ShouldBe(expectedRa, 1e-9);
        decPx.ShouldBe(expectedDec, 1e-9);
    }
}
