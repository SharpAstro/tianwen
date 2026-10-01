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
    public void APhysicalKernelFittedToTheEdgeCarriesItToTheCutoff()
    {
        var truth = new PhysicalKernel(12, 0.5, 0.05, 10, 0.94);
        var sharp = Disk();
        var blurred = PlanetaryInverse.Apply(sharp, Size, Size, truth.TransferAt);

        var fit = PlanetaryFinestBand.FitPhysical(PlanetaryFinestBand.Edge(blurred, sharp, Size, Size, Center, sunSide: 0), truth.CutoffCyclesPerPixel);
        TestContext.Current.TestOutputHelper?.WriteLine($"fitted: A {fit.Seeing:0.00} (D/r0 {fit.ApertureOverR0:0.00}), sigma {fit.SigmaPx:0.00} px, halo {fit.Halo:0.000} of {fit.HaloWidthPx:0.0} px");
        foreach (var f in new[] { 0.1, 0.2, 0.3, 0.4, 0.45 })
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{f}: fitted {fit.TransferAt(f):0.000}, true {truth.TransferAt(f):0.000}");
            fit.TransferAt(f).ShouldBe(truth.TransferAt(f), 0.03);
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

    // A round Gaussian of Sigma, and an extra Gaussian jitter of 0.8 px along x: the planet's equator along x, its axis along y.
    private const double Jitter = 0.8;

    private static double Elongated(double fx, double fy) => Gaussian(Math.Sqrt((fx * fx) + (fy * fy))) * Math.Exp(-2 * Math.PI * Math.PI * Jitter * Jitter * fx * fx);

    // A sector's mean of a 2-D transfer at |f|, over directions within 30 degrees of axisDeg (the modes of a ring fill it evenly).
    private static double SectorMean(Func<double, double, double> transfer, double f, double axisDeg)
    {
        double sum = 0;
        for (var k = -30; k <= 30; k++)
        {
            var theta = (axisDeg + k) * Math.PI / 180;
            sum += transfer(f * Math.Cos(theta), f * Math.Sin(theta));
        }
        return sum / 61;
    }

    [Fact]
    public void AnElongatedBlurIsReadOffTheEdgeInTwoSectors()
    {
        var sharp = Disk();
        var blurred = PlanetaryInverse.Apply(sharp, Size, Size, Elongated);

        var axis = PlanetaryFinestBand.Edge(blurred, sharp, Size, Size, Center, sunSide: 0, sectorDeg: 90);
        var equator = PlanetaryFinestBand.Edge(blurred, sharp, Size, Size, Center, sunSide: 0, sectorDeg: 0);
        var sigma = PlanetaryFinestBand.JitterSigma(axis.TransferAt, equator.TransferAt);
        TestContext.Current.TestOutputHelper?.WriteLine($"the jitter read {sigma:0.000} px, put in {Jitter} px");
        // A sector 30 degrees each way mixes a little of the other direction into each read, so the jitter reads about a tenth short.
        sigma.ShouldBe(Jitter, 0.12);

        var fit = PlanetaryFinestBand.FitElongated(axis, equator, 0.94, equatorDeg: 0);
        foreach (var f in new[] { 0.1, 0.2, 0.3 })
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{f}: along x {fit.TransferAt(f, 0):0.000} (true {Elongated(f, 0):0.000}), along y {fit.TransferAt(0, f):0.000} (true {Elongated(0, f):0.000})");
            fit.TransferAt(f, 0).ShouldBe(Elongated(f, 0), 0.04);
            fit.TransferAt(0, f).ShouldBe(Elongated(0, f), 0.04);
        }
    }

    [Fact]
    public void TwoDirectionsInterpolateAJitterExactly()
    {
        var twoD = PlanetaryFinestBand.TwoDirections(f => Gaussian(f), f => Elongated(f, 0), axisDeg: 90);
        foreach (var (fx, fy) in new[] { (0.1, 0.0), (0.0, 0.2), (0.15, 0.15), (-0.2, 0.1), (0.05, -0.3) })
        {
            twoD(fx, fy).ShouldBe(Elongated(fx, fy), 1e-9);
        }
    }

    [Fact]
    public void TheOracleReadInASectorIsTheKernelAlongIt()
    {
        var seen = Disk(Texture(1));
        var blurred = PlanetaryInverse.Apply(seen, Size, Size, Elongated);
        foreach (var axis in new[] { 0.0, 90.0 })
        {
            var read = PlanetaryInverse.Measure(blurred, seen, Size, Size, sectorDeg: axis);
            foreach (var f in new[] { 0.1, 0.2, 0.3 })
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"{axis} deg, {f}: read {read.At(f):0.000}, the kernel's {SectorMean(Elongated, f, axis):0.000}");
                read.At(f).ShouldBe(SectorMean(Elongated, f, axis), 0.03);
            }
        }
    }

    [Fact]
    public void RichardsonLucyWithARoundTwoDimensionalTransferIsTheRoundOne()
    {
        var blurred = PlanetaryInverse.Apply(Disk(Texture(1)), Size, Size, Gaussian);
        var (round, twoD) = (new float[Size * Size], new float[Size * Size]);
        PlanetaryInverse.RichardsonLucy(blurred, Size, Size, Gaussian, 3, (_, p) => round = p);
        PlanetaryInverse.RichardsonLucy(blurred, Size, Size, (fx, fy) => Gaussian(Math.Sqrt((fx * fx) + (fy * fy))), 3, (_, p) => twoD = p);
        for (var i = 0; i < round.Length; i++)
        {
            twoD[i].ShouldBe(round[i], 1e-6f);
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
