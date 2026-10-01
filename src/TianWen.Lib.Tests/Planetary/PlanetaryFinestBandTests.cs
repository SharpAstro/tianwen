using System;
using System.Collections.Immutable;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R8 follow-up 3's two direct reads of a stack's finest band (docs/plans/planetary-restoration.md): the limb's edge against its sharp
/// model, and the disk's spectrum against another texture of the same spectrum, each on a disk blurred by a kernel of known transfer.
/// </summary>
public class PlanetaryFinestBandTests
{
    private const int Size = 160;
    private const double Radius = 50;
    private const double Sigma = 1.2;

    private static double Gaussian(double f) => Math.Exp(-2 * Math.PI * Math.PI * Sigma * Sigma * f * f);

    // A disk darkened to its limb, 8 x 8 cells to a pixel, times a texture when one is given.
    private static float[] Disk(float[]? texture = null)
    {
        var plane = new float[Size * Size];
        var c = (Size - 1) / 2.0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                double sum = 0;
                for (var v = 0; v < 8; v++)
                {
                    for (var u = 0; u < 8; u++)
                    {
                        var (dx, dy) = (x - c + ((u + 0.5) / 8) - 0.5, y - c + ((v + 0.5) / 8) - 0.5);
                        var r = Math.Sqrt((dx * dx) + (dy * dy)) / Radius;
                        sum += r < 1 ? Math.Pow(Math.Sqrt(1 - (r * r)), 0.6) : 0;
                    }
                }
                var i = (y * Size) + x;
                plane[i] = (float)(sum / 64 * (texture is null ? 1 : 1 + texture[i]));
            }
        }
        return plane;
    }

    // A texture of a falling power-law spectrum, about a tenth of the disk in contrast.
    private static float[] Texture(int seed)
    {
        var random = new Random(seed);
        var white = Enumerable.Range(0, Size * Size).Select(_ => (float)((random.NextDouble() * 2) - 1)).ToArray();
        var shaped = PlanetaryInverse.Apply(white, Size, Size, f => f < 1e-6 ? 0 : Math.Pow(f / 0.05, -1.2) / (1 + Math.Pow(f / 0.05, -1.2)));
        var rms = Math.Sqrt(shaped.Average(v => (double)v * v));
        return [.. shaped.Select(v => (float)(0.1 * v / rms))];
    }

    private static readonly MetricDisk Center = new MetricDisk((Size - 1) / 2.0, (Size - 1) / 2.0, Radius);

    [Fact]
    public void TheLimbsEdgeReadsAKnownBlursTransfer()
    {
        var sharp = Disk();
        var blurred = PlanetaryInverse.Apply(sharp, Size, Size, Gaussian);

        var edge = PlanetaryFinestBand.Edge(blurred, sharp, Size, Size, Center, sunSide: 0);
        foreach (var f in new[] { 0.1, 0.2, 0.3 })
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{f}: the edge reads {edge.TransferAt(f):0.000}, the blur's is {Gaussian(f):0.000}");
            edge.TransferAt(f).ShouldBe(Gaussian(f), 0.03);
        }
    }

    [Fact]
    public void TheLimbsEdgeOnItsOwnModelReadsOne()
    {
        var sharp = Disk();
        var edge = PlanetaryFinestBand.Edge(sharp, sharp, Size, Size, Center, sunSide: 0);
        foreach (var f in new[] { 0.1, 0.2, 0.3 })
        {
            edge.TransferAt(f).ShouldBe(1, 1e-6);
        }
    }

    [Fact]
    public void ADisksSpectrumOverAnotherTextureOfItsSpectrumReadsTheBlur()
    {
        var (seen, other) = (Disk(Texture(1)), Disk(Texture(2)));
        var blurred = PlanetaryInverse.Apply(seen, Size, Size, Gaussian);
        var stackPower = PlanetaryFinestBand.TexturePower(blurred, Size, Size, Center);
        var objectPower = PlanetaryFinestBand.TexturePower(other, Size, Size, Center);
        var noise = stackPower.Select(_ => 0.0).ToImmutableArray();

        var transfer = PlanetaryFinestBand.Spectrum(stackPower, noise, objectPower);
        TestContext.Current.TestOutputHelper?.WriteLine($"the two textures' power ratio 0.1 to 0.3: {PlanetaryFinestBand.PowerRatio(PlanetaryFinestBand.TexturePower(seen, Size, Size, Center), objectPower, 0.1, 0.3):0.000}");
        foreach (var f in new[] { 0.1, 0.2, 0.3 })
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{f}: the spectrum reads {transfer.At(f):0.000}, the blur's is {Gaussian(f):0.000}");
            transfer.At(f).ShouldBe(Gaussian(f), 0.15 * Gaussian(f) + 0.02);
        }
    }
}
