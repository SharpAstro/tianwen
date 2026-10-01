using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R7's inverse (docs/plans/planetary-restoration.md, R7 part 4): a stack's transfer against its truth is the blur it was given, a
/// clear pupil's diffraction transfer is the textbook one, and Richardson-Lucy with the right transfer brings a blurred object back
/// toward itself.
/// </summary>
public class PlanetaryInverseTests
{
    private const int Size = 128;
    private static readonly MetricDisk Disk = new(63.5, 63.5, 44);

    private static float[] Texture()
    {
        var random = new Random(11);
        var blobs = Enumerable.Range(0, 150).Select(_ => (X: 63.5 + (random.NextDouble() * 70) - 35, Y: 63.5 + (random.NextDouble() * 70) - 35, A: (random.NextDouble() * 0.6) - 0.3, S: 0.8 + (random.NextDouble() * 2.5))).ToArray();
        var plane = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r = Math.Sqrt(((x - 63.5) * (x - 63.5)) + ((y - 63.5) * (y - 63.5)));
                var v = 1.0;
                foreach (var b in blobs)
                {
                    v += b.A * Math.Exp(-(((x - b.X) * (x - b.X)) + ((y - b.Y) * (y - b.Y))) / (2 * b.S * b.S));
                }
                plane[(y * Size) + x] = (float)(r < 48 ? v : 0);
            }
        }
        return plane;
    }

    private static double Gaussian(double sigma, double f) => Math.Exp(-2 * Math.PI * Math.PI * sigma * sigma * f * f);

    [Fact]
    public void AStacksTransferAgainstItsTruthIsTheBlurItWasGiven()
    {
        var truth = Texture();
        var stack = PlanetaryInverse.Apply(truth, Size, Size, f => Gaussian(1.3, f));
        var measured = PlanetaryInverse.Measure(stack, truth, Size, Size);
        foreach (var f in new[] { 0.05, 0.1, 0.2, 0.3 })
        {
            measured.At(f).ShouldBe(Gaussian(1.3, f), 0.02, $"at {f} cycles a pixel");
        }
    }

    [Fact]
    public void AClearPupilsDiffractionIsTheTextbooksTransfer()
    {
        // At half the cutoff a clear circular aperture passes (2 / pi) (acos(0.5) - 0.5 sqrt(0.75)) = 0.391.
        var pupil = new Pupil(0.254);
        const double wavelength = 650e-9, scale = 0.25;
        var cutoff = pupil.DiameterM / wavelength / ShortExposurePsf.ArcsecPerRadian * scale;
        var diffraction = PlanetaryInverse.Diffraction(pupil, wavelength, scale);
        TestContext.Current.TestOutputHelper?.WriteLine($"cutoff {cutoff:0.000} cycles a pixel; at half of it {diffraction.At(cutoff / 2):0.000}");
        diffraction.At(0).ShouldBe(1, 1e-9);
        diffraction.At(cutoff / 2).ShouldBe(0.391, 0.03);
        diffraction.At(cutoff * 1.05).ShouldBe(0, 0.02);
    }

    [Fact]
    public void RichardsonLucyWithTheRightTransferBringsABlurredObjectBack()
    {
        var truth = Texture();
        var blurred = PlanetaryInverse.Apply(truth, Size, Size, f => Gaussian(1.2, f));
        var before = PlanetaryMetrics.Fidelity(blurred, truth, Size, Size, Disk).Select(f => f.Transfer).ToArray();
        float[]? last = null;
        PlanetaryInverse.RichardsonLucy(blurred, Size, Size, f => Gaussian(1.2, f), 30, (_, plane) => last = plane);
        var after = PlanetaryMetrics.Fidelity(last.ShouldNotBeNull(), truth, Size, Size, Disk).Select(f => f.Transfer).ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(", ", before.Select((t, b) => $"band {b + 1}: {t:0.000} to {after[b]:0.000}")));
        after[0].ShouldBeGreaterThan(before[0] + 0.1);
        after[1].ShouldBeGreaterThan(before[1] + 0.1);
        after[1].ShouldBeLessThan(1.1);
    }
}
