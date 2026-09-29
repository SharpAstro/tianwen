using System;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The averaged power spectrum's own contract (docs/plans/planetary-restoration.md, R1 part 3): white noise of variance
/// sigma squared reads sigma squared in every ring, whatever the frame's size or taper, and a spectrum of noise alone has no
/// cutoff and no fall through its corners.
/// </summary>
public class PlanetaryPowerSpectrumTests
{
    [Theory]
    [InlineData(64, 64)]
    [InlineData(160, 120)]
    public void WhiteNoiseReadsItsVarianceAndNoCutoff(int width, int height)
    {
        const double sigma = 0.05;
        var random = new Random(3);
        var spectrum = new PlanetaryPowerSpectrum(width, height);
        var plane = new float[width * height];
        for (var frame = 0; frame < 200; frame++)
        {
            for (var i = 0; i < plane.Length; i++)
            {
                var u1 = 1.0 - random.NextDouble();
                var u2 = random.NextDouble();
                plane[i] = (float)(0.3 + (sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2)));
            }
            spectrum.Add(plane);
        }

        var rings = spectrum.Rings();
        var cutoff = ApertureCutoff.Measure(rings).ShouldNotBeNull();

        cutoff.Floor.ShouldBe(sigma * sigma, sigma * sigma * 0.03, "white noise's power per sample is its variance");
        cutoff.BeyondCorners.ShouldBeFalse();
        // Two neighbouring rings both past 3 standard errors happen by chance about once in 500,000 pairs.
        cutoff.CutoffCyclesPerPixel.ShouldBe(0);
        foreach (var ring in rings)
        {
            if (ring.CyclesPerPixel > 0.05)
            {
                ring.Power.ShouldBe(sigma * sigma, sigma * sigma * 0.15, $"ring at {ring.CyclesPerPixel:0.000}");
            }
        }
    }
}
