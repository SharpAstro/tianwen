using System;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The optics a synthetic capture is rendered through (docs/plans/planetary-restoration.md, R1 part 3 and R2), each against
/// the closed form it must reproduce: the pupil's open area, Kolmogorov's structure function, and a diffraction-limited
/// transfer function that ends exactly at the aperture's cutoff.
/// </summary>
public class OpticsPrimitiveTests
{
    [Fact]
    public void APupilsOpenAreaIsItsAnnulusLessItsVanes()
    {
        var pupil = new Pupil(0.254, ObstructionRatio: 0.23, Vanes: 4, VaneWidthM: 0.002, VaneAngleDeg: 20);
        const double spacing = 0.0015;

        var raster = pupil.Rasterise(256, spacing);

        var open = 0.0;
        foreach (var t in raster)
        {
            open += t;
        }
        var area = open * spacing * spacing;
        var outer = 0.254 / 2;
        var inner = outer * 0.23;
        var expected = (Math.PI * ((outer * outer) - (inner * inner))) - (4 * 0.002 * (outer - inner));
        area.ShouldBe(expected, expected * 0.005);
    }

    [Fact]
    public void AScreensStructureFunctionIsKolmogorovsWellInsideTheScreen()
    {
        const int n = 256;
        const double spacing = 0.01;
        const double r0 = 0.1;
        var random = new Random(7);
        var phase = new double[n * n];
        var scratch = new Complex[n * n];
        int[] separations = [4, 8];
        var sums = new double[separations.Length];
        var counts = new long[separations.Length];
        for (var screen = 0; screen < 20; screen++)
        {
            PhaseScreen.Kolmogorov(phase, n, spacing, r0, random, scratch);
            for (var s = 0; s < separations.Length; s++)
            {
                var d = separations[s];
                for (var y = 0; y < n - d; y++)
                {
                    for (var x = 0; x < n - d; x++)
                    {
                        var p = phase[(y * n) + x];
                        var dx = phase[(y * n) + x + d] - p;
                        var dy = phase[((y + d) * n) + x] - p;
                        sums[s] += (dx * dx) + (dy * dy);
                        counts[s] += 2;
                    }
                }
            }
        }

        for (var s = 0; s < separations.Length; s++)
        {
            var measured = sums[s] / counts[s];
            var expected = 6.88 * Math.Pow(separations[s] * spacing / r0, 5.0 / 3.0);
            TestContext.Current.TestOutputHelper?.WriteLine($"r = {separations[s] * spacing:0.00} m: D = {measured:0.000} rad^2, Kolmogorov {expected:0.000}");
            measured.ShouldBe(expected, expected * 0.15);
        }
    }

    [Fact]
    public void ADiffractionLimitedTransferFunctionEndsAtTheApertureCutoff()
    {
        // A 40-sample pupil on a 128 grid: its transfer function reaches 40 samples of frequency, and not one beyond.
        const int n = 128;
        var pupil = new Pupil(40, ObstructionRatio: 0.25).Rasterise(n, 1.0);
        var psf = new double[n * n];
        ShortExposurePsf.Compute(pupil, ReadOnlySpan<double>.Empty, n, psf);

        var otf = new Complex[n * n];
        for (var i = 0; i < psf.Length; i++)
        {
            otf[i] = psf[i];
        }
        Fft2D.Forward(otf, n, n);

        otf[0].Magnitude.ShouldBe(1.0, 1e-9, "a normalised PSF transfers the mean exactly");
        otf[37].Magnitude.ShouldBeGreaterThan(1e-4, "inside the cutoff");
        otf[43].Magnitude.ShouldBeLessThan(1e-9, "past the cutoff there is nothing, whatever the pupil's shape inside it");
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void AClearPupilsPsfHoldsAirysEncircledEnergy(int samplesAcross)
    {
        // Airy's encircled energy inside the first three dark rings (1.22, 2.23 and 3.24 lambda / D): 83.8, 91.0 and 93.8 %.
        // A pupil drawn on a grid has a staircase edge, which scatters light wide; this bounds how much, at the sampling a
        // render uses (about 46 samples across the Newtonian's pupil).
        const int n = 256;
        var pupil = new Pupil(samplesAcross).Rasterise(n, 1.0);
        var psf = new double[n * n];
        ShortExposurePsf.Compute(pupil, ReadOnlySpan<double>.Empty, n, psf);
        // One PSF sample is lambda / (n spacing) radians, so lambda / D is n / samplesAcross samples.
        var lambdaOverD = (double)n / samplesAcross;
        double[] rings = [1.22, 2.23, 3.24];
        double[] airy = [0.838, 0.910, 0.938];
        for (var r = 0; r < rings.Length; r++)
        {
            var radius = rings[r] * lambdaOverD;
            var inside = 0.0;
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var dx = x - (n / 2);
                    var dy = y - (n / 2);
                    if ((dx * dx) + (dy * dy) <= radius * radius)
                    {
                        inside += psf[(y * n) + x];
                    }
                }
            }
            TestContext.Current.TestOutputHelper?.WriteLine($"{samplesAcross} samples across: inside {rings[r]} lambda/D {inside:0.0000} (Airy {airy[r]:0.000})");
            // Never short of Airy's (light scattered wide would be), and over it by at most what summing samples instead of
            // integrating a peaked profile adds: 1.7 % at 8 samples a lambda / D, 0.9 % at 4.
            inside.ShouldBeGreaterThan(airy[r] - 0.003);
            inside.ShouldBeLessThan(airy[r] + 0.02);
        }
    }
}
