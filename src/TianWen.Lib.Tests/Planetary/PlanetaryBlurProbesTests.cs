using System;
using System.Collections.Immutable;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R7 part 2's probes of a stack's blur (docs/plans/planetary-restoration.md): the stack's transfer over two halves of lucky frames
/// reads a known blur with the noise left out, the limb fit's kernel mixes its core and its halo as the fit models them, and a
/// kernel's equivalent Gaussian is the Gaussian it was made from.
/// </summary>
public class PlanetaryBlurProbesTests
{
    private const int Size = 128;
    private static readonly MetricDisk Disk = new(63.5, 63.5, 44);

    // A disk of texture on a zero sky, about 1 inside: a normalised object.
    private static float[] Texture(int seed)
    {
        var random = new Random(seed);
        var blobs = Enumerable.Range(0, 120).Select(_ => (X: 63.5 + (random.NextDouble() * 70) - 35, Y: 63.5 + (random.NextDouble() * 70) - 35, A: (random.NextDouble() * 0.6) - 0.3, S: 1 + (random.NextDouble() * 3))).ToArray();
        var plane = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r = Math.Sqrt(((x - 63.5) * (x - 63.5)) + ((y - 63.5) * (y - 63.5)));
                var v = r < 48 ? 1.0 : 0.0;
                foreach (var b in blobs)
                {
                    v += b.A * Math.Exp(-(((x - b.X) * (x - b.X)) + ((y - b.Y) * (y - b.Y))) / (2 * b.S * b.S));
                }
                plane[(y * Size) + x] = (float)(r < 48 ? v : 0);
            }
        }
        return plane;
    }

    private static float[] WithNoise(float[] plane, double sigma, int seed)
    {
        var random = new Random(seed);
        return [.. plane.Select(v => (float)(v + (sigma * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble()))))];
    }

    [Fact]
    public void TheStackOverTwoNoisyHalvesReadsItsOwnBlurWithTheNoiseLeftOut()
    {
        var lucky = Texture(1);
        var stack = Image.SeparableGaussianBlur(lucky, Size, Size, 1.2f);
        // The halves each carry noise of their own, as few lucky frames do, and the stack its own: a pure noise term in either the
        // numerator or the denominator would pull the ratio off its true transfer. (Where the halves' noise outweighs the finest band,
        // as it does on an 8-bit capture's band 1, the ratio is unbiased but no longer precise.)
        var probe = PlanetaryMetrics.CrossTransfer(WithNoise(stack, 0.01, 2), WithNoise(lucky, 0.02, 3), WithNoise(lucky, 0.02, 4), Size, Size, Disk);
        var truth = PlanetaryMetrics.Fidelity(stack, lucky, Size, Size, Disk).Select(f => f.Transfer).ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(", ", probe.Select((t, b) => $"band {b + 1}: {t:0.000} (true {truth[b]:0.000})")));
        for (var b = 0; b < 4; b++)
        {
            probe[b].ShouldBe(truth[b], 0.05, $"band {b + 1}");
        }
    }

    [Fact]
    public void TheLimbKernelMixesItsCoreAndItsHaloAsTheFitDoes()
    {
        var plane = Texture(5);
        var coreOnly = new LimbFit(63.5, 63.5, 44, 0, 1, 1.5, 1, 0, 0, 0, 0, [], 0, true);
        PlanetaryBlurProbes.BlurByLimbKernel(plane, Size, Size, coreOnly).ShouldBe(Image.SeparableGaussianBlur(plane, Size, Size, 1.5f));
        var withHalo = coreOnly with { HaloFraction = 0.2, HaloWidth = 6 };
        var mixed = PlanetaryBlurProbes.BlurByLimbKernel(plane, Size, Size, withHalo);
        var (core, halo) = (Image.SeparableGaussianBlur(plane, Size, Size, 1.5f), Image.SeparableGaussianBlur(plane, Size, Size, 6f));
        for (var i = 0; i < mixed.Length; i += 97)
        {
            mixed[i].ShouldBe((0.8f * core[i]) + (0.2f * halo[i]), 1e-5f);
        }
    }

    [Fact]
    public void AKernelsEquivalentGaussianIsTheGaussianItWasMadeFrom()
    {
        var plane = Texture(7);
        var made = PlanetaryMetrics.Fidelity(Image.SeparableGaussianBlur(plane, Size, Size, 1.7f), plane, Size, Size, Disk).Select(f => f.Transfer).ToImmutableArray();
        var sigma = PlanetaryBlurProbes.EquivalentGaussianSigma(made, plane, Size, Size, Disk);
        TestContext.Current.TestOutputHelper?.WriteLine($"equivalent sigma {sigma:0.000} for 1.7");
        sigma.ShouldBe(1.7, 0.02);
    }
}
