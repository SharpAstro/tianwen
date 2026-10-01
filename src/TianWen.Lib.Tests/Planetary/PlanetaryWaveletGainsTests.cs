using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R8's derived wavelet gains (docs/plans/planetary-restoration.md, R8, #1055): the a trous layers' transfer the fit assumes, the noise
/// two halves give, and the gains the Wiener filter gives restoring a blurred texture near the jointly fitted oracle.
/// </summary>
public class PlanetaryWaveletGainsTests
{
    [Fact]
    public void LayerGainsActAsTheBSplinesTransfer()
    {
        // Three layers reach 14 px, so a disk 34 px clear of the window's edge sees no boundary, mirrored or periodic.
        const int size = 128;
        var plane = Texture(size, 30, seed: 3);
        double[] gains = [2.0, 1.5, 0.7];
        var applied = PlanetaryWaveletGains.Apply(plane, size, size, gains);

        var field = new System.Numerics.Complex[size * size];
        for (var i = 0; i < plane.Length; i++)
        {
            field[i] = plane[i];
        }
        Fft2D.Forward(field, size, size);
        for (var ky = 0; ky < size; ky++)
        {
            var fy = (ky < size / 2 ? ky : ky - size) / (double)size;
            for (var kx = 0; kx < size; kx++)
            {
                var fx = (kx < size / 2 ? kx : kx - size) / (double)size;
                var g = PlanetaryWaveletGains.Scaling(gains.Length, fx, fy);
                for (var j = 0; j < gains.Length; j++)
                {
                    g += gains[j] * (PlanetaryWaveletGains.Scaling(j, fx, fy) - PlanetaryWaveletGains.Scaling(j + 1, fx, fy));
                }
                field[(ky * size) + kx] *= g;
            }
        }
        Fft2D.Inverse(field, size, size);

        var rms = Math.Sqrt(plane.Select(v => (double)v * v).Average());
        var difference = Math.Sqrt(applied.Select((v, i) => (v - field[i].Real) * (v - field[i].Real)).Average());
        TestContext.Current.TestOutputHelper?.WriteLine($"difference {difference:G3} of an RMS {rms:G3}");
        difference.ShouldBeLessThan(1e-4 * rms);
    }

    [Fact]
    public void HalfTheHalvesDifferenceIsTheStacksNoise()
    {
        const int size = 128;
        const double sigma = 3;
        var truth = Texture(size, 44, seed: 5);
        var random = new Random(7);
        var a = truth.Select(v => (float)(v + (sigma * Normal(random)))).ToArray();
        var b = truth.Select(v => (float)(v + (sigma * Normal(random)))).ToArray();
        var disk = new MetricDisk(63.5, 63.5, 44);
        var noise = PlanetaryWaveletGains.HalvesNoise(a, b, size, size, disk);
        // Half the difference of two halves each with sigma per pixel: sigma^2 / 2 per pixel, summed per coefficient over the pixels the
        // interior's taper keeps, each by its square.
        var taper = PlanetaryWaveletGains.Interior(Enumerable.Repeat(1f, size * size).Select((_, i) => i % 2 == 0 ? 1f : -1f).ToArray(), size, size, disk);
        var expected = taper.Sum(t => (double)t * t) * sigma * sigma / 2;
        var measured = Enumerable.Range(20, 100).Average(r => noise[r]);
        TestContext.Current.TestOutputHelper?.WriteLine($"noise per coefficient {measured:G4}, expected {expected:G4}");
        measured.ShouldBe(expected, expected * 0.05);
    }

    [Fact]
    public void GainsFromTheWienerFilterRestoreABlurredTextureNearTheJointOracle()
    {
        // A texture on a disk blurred by a known Gaussian, stacked from two halves with their own noise; the gains derived with the true
        // kernel and the disk's model (the texture's own mean level), against the gains for the texture alone, which do not see the limb.
        const int size = 128;
        var disk = new MetricDisk(63.5, 63.5, 44);
        var truth = Texture(size, 44, seed: 11);
        const double sigma = 1.6;
        Func<double, double> kernel = f => Math.Exp(-2 * Math.PI * Math.PI * sigma * sigma * f * f);
        var blurred = PlanetaryInverse.Apply(truth, size, size, kernel);
        var random = new Random(13);
        var a = blurred.Select(v => (float)(v + (3 * Math.Sqrt(2) * Normal(random)))).ToArray();
        var b = blurred.Select(v => (float)(v + (3 * Math.Sqrt(2) * Normal(random)))).ToArray();
        var stack = a.Zip(b, (x, y) => (x + y) / 2).ToArray();
        var sharpDisk = Texture(size, 44, seed: 11, blobs: 0);
        var blurredDisk = PlanetaryInverse.Apply(sharpDisk, size, size, kernel);

        var power = PlanetaryWaveletGains.StackPower(stack, size, size, disk);
        var wiener = PlanetaryWaveletGains.Wiener(power, PlanetaryWaveletGains.HalvesNoise(a, b, size, size, disk), kernel);
        var gains = PlanetaryWaveletGains.Fit(power, wiener, sharpDisk, blurredDisk, size, size, disk);
        var textureOnly = PlanetaryWaveletGains.FitTexture(power, wiener);
        var (joint, jointGains) = PlanetaryCeilings.PerBandJointOracle(stack, truth, size, size, disk, 4);

        double Error(float[] plane) => PlanetaryMetrics.Fidelity(plane, truth, size, size, disk, 4).Sum(f => f.Error);
        var (e0, e1, e2, e3) = (Error(stack), Error(PlanetaryWaveletGains.Apply(stack, size, size, gains.AsSpan())), Error(PlanetaryWaveletGains.Apply(stack, size, size, textureOnly.AsSpan())), Error(joint));
        static string Show(System.Collections.Generic.IEnumerable<double> g) => string.Join(", ", g.Select(v => v.ToString("0.00")));
        TestContext.Current.TestOutputHelper?.WriteLine($"error: the stack {e0:0.000}; derived {e1:0.000} ({Show(gains)}); the texture alone {e2:0.000} ({Show(textureOnly)}); the joint oracle {e3:0.000} ({Show(jointGains)})");
        e1.ShouldBeLessThan(e0);
        e1.ShouldBeLessThan(e2);
        e1.ShouldBeLessThan(1.2 * e3);

        // The finest layer held at 1: it stays at 1, and the rest are fitted around it.
        var held = PlanetaryWaveletGains.Fit(power, wiener, sharpDisk, blurredDisk, size, size, disk, held: 1);
        held[0].ShouldBe(1);
        Error(PlanetaryWaveletGains.Apply(stack, size, size, held.AsSpan())).ShouldBeLessThan(e0);
    }

    private static double Normal(Random random) => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    // A texture of blobs on a disk of `radius` in the window's middle, a thousand over a sky of zero; with no blobs, the disk alone.
    private static float[] Texture(int size, double radius, int seed, int blobs = 80)
    {
        var random = new Random(seed);
        var c = (size / 2) - 0.5;
        var spread = 1.5 * radius;
        var placed = Enumerable.Range(0, blobs).Select(_ => (X: c + (random.NextDouble() * spread) - (spread / 2), Y: c + (random.NextDouble() * spread) - (spread / 2), A: (random.NextDouble() * 300) - 150, S: 0.8 + (random.NextDouble() * 2))).ToArray();
        var plane = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var r = Math.Sqrt(((x - c) * (x - c)) + ((y - c) * (y - c)));
                var v = 1000.0;
                foreach (var blob in placed)
                {
                    v += blob.A * Math.Exp(-(((x - blob.X) * (x - blob.X)) + ((y - blob.Y) * (y - blob.Y))) / (2 * blob.S * blob.S));
                }
                plane[(y * size) + x] = (float)(r < radius ? v : 0);
            }
        }
        return plane;
    }
}
