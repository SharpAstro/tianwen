using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The twin's calibration search (<see cref="PlanetaryTwinCalibration"/>, docs/plans/planetary-stacking.md, A1) against a twin whose
/// statistics are known functions of its knobs, so the air that made the real capture is known: each fitted statistic a power of the knobs
/// it depends on, as the physics has them (the limb's edge narrows with the free air's r0 and the still layer's, the disk's motion grows as
/// r0 to the minus five sixths, the wind moves the aligner's error and the frame-to-frame flux, the scatter lifts the halo by its share and
/// spreads it by its core). The search must find that air from R2's hand calibration of another night.
/// </summary>
public class PlanetaryTwinCalibrationTests
{
    private static FrameLimb Limb(int frame) => new FrameLimb(frame, 0.03 + (frame * 1e-5), 6.9 + (frame * 1e-4), new LimbFit(
        158.89 + (frame * 1e-3), 161.31, 119.34, 22.31, 0.989, 1.666, 0.671, 0.0062, 1, 0.0123, 39573,
        [.. Enumerable.Repeat(double.NaN, 13)], 12, true, 0.202, -0.474, 0.0022, 202.31, 0.4995, 6.53));

    private static CaptureStatistics Real() => new CaptureStatistics(
        160, 29.9, 7, 158.6, 158.8, 114.6, [], [], [], [], 0.35, 0.01, 0.02, [], 0.023, 0.0026, 14.0, 13.1,
        null, null, [.. Enumerable.Range(0, 40).Select(i => Limb(i * 4))], 0.36, 1.1, 0.05, 0, [0.38, 0.16, 0.021, double.NaN],
        new WarpStatistics(163, 1.06, 3, WarpLengthBound.AtMost, double.NaN, []), [], [0.97, 0.99, 1, 1.02, 1.06], 0.84,
        [new BandNoise(1, 0.57, 4.87)], new CameraEstimate(255, 1.25, 0.65, 113, 1.09, 0.54, 1.09));

    // The twin the knobs make, given the air that made the real capture.
    private static CaptureStatistics Twin(CaptureStatistics real, TwinKnobs k, TwinKnobs air) => real with
    {
        LimbWidthBest = real.LimbWidthBest * Math.Pow(air.R0M / k.R0M, 0.5),
        LimbWidthAll = real.LimbWidthAll * Math.Pow(air.R0M / k.R0M, 0.2) * Math.Pow(air.LocalR0M / k.LocalR0M, 0.3),
        LimbSeeingRms = real.LimbSeeingRms * Math.Pow(air.R0M / k.R0M, 5.0 / 6),
        AlignerErrorRms = real.AlignerErrorRms * Math.Pow(air.WindMps / k.WindMps, 0.3),
        FluxFastRms = real.FluxFastRms * Math.Pow(k.WindMps / air.WindMps, 0.5),
        Halo = [.. real.Halo.Select((h, j) => h * (k.ScatterFraction / air.ScatterFraction) * Math.Pow(k.ScatterCoreArcsec / air.ScatterCoreArcsec, j - 1.0))],
    };

    [Fact(Timeout = 60_000)]
    public async Task TheSearchFindsTheAirThatMadeTheCapture()
    {
        var real = Real();
        var air = new TwinKnobs(0.12, 15, 0.04, 0.03, 7);
        var (best, trials) = await PlanetaryTwinCalibration.FitAsync(real, TwinKnobs.HandCalibratedRed,
            (k, _) => Task.FromResult<CaptureStatistics?>(Twin(real, k, air)), maxTrials: 300, cancellationToken: TestContext.Current.CancellationToken);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{trials.Length} trials, mismatch {best.Mismatch:G3}: r0 {best.Knobs.R0M:0.0000}, wind {best.Knobs.WindMps:0.00}, still r0 {best.Knobs.LocalR0M:0.0000}, scatter {best.Knobs.ScatterFraction:0.0000} core {best.Knobs.ScatterCoreArcsec:0.00}");
        // The search stops at a match to about a percent a statistic, which pins each knob only as well as the statistics move with it:
        // the still layer's r0 moves the limb's width as its 0.3 power, so a percent there leaves it to several (it landed 6.7 % off),
        // where the free air's r0, read through three statistics, lands within two.
        best.Mismatch.ShouldBeLessThan(1e-4);
        best.Knobs.R0M.ShouldBe(air.R0M, 0.05 * air.R0M);
        best.Knobs.WindMps.ShouldBe(air.WindMps, 0.05 * air.WindMps);
        best.Knobs.LocalR0M.ShouldBe(air.LocalR0M, 0.1 * air.LocalR0M);
        best.Knobs.ScatterFraction.ShouldBe(air.ScatterFraction, 0.05 * air.ScatterFraction);
        best.Knobs.ScatterCoreArcsec.ShouldBe(air.ScatterCoreArcsec, 0.05 * air.ScatterCoreArcsec);
        trials.Length.ShouldBeLessThanOrEqualTo(300);
    }

    [Fact(Timeout = 60_000)]
    public async Task ATwinThatCouldNotBeMeasuredScoresWorstAndTheSearchGoesOn()
    {
        var real = Real();
        var air = new TwinKnobs(0.1, 20, 0.03, 0.05, 5);
        // Any twin whose free air is sharper than 0.15 cm has no disk to measure: the search must step away from it, not stop.
        var (best, trials) = await PlanetaryTwinCalibration.FitAsync(real, TwinKnobs.HandCalibratedRed,
            (k, _) => Task.FromResult(k.R0M > 0.15 ? null : (CaptureStatistics?)Twin(real, k, air)), maxTrials: 60,
            cancellationToken: TestContext.Current.CancellationToken);

        trials.ShouldContain(t => double.IsPositiveInfinity(t.Mismatch) || t.Knobs.R0M <= 0.15);
        double.IsFinite(best.Mismatch).ShouldBeTrue();
        best.Mismatch.ShouldBeLessThan(trials[0].Mismatch);
    }

    [Fact]
    public void KnobsGoIntoTheOptionsAndBackUnchanged()
    {
        var options = new DegradeOptions(new TianWen.Lib.Imaging.Optics.Pupil(0.28, ObstructionRatio: 0.375), 650e-9);
        var knobs = new TwinKnobs(0.085, 22, 0.027, 0.05, 5);
        TwinKnobs.From(knobs.ApplyTo(options)).ShouldBe(knobs);
        // No still layer and no scatter read as the bounds the search starts from.
        var none = TwinKnobs.From(options);
        (none.LocalR0M, none.ScatterFraction).ShouldBe((TwinKnobs.Upper.LocalR0M, TwinKnobs.Lower.ScatterFraction));
    }
}
