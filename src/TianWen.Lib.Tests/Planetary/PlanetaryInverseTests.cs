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

    // O'Neill (1956): the transfer of an annular aperture of obstruction eps at v, a fraction of the cutoff (the clear term A, the
    // obstruction's own B, and their overlap C, over the annulus's area).
    private static double ONeill(double v, double eps)
    {
        static double Overlap(double u) => (2 / Math.PI) * (Math.Acos(u) - (u * Math.Sqrt(1 - (u * u))));
        var a = Overlap(v);
        var b = v <= eps ? eps * eps * Overlap(v / eps) : 0;
        double c;
        if (v <= (1 - eps) / 2)
        {
            c = -2 * eps * eps;
        }
        else if (v <= (1 + eps) / 2)
        {
            var phi = Math.Acos((1 + (eps * eps) - (4 * v * v)) / (2 * eps));
            c = (-2 * eps * eps) + (2 * eps / Math.PI * Math.Sin(phi)) + ((1 + (eps * eps)) / Math.PI * phi)
                - (2 * (1 - (eps * eps)) / Math.PI * Math.Atan((1 + eps) / (1 - eps) * Math.Tan(phi / 2)));
        }
        else
        {
            c = 0;
        }
        return (a + b + c) / (1 - (eps * eps));
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.2)]
    public void AnObstructedPupilsDiffractionIsONeillsTransfer(double scale)
    {
        // An obstruction of 0.23 passes 0.612 at a quarter of the cutoff and 0.360 at half, against a clear aperture's 0.685 and 0.391:
        // the transfers whose inverses, 1.6 and 2.8, the sharpening's docs quote for a 23 % obstructed pupil (#1398). At half the cutoff
        // the two pupils are 0.031 apart, so the bound is far tighter than the clear pupil's test above. Read on a window's reach, as the
        // sharpening reads it (PlanetaryLiveLimb.Diffraction): the default 128-sample grid reads both pupils about 0.009 high at a quarter.
        ONeill(0.25, 0.23).ShouldBe(0.612, 0.001);
        ONeill(0.5, 0.23).ShouldBe(0.360, 0.001);
        ONeill(0.5, 0).ShouldBe(0.391, 0.001);
        var pupil = new Pupil(0.254, ObstructionRatio: 0.23);
        const double wavelength = 650e-9;
        var cutoff = pupil.DiameterM / wavelength / ShortExposurePsf.ArcsecPerRadian * scale;
        var diffraction = PlanetaryInverse.Diffraction(pupil, wavelength, scale, reachPx: 256);
        foreach (var v in new[] { 0.125, 0.25, 0.375, 0.5, 0.75 })
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"at {v} of the cutoff ({cutoff:0.000} cycles a pixel): {diffraction.At(v * cutoff):0.0000} against O'Neill's {ONeill(v, 0.23):0.0000}");
            diffraction.At(v * cutoff).ShouldBe(ONeill(v, 0.23), 0.005, $"at {v} of the cutoff");
        }
    }

    [Fact]
    public void TheWhiteNoiseFloorAndThePowerLawPriorAreReadBack()
    {
        // A field whose power falls as f^-3, blurred by a Gaussian, with white noise of a known spread.
        var random = new Random(9);
        var spectrum = new System.Numerics.Complex[Size * Size];
        for (var ky = 0; ky < Size; ky++)
        {
            var fy = (ky < Size / 2 ? ky : ky - Size) / (double)Size;
            for (var kx = 0; kx < Size; kx++)
            {
                var fx = (kx < Size / 2 ? kx : kx - Size) / (double)Size;
                var f = Math.Sqrt((fx * fx) + (fy * fy));
                var amplitude = f > 0 ? Math.Pow(f, -1.5) : 0;
                spectrum[(ky * Size) + kx] = System.Numerics.Complex.FromPolarCoordinates(amplitude * Normal(random), 2 * Math.PI * random.NextDouble());
            }
        }
        TianWen.Lib.Stat.Fft2D.Inverse(spectrum, Size, Size);
        // Tapered to zero at the window's edge, as a stack's sky is: an edge left standing leaks into every frequency once padded.
        var field = spectrum.Select((c, i) => (float)(c.Real * Hann(i % Size) * Hann(i / Size))).ToArray();
        var rms = Math.Sqrt(field.Average(v => (double)v * v));
        var blurred = PlanetaryInverse.Apply(field.Select(v => (float)(v / rms)).ToArray(), Size, Size, f => Gaussian(1.0, f));
        const double sigma = 0.01;
        var noisy = blurred.Select(v => (float)(v + (sigma * Normal(random)))).ToArray();
        var noise = PlanetaryInverse.WhiteNoise(noisy, Size, Size);
        var (_, exponent) = PlanetaryInverse.PowerLawPrior(noisy, Size, Size, f => Gaussian(1.0, f), noise);
        TestContext.Current.TestOutputHelper?.WriteLine($"noise {Math.Sqrt(noise / (Size * Size)):0.0000} a pixel (made {sigma}); the power law's exponent {exponent:0.000} (made 3)");
        Math.Sqrt(noise / (Size * Size)).ShouldBe(sigma, sigma * 0.1);
        exponent.ShouldBe(3, 0.3);
    }

    [Fact]
    public void AnEdgePreservingPriorRestoresTheBandsAndKeepsThePlanePositive()
    {
        var truth = Texture();
        var random = new Random(4);
        var blurred = PlanetaryInverse.Apply(truth, Size, Size, f => Gaussian(1.2, f)).Select(v => (float)(v + (0.01 * Normal(random)))).ToArray();
        var before = PlanetaryMetrics.Fidelity(blurred, truth, Size, Size, Disk).Select(f => f.Transfer).ToArray();
        var restored = PlanetaryInverse.L1L2(blurred, Size, Size, f => Gaussian(1.2, f), mu: 1e-3, delta: 0.0141);
        var after = PlanetaryMetrics.Fidelity(restored, truth, Size, Size, Disk).Select(f => f.Transfer).ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(", ", before.Select((v, b) => $"band {b + 1}: {v:0.000} to {after[b]:0.000}")));
        after[0].ShouldBeGreaterThan(before[0] + 0.1);
        after[1].ShouldBeGreaterThan(before[1] + 0.05);
        restored.Min().ShouldBeGreaterThanOrEqualTo(0f);
    }

    private static double Hann(int i) => 0.5 - (0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / Size));

    private static double Normal(Random random) => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

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
