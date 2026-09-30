using System;
using System.Collections.Immutable;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The spectral ratio (docs/plans/planetary-restoration.md, R7 part 1): a still object moved about reads one at every frequency, the
/// noise taken out of both halves; the seeing model's ratio is one with no air and falls as the air strengthens; and the fit finds
/// the r0 a ratio was made at.
/// </summary>
public class PlanetarySpectralRatioTests
{
    private const double Scale = 0.4974;
    private static readonly DegradeOptions Seeing = new DegradeOptions(new Pupil(0.254, ObstructionRatio: 0.23, Vanes: 4, VaneWidthM: 0.001), 650e-9)
    {
        R0M = 0.1,
        WindMps = 22,
        OuterScaleM = 4,
    };

    [Fact]
    public void AStillObjectMovedAboutReadsOneAtEveryFrequencyWithTheNoiseTakenOut()
    {
        const int size = 64;
        var camera = new CameraNoise(OffsetAdu: 10, ElectronsPerAdu: 1e9, ReadNoiseAdu: 0.5);
        var sigma = Math.Sqrt(camera.VarianceAt(10));
        var random = new Random(3);
        var blobs = Enumerable.Range(0, 40).Select(_ => (X: 32 + (random.NextDouble() * 24) - 12, Y: 32 + (random.NextDouble() * 24) - 12, A: 5 + (random.NextDouble() * 20))).ToArray();
        // A disk of texture on a flat sky, well inside the window's flat middle, sampled wherever the frame's shift puts it.
        double Object(double x, double y) => 10 + blobs.Sum(b => b.A * Math.Exp(-(((x - b.X) * (x - b.X)) + ((y - b.Y) * (y - b.Y))) / 4.5));
        var ratio = new PlanetarySpectralRatio(size);
        var window = new float[size * size];
        for (var frame = 0; frame < 200; frame++)
        {
            var (sx, sy) = ((random.NextDouble() * 0.8) - 0.4, (random.NextDouble() * 0.8) - 0.4);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // The frame shows the object moved by (sx, sy), with white noise of the camera's variance.
                    window[(y * size) + x] = (float)(Object(x - sx, y - sy) + (sigma * Gaussian(random)));
                }
            }
            ratio.Add(window, sx, sy, camera);
        }
        var rings = ratio.Rings().Where(r => r.CyclesPerPixel is > 0.03 and < 0.25 && r.PowerOverNoise > 4).ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(", ", rings.Select(r => $"{r.CyclesPerPixel:0.000}: {r.Ratio:0.000} ({r.PowerOverNoise:0})")));
        rings.Length.ShouldBeGreaterThan(5);
        foreach (var ring in rings)
        {
            ring.Ratio.ShouldBe(1, 0.05);
        }
    }

    [Fact]
    public void TheSeeingModelsRatioIsOneWithNoAirAndFallsAsTheAirStrengthens()
    {
        var still = PlanetarySpectralRatio.Theory(Seeing, Scale, freeAirScale: 0, exposures: 20);
        foreach (var (f, value) in still.Where(r => r.CyclesPerPixel is > 0 and < 0.5))
        {
            value.ShouldBe(1, 1e-6, $"at {f:0.000} cycles a pixel");
        }
        double At(double r0M, double f) => PlanetarySpectralRatio.Theory(Seeing, Scale, Math.Pow(Seeing.R0M / r0M, 5.0 / 6), exposures: 100)
            .MinBy(r => Math.Abs(r.CyclesPerPixel - f)).Ratio;
        var (weak, strong) = (At(0.15, 0.2), At(0.05, 0.2));
        TestContext.Current.TestOutputHelper?.WriteLine($"at 0.2 cycles a pixel: r0 15 cm {weak:0.000}, 5 cm {strong:0.000}");
        strong.ShouldBeLessThan(weak * 0.8);
        weak.ShouldBeLessThan(1);
    }

    [Fact]
    public void TheFitFindsTheR0ARatioWasMadeAt()
    {
        // A ratio made at 9 cm from other draws than the fit's own, read as a capture's rings would be.
        var made = PlanetarySpectralRatio.Theory(Seeing with { Seed = 7 }, Scale, Math.Pow(Seeing.R0M / 0.09, 5.0 / 6), exposures: 200);
        var measured = made.Where(r => r.CyclesPerPixel is > 0 and <= 0.5 && r.Ratio > 0)
            .Select(r => new SpectralRatioRing(r.CyclesPerPixel, r.Ratio, PowerOverNoise: 100, Samples: 1)).ToImmutableArray();
        var fit = PlanetarySpectralRatio.Fit(measured, Seeing, Scale, minR0M: 0.05, maxR0M: 0.16, grid: 13, exposures: 150).ShouldNotBeNull();
        TestContext.Current.TestOutputHelper?.WriteLine($"fitted r0 {fit.R0M * 100:0.00} cm, log RMS {fit.LogRms:0.000} over {fit.Rings.Length} rings");
        fit.R0M.ShouldBe(0.09, 0.009);
    }

    private static double Gaussian(Random random)
        => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
