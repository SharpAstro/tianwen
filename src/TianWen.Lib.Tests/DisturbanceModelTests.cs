using Shouldly;
using System;
using TianWen.Lib.Devices.Fake.Disturbance;
using TianWen.Lib.Devices.Fake.Disturbance.Terms;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for the pure disturbance subsystem (terms + <see cref="DisturbanceModel"/> +
/// <see cref="CorrectionActuator"/>). No fakes, no time provider -- terms are deterministic
/// functions of elapsed time and (for stochastic terms) a fixed seed.
/// </summary>
public class DisturbanceModelTests
{
    private static DisturbanceContext Ctx(double elapsedSeconds, double wormPhase = double.NaN)
        => new(elapsedSeconds, wormPhase);

    [Fact]
    public void GivenPositionalWormPhaseWhenPeAtQuarterTurnThenPeakRaAndZeroDec()
    {
        var pe = new PeriodicErrorTerm(peakToPeakArcsec: 20.0, periodSeconds: 600.0);

        var (dRa, dDec) = pe.Evaluate(Ctx(elapsedSeconds: 999.0, wormPhase: Math.PI / 2.0));

        dRa.ShouldBe(10.0, 1e-9); // peak = peak-to-peak / 2; sin(pi/2) = 1; worm phase wins over elapsed
        dDec.ShouldBe(0.0);
    }

    [Fact]
    public void GivenNoEncoderWhenPeThenFallsBackToWallClockSine()
    {
        var pe = new PeriodicErrorTerm(peakToPeakArcsec: 20.0, periodSeconds: 600.0);

        // No worm phase -> time-based: quarter period elapsed -> sin(pi/2) = 1 -> peak.
        var (dRa, _) = pe.Evaluate(Ctx(elapsedSeconds: 150.0, wormPhase: double.NaN));

        dRa.ShouldBe(10.0, 1e-9);
    }

    [Fact]
    public void GivenFlexureWhenTrackingThenDecDriftsLinearlyInElapsed()
    {
        var flexure = new FlexureTerm(decArcsecPerHaHour: 5.0);

        var (ra1, dec1) = flexure.Evaluate(Ctx(3600.0));
        var (_, dec2) = flexure.Evaluate(Ctx(7200.0));

        ra1.ShouldBe(0.0);
        dec1.ShouldBeGreaterThan(0.0);
        dec2.ShouldBe(dec1 * 2.0, 1e-9); // linear in elapsed
    }

    [Fact]
    public void GivenLinearDriftWhenTrackingThenBothAxesDriftLinearlyInElapsed()
    {
        var drift = new LinearDriftTerm(raArcsecPerSec: 0.2, decArcsecPerSec: 0.5);

        var (ra1, dec1) = drift.Evaluate(Ctx(10.0));
        var (ra2, dec2) = drift.Evaluate(Ctx(20.0));

        ra1.ShouldBe(2.0, 1e-9);  // 0.2"/s * 10s
        dec1.ShouldBe(5.0, 1e-9); // 0.5"/s * 10s
        ra2.ShouldBe(ra1 * 2.0, 1e-9);   // linear in elapsed
        dec2.ShouldBe(dec1 * 2.0, 1e-9);
    }

    [Fact]
    public void GivenCableSnagWhenBeforeAndAfterTriggerThenZeroThenStep()
    {
        var snag = new CableSnagTerm(atSeconds: 20.0, raArcsec: 8.0, decArcsec: -4.0);

        snag.Evaluate(Ctx(10.0)).ShouldBe((0.0, 0.0));
        snag.Evaluate(Ctx(25.0)).ShouldBe((8.0, -4.0));
    }

    [Fact]
    public void GivenWindWhenAdvancedThenDeterministicBoundedAndIdempotentAtSameInstant()
    {
        var a = new WindGustTerm(amplitudeArcsec: 2.0, decayTimeSeconds: 10.0, seed: 23);
        var b = new WindGustTerm(amplitudeArcsec: 2.0, decayTimeSeconds: 10.0, seed: 23);

        for (var t = 2.0; t <= 200.0; t += 2.0)
        {
            var va = a.Evaluate(Ctx(t));
            var vb = b.Evaluate(Ctx(t));

            vb.ShouldBe(va); // same seed + same elapsed sequence -> identical
            Math.Abs(va.DRaArcsec).ShouldBeLessThan(16.0); // stationary sd ~ amplitude; 8x is unreachable
            Math.Abs(va.DDecArcsec).ShouldBeLessThan(16.0);
        }

        // Idempotent: re-evaluating at the SAME elapsed does not step the process.
        var v1 = a.Evaluate(Ctx(200.0));
        var v2 = a.Evaluate(Ctx(200.0));
        v2.ShouldBe(v1);
    }

    [Fact]
    public void GivenWindWhenResetThenStateZeroesAndSequenceRepeats()
    {
        var wind = new WindGustTerm(amplitudeArcsec: 2.0, decayTimeSeconds: 10.0, seed: 23);

        var first = wind.Evaluate(Ctx(4.0));
        first.ShouldNotBe((0.0, 0.0));

        wind.Reset();
        wind.Evaluate(Ctx(0.0)).ShouldBe((0.0, 0.0)); // back to rest
        wind.Evaluate(Ctx(4.0)).ShouldBe(first);      // identical replay after reset
    }

    [Fact]
    public void GivenSeeingWhenSummedThenAppearsInSensorDeltaNotPointingDelta()
    {
        var model = new DisturbanceModel([new AtmosphericSeeingTerm(seeingArcsec: 1.5, seed: 7)]);

        model.PointingDelta(Ctx(2.0)).ShouldBe((0.0, 0.0)); // atmosphere is not a pointing term
        var sensor = model.SensorDelta(Ctx(2.0));
        (sensor.DRaArcsec != 0.0 || sensor.DDecArcsec != 0.0).ShouldBeTrue();
    }

    [Fact]
    public void GivenMixedModelWhenPointingDeltaThenSumsOnlyNonAtmosphereTerms()
    {
        var pe = new PeriodicErrorTerm(peakToPeakArcsec: 20.0, periodSeconds: 600.0);
        var flexure = new FlexureTerm(decArcsecPerHaHour: 5.0);
        var seeing = new AtmosphericSeeingTerm(seeingArcsec: 1.5, seed: 7);
        var model = new DisturbanceModel([pe, flexure, seeing]);

        var ctx = Ctx(3600.0, wormPhase: Math.PI / 2.0);
        var (peRa, _) = pe.Evaluate(ctx);
        var (_, flexDec) = flexure.Evaluate(ctx);

        var pointing = model.PointingDelta(ctx);
        pointing.DRaArcsec.ShouldBe(peRa, 1e-9);   // PE only; seeing excluded
        pointing.DDecArcsec.ShouldBe(flexDec, 1e-9); // flexure only; seeing excluded
    }

    [Fact]
    public void GivenMountPulseActuatorThenCorrectabilityIsDerivedFromStageAndBandwidth()
    {
        var mount = CorrectionActuator.MountPulse;

        mount.Corrects(new PeriodicErrorTerm(20.0, 600.0)).ShouldBeTrue("slow drivetrain PE is correctable");
        mount.Corrects(new FlexureTerm(5.0)).ShouldBeTrue("DC tube flexure is correctable");
        mount.Corrects(new CableSnagTerm(20.0, 8.0, -4.0)).ShouldBeTrue("a settled tube step is correctable");
        mount.Corrects(new WindGustTerm(2.0, 10.0)).ShouldBeTrue("slow wind is within the mount loop");

        mount.Corrects(new GearNoiseTerm()).ShouldBeFalse("fast gear noise is above the ~0.5 Hz mount loop");
        mount.Corrects(new AtmosphericSeeingTerm(2.0)).ShouldBeFalse("the atmosphere is upstream of the mount axis");
    }
}
