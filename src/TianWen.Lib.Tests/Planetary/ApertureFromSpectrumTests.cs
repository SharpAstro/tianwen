using System;
using Shouldly;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Which telescope took a capture, from its frames (docs/plans/planetary-restoration.md, R1 part 3, #1049), on synthetic
/// captures rendered through each pupil with Kolmogorov seeing, before any real capture is read.
/// <para>
/// A measured cutoff is where the signal sank into the noise. The first pre-registration, a cutoff within 15 % of the pupil's,
/// FAILED here: at 150 frames both pupils came back at 0.25 cycles a pixel whatever the pupil (72 and 74 % of their cutoffs),
/// and at 1,500 the Maksutov's came back at 82 %. What holds is the measured cutoff read as a LOWER bound, never above the
/// pupil's: a bound past 1.15 times the Maksutov's rules the Maksutov out, which a Newtonian's capture does and a Maksutov's
/// never can. The captures have 1,500 frames, a real capture's best tenth, and the spider must be found where it is
/// (statistic above 10, at its vanes' angle) and not where it is not (below 3).
/// </para>
/// </summary>
public class ApertureFromSpectrumTests
{
    private const double Wavelength = 550e-9;
    private const double R0 = 0.08;
    private const double Noise = 0.02;
    private const int Frames = 1500;
    private const double DiskRadius = 15;
    private const double MaksutovD = 0.102;
    private const double NewtonianD = 0.254;

    // Clear of the grid (0) and of the synthetic disk's axes (20 and 110), each of which could print a four-fold pattern of its own.
    private const double VaneAngle = 35;

    private static readonly Pupil Maksutov = new Pupil(MaksutovD, ObstructionRatio: 0.30);
    private static readonly Pupil Newtonian = new Pupil(NewtonianD, ObstructionRatio: 0.23, Vanes: 4, VaneWidthM: 0.001, VaneAngleDeg: VaneAngle);
    private static readonly Pupil NewtonianWithoutSpider = Newtonian with { Vanes = 0 };

    [Fact]
    public void AMaksutovsMeasuredCutoffIsNeverAboveItsOwnSoItNeverRulesItselfOut()
    {
        const double scale = 0.4;
        var result = Measure(Maksutov, scale, seed: 11);

        var truth = ApertureCutoff.CyclesPerPixel(MaksutovD, scale, Wavelength);
        Report("Maksutov", scale, result, truth);
        result.Cutoff.CutoffCyclesPerPixel.ShouldBeLessThanOrEqualTo(truth + (2.0 / result.Size), "past the pupil's cutoff there is nothing to find");
        ApertureCutoff.RulesOut(result.Cutoff, MaksutovD, scale, Wavelength).ShouldBeFalse();
    }

    [Fact]
    public void ANewtonianAtAPrimeFocusScaleRulesTheMaksutovOut()
    {
        // At 0.4"/px the Newtonian's cutoff, 0.9 cycles a pixel, is past the corners; the Maksutov's is at 0.36.
        const double scale = 0.4;
        var result = Measure(Newtonian, scale, seed: 13);

        Report("Newtonian", scale, result, ApertureCutoff.CyclesPerPixel(NewtonianD, scale, Wavelength));
        ApertureCutoff.RulesOut(result.Cutoff, MaksutovD, scale, Wavelength).ShouldBeTrue();
    }

    [Fact]
    public void ANewtonianSampledPastItsCutoffRulesTheMaksutovOutAndNeverItself()
    {
        const double scale = 0.15;
        var result = Measure(Newtonian, scale, seed: 12);

        var truth = ApertureCutoff.CyclesPerPixel(NewtonianD, scale, Wavelength);
        Report("Newtonian", scale, result, truth);
        ApertureCutoff.RulesOut(result.Cutoff, MaksutovD, scale, Wavelength).ShouldBeTrue();
        result.Cutoff.CutoffCyclesPerPixel.ShouldBeLessThanOrEqualTo(truth + (2.0 / result.Size));
        ApertureCutoff.RulesOut(result.Cutoff, NewtonianD, scale, Wavelength).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSpiderIsFoundWhereItIsAndNotWhereItIsNot(bool moons)
    {
        // A real capture's geometry: Jupiter's 23" radius at a prime-focus 0.4"/px in a 256 px frame, whose sky reaches 2.2
        // radii. The stack's noise is what 1,500 frames of the spectrum tests' leave.
        const double scale = 0.4;
        var withSpider = MeasureSpider(Newtonian, scale, seed: 14, moons);
        var withoutSpider = MeasureSpider(NewtonianWithoutSpider, scale, seed: 15, moons);
        var maksutov = MeasureSpider(Maksutov, scale, seed: 16, moons);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"moons {moons}: spider {withSpider.Statistic:0.0} at {withSpider.SpikeAngleDeg:0.0} deg (vanes at {VaneAngle}), obstruction only {withoutSpider.Statistic:0.00} at {withoutSpider.SpikeAngleDeg:0.0}, " +
            $"Maksutov {maksutov.Statistic:0.00} at {maksutov.SpikeAngleDeg:0.0}; annulus {withSpider.InnerRadius:0.0} to {withSpider.OuterRadius:0.0} px");
        withSpider.Statistic.ShouldBeGreaterThan(SpiderSignature.SpiderAbove);
        // A + spider's spikes run along its vanes' perpendiculars, which modulo 90 degrees are its vanes.
        AngleModulo90Difference(withSpider.SpikeAngleDeg, VaneAngle).ShouldBeLessThan(5);
        // Moons put a little power into every harmonic even through the medians: across eight noise draws a spider-less halo
        // with them read up to 7.6, inconclusive, and never near a spider's 380 and more. Without them it read 2.1 at most.
        var none = moons ? SpiderSignature.SpiderAbove : SpiderSignature.NoneBelow;
        withoutSpider.Statistic.ShouldBeLessThan(none);
        maksutov.Statistic.ShouldBeLessThan(none);
    }

    [Fact]
    public void TheDetailAMaksutovsHalfStacksShareNeverReachesPastItsCutoff()
    {
        const double scale = 0.4;
        var detail = MeasureHalves(Maksutov, scale, 23.3 / scale, seed: 21);

        var truth = ApertureCutoff.CyclesPerPixel(MaksutovD, scale, Wavelength);
        TestContext.Current.TestOutputHelper?.WriteLine($"Maksutov at {scale}\"/px: the halves share detail to {detail:0.000} c/px (cutoff {truth:0.000}), D >= {ApertureCutoff.ApertureM(detail, scale, Wavelength) * 1000:0} mm");
        detail.ShouldBeLessThanOrEqualTo(truth + (2.0 / HalfStackSize), "past the pupil's cutoff there is no detail to share");
        ApertureCutoff.RulesOut(detail, MaksutovD, scale, Wavelength).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0.4, 58.25)] // prime focus: Jupiter's 23.3" radius
    [InlineData(0.15, 80.0)] // a Barlow: a smaller disk, so it fits the frame
    public void TheDetailANewtoniansHalfStacksShareRulesTheMaksutovOutAndNeverItself(double scale, double radius)
    {
        var detail = MeasureHalves(Newtonian, scale, radius, seed: 22);

        var truth = ApertureCutoff.CyclesPerPixel(NewtonianD, scale, Wavelength);
        TestContext.Current.TestOutputHelper?.WriteLine($"Newtonian at {scale}\"/px: the halves share detail to {detail:0.000} c/px (cutoff {truth:0.000}, the Maksutov's {ApertureCutoff.CyclesPerPixel(MaksutovD, scale, Wavelength):0.000}), D >= {ApertureCutoff.ApertureM(detail, scale, Wavelength) * 1000:0} mm");
        ApertureCutoff.RulesOut(detail, MaksutovD, scale, Wavelength).ShouldBeTrue();
        detail.ShouldBeLessThanOrEqualTo(truth + (2.0 / HalfStackSize));
        ApertureCutoff.RulesOut(detail, NewtonianD, scale, Wavelength).ShouldBeFalse();
    }

    private const int HalfStackSize = 256;

    // Two half-stacks of the best frames are the ensemble mean with the noise of half the frames each, independent between the
    // halves: two renders of the mean with different noise draws.
    private static double MeasureHalves(Pupil pupil, double scale, double radius, int seed)
    {
        var noise = Noise / Math.Sqrt(Frames / 2.0);
        var a = SyntheticSeeingCapture.RenderStack(pupil, scale, Wavelength, R0, radius, HalfStackSize, noise, seed);
        var b = SyntheticSeeingCapture.RenderStack(pupil, scale, Wavelength, R0, radius, HalfStackSize, noise, seed + 1000);
        return ApertureCutoff.MeasureCross(new PlanetaryPowerSpectrum(HalfStackSize, HalfStackSize).Cross(a, b));
    }

    private static void Report(string name, double scale, Result result, double truth)
        => TestContext.Current.TestOutputHelper?.WriteLine(
            $"{name} at {scale}\"/px, {Frames} frames: cutoff {result.Cutoff.CutoffCyclesPerPixel:0.000} c/px (truth {truth:0.000}, Maksutov's {ApertureCutoff.CyclesPerPixel(MaksutovD, scale, Wavelength):0.000}), " +
            $"lower bound {ApertureCutoff.ApertureM(result.Cutoff.CutoffCyclesPerPixel, scale, Wavelength) * 1000:0} mm, corners {result.Cutoff.CornerSlopeSigmas:+0.0;-0.0} sigma, floor {result.Cutoff.Floor:0.0e0}");

    private static double AngleModulo90Difference(double a, double b)
    {
        var d = Math.Abs(((a - b) % 90 + 90) % 90);
        return Math.Min(d, 90 - d);
    }

    private sealed record Result(int Size, CutoffMeasurement Cutoff);

    private static Result Measure(Pupil pupil, double scale, int seed)
    {
        const int size = SyntheticSeeingCapture.FrameSize;
        const int pixels = size * size;
        var frames = SyntheticSeeingCapture.Render(pupil, scale, Wavelength, R0, Noise, DiskRadius, seed, Frames);
        var spectrum = new PlanetaryPowerSpectrum(size, size);
        for (var i = 0; i < Frames; i++)
        {
            spectrum.Add(frames.AsSpan(i * pixels, pixels));
        }
        var cutoff = ApertureCutoff.Measure(spectrum.Rings()) ?? throw new InvalidOperationException("no corners");
        return new Result(spectrum.Size, cutoff);
    }

    // Optionally with three moons in the annulus, as 2022-09-03 had: bright compact points that put power into every harmonic,
    // and which a plain mean profile let swamp a spider plain to the eye.
    private static SpiderMeasurement MeasureSpider(Pupil pupil, double scale, int seed, bool moons)
    {
        const int size = 256;
        var radius = 23.3 / scale;
        var stack = SyntheticSeeingCapture.RenderStack(pupil, scale, Wavelength, R0, radius, size, Noise / Math.Sqrt(Frames), seed);
        var (cx, cy) = SyntheticSeeingCapture.StackCenter(size);
        foreach (var (radii, angleDeg) in moons ? new[] { (1.55, 200.0), (1.8, 20.0), (2.05, 115.0) } : [])
        {
            var (sin, cos) = Math.SinCos(angleDeg * Math.PI / 180);
            AddMoon(stack, size, cx + (radii * radius * cos), cy + (radii * radius * sin), peak: 0.3, sigma: 1.5);
        }
        // The synthetic disk's shape and pole (SyntheticSeeingCapture.RenderObject: its equator at 20 degrees, its pole at 110), as
        // a limb fit reports them.
        return SpiderSignature.Measure(stack, size, size, cx, cy, radius, axisRatio: 0.935, axisAngleDeg: 110) ?? throw new InvalidOperationException("no annulus");
    }

    private static void AddMoon(float[] image, int size, double x0, double y0, double peak, double sigma)
    {
        for (var y = (int)(y0 - (5 * sigma)); y <= (int)(y0 + (5 * sigma)); y++)
        {
            for (var x = (int)(x0 - (5 * sigma)); x <= (int)(x0 + (5 * sigma)); x++)
            {
                if (x >= 0 && y >= 0 && x < size && y < size)
                {
                    var r2 = ((x - x0) * (x - x0)) + ((y - y0) * (y - y0));
                    image[(y * size) + x] += (float)(peak * Math.Exp(-r2 / (2 * sigma * sigma)));
                }
            }
        }
    }
}
