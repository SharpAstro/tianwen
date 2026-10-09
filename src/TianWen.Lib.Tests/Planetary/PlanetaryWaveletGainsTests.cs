using System;
using System.Collections.Immutable;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
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
    public void AGainBelowZeroBesideALargeOneIsNoDipInTheFilter()
    {
        // The layers overlap in frequency (#1251): EdgeHD Jupiter's derived gains swing below zero in band 3, and the filter they make
        // stays at or above one from the mean to Nyquist, peaking where bands 2 and 3 meet.
        double[] swinging = [1.00, 7.97, -0.65, 1.50];
        double[] unit = [1, 1, 1, 1];
        var lowest = double.PositiveInfinity;
        var (peak, peakAt) = (0.0, 0.0);
        for (var step = 0; step <= 100; step++)
        {
            var f = step * 0.005;
            PlanetaryWaveletGains.Transfer(unit, f).ShouldBe(1, 1e-12);
            var t = PlanetaryWaveletGains.Transfer(swinging, f);
            lowest = Math.Min(lowest, t);
            if (t > peak)
            {
                (peak, peakAt) = (t, f);
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"lowest {lowest:0.000}, peak {peak:0.00} at {peakAt:0.000} cycles a pixel");
        PlanetaryWaveletGains.Transfer(swinging, 0).ShouldBe(1, 1e-12);
        lowest.ShouldBeGreaterThanOrEqualTo(1 - 1e-9);
        peakAt.ShouldBeInRange(0.1, 0.2);
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
        // The gains derived with the true kernel and the disk's model (the texture's own mean level), against the gains for the texture
        // alone, which do not see the limb.
        var (truth, stack, power, wiener, sharpDisk, blurredDisk) = BlurredTexture();
        var (size, disk) = (BlurredTextureSize, BlurredTextureDisk);
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

        // The finest layer fitted within an interval (#1376): the free fit's finest gain clamped to it and the rest fitted around that, so
        // an interval holding the free gain gives the free fit, and one that does not gives the fit held at its nearer end.
        PlanetaryWaveletGains.FitFinestWithin(power, wiener, sharpDisk, blurredDisk, size, size, disk, gains[0] - 1, gains[0] + 1).ShouldBe(gains);
        PlanetaryWaveletGains.FitFinestWithin(power, wiener, sharpDisk, blurredDisk, size, size, disk, gains[0] + 0.5, gains[0] + 1)
            .ShouldBe(PlanetaryWaveletGains.Fit(power, wiener, sharpDisk, blurredDisk, size, size, disk, held: 1, heldAt: gains[0] + 0.5));
        PlanetaryWaveletGains.FitFinestWithin(power, wiener, sharpDisk, blurredDisk, size, size, disk, gains[0] - 1, gains[0] - 0.5)
            .ShouldBe(PlanetaryWaveletGains.Fit(power, wiener, sharpDisk, blurredDisk, size, size, disk, held: 1, heldAt: gains[0] - 0.5));
    }

    [Fact]
    public void TheFinestGainFittedWithinAnIntervalIsTheBestTheIntervalHolds()
    {
        // #1398: the clamp-then-refit of FitFinestWithin claimed exact for the convex quadratic the fit minimises, g A g - 2 b g, checked
        // against a brute-force scan of the finest gain over each interval, the other fitted gains solved for at every step by a solver of
        // the test's own (TryInvert3, not the fit's elimination). Either bound binding, and an interval holding the free optimum.
        var (_, _, power, wiener, sharpDisk, blurredDisk) = BlurredTexture();
        var (size, disk) = (BlurredTextureSize, BlurredTextureDisk);
        var (a, b) = PlanetaryWaveletGains.NormalEquations(power, wiener, sharpDisk, blurredDisk, size, size, disk, PlanetaryWaveletGains.ScoredBands, 1);
        a.GetLength(0).ShouldBe(4, "the others are three gains, solved by a 3x3 inverse");
        double Objective(ReadOnlySpan<double> g)
        {
            var sum = 0.0;
            for (var j = 0; j < 4; j++)
            {
                sum -= 2 * b[j] * g[j];
                for (var k = 0; k < 4; k++)
                {
                    sum += g[j] * a[j, k] * g[k];
                }
            }
            return sum;
        }
        // The finest gain held at t, the other three at their minimum given it: A_yy y = b_y - A_y0 t.
        double[] others = [a[1, 1], a[1, 2], a[1, 3], a[2, 1], a[2, 2], a[2, 3], a[3, 1], a[3, 2], a[3, 3]];
        var inverse = new double[9];
        CameraColorMatrix.TryInvert3(others, inverse).ShouldBeTrue();
        double Profile(double t)
        {
            Span<double> rhs = [b[1] - (a[1, 0] * t), b[2] - (a[2, 0] * t), b[3] - (a[3, 0] * t)];
            Span<double> g = [t, 0, 0, 0];
            for (var j = 0; j < 3; j++)
            {
                g[j + 1] = (inverse[j * 3] * rhs[0]) + (inverse[(j * 3) + 1] * rhs[1]) + (inverse[(j * 3) + 2] * rhs[2]);
            }
            return Objective(g);
        }

        var free = PlanetaryWaveletGains.Fit(power, wiener, sharpDisk, blurredDisk, size, size, disk)[0];
        TestContext.Current.TestOutputHelper?.WriteLine($"the free finest gain {free:0.000}");
        foreach (var (low, high) in new[] { (free + 0.5, free + 1), (free - 1, free - 0.5), (free - 1, free + 1), (0.0, 1.0) })
        {
            var within = PlanetaryWaveletGains.FitFinestWithin(power, wiener, sharpDisk, blurredDisk, size, size, disk, low, high);
            const int steps = 20_000;
            var (bestT, bestQ) = (low, Profile(low));
            for (var i = 1; i <= steps; i++)
            {
                var t = low + ((high - low) * i / steps);
                if (Profile(t) is var q && q < bestQ)
                {
                    (bestT, bestQ) = (t, q);
                }
            }
            var reached = Objective([within[0], within[1], within[2], within[3]]);
            TestContext.Current.TestOutputHelper?.WriteLine($"[{low:0.000}, {high:0.000}]: fitted {within[0]:0.0000} ({reached:G10}), scanned {bestT:0.0000} ({bestQ:G10})");
            within[0].ShouldBeInRange(low, high);
            within[0].ShouldBe(bestT, (high - low) / steps + 1e-9, "the finest gain the scan finds best");
            reached.ShouldBeLessThanOrEqualTo(bestQ + (1e-9 * Math.Abs(bestQ)), "no point of the interval does better");
        }
    }

    private const int BlurredTextureSize = 128;
    private static readonly MetricDisk BlurredTextureDisk = new(63.5, 63.5, 44);

    // A texture on a disk blurred by a known Gaussian, stacked from two halves with their own noise, and what gains are derived from: the
    // stack's power, the Wiener filter the halves' noise and the true kernel give, and the disk's model sharp and through the kernel.
    private static (float[] Truth, float[] Stack, ImmutableArray<double> Power, ImmutableArray<double> Wiener, float[] SharpDisk, float[] BlurredDisk) BlurredTexture()
    {
        const int size = BlurredTextureSize;
        var disk = BlurredTextureDisk;
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
        return (truth, stack, power, PlanetaryWaveletGains.Wiener(power, PlanetaryWaveletGains.HalvesNoise(a, b, size, size, disk), kernel), sharpDisk, blurredDisk);
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
