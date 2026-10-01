using System;
using System.Collections.Immutable;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A Galilean moon beside the disk as a near-point probe (docs/plans/planetary-restoration.md, R8 follow-up 4): drawn by the render
/// where the ephemeris' frame puts it, and its blurred image read back as the kernel's transfer out to 0.45 cycles a pixel.
/// </summary>
public class PlanetaryMoonProbeTests
{
    private const int Size = 96;
    private const double Radius = 1.07;
    private const double Sigma = 0.9;

    private static double Gaussian(double f) => Math.Exp(-2 * Math.PI * Math.PI * Sigma * Sigma * f * f);

    private const int Fine = 8;

    // A uniform disk at (x, y) smeared along its drift, as a camera sees it: blurred by `transfer` on a grid eight times finer than
    // the pixels, then summed into them, on a sky that slopes.
    private static float[] Moon(double x, double y, Func<double, double> transfer, double driftX = 0, double driftY = 0)
    {
        var n = Size * Fine;
        var fine = new float[n * n];
        var reach = Radius + Math.Max(Math.Abs(driftX), Math.Abs(driftY)) + 1;
        for (var l = (int)((y - reach + 0.5) * Fine); l <= (int)((y + reach + 0.5) * Fine); l++)
        {
            for (var k = (int)((x - reach + 0.5) * Fine); k <= (int)((x + reach + 0.5) * Fine); k++)
            {
                var hits = 0;
                for (var s = 0; s < 8; s++)
                {
                    var t = ((s + 0.5) / 8) - 0.5;
                    for (var v = 0; v < 4; v++)
                    {
                        for (var u = 0; u < 4; u++)
                        {
                            // Fine sample k spans [k, k + 1) / Fine of a pixel, pixel i spanning [i - 0.5, i + 0.5).
                            var px = ((k + ((u + 0.5) / 4)) / Fine) - 0.5 - x - (t * driftX);
                            var py = ((l + ((v + 0.5) / 4)) / Fine) - 0.5 - y - (t * driftY);
                            if ((px * px) + (py * py) < Radius * Radius)
                            {
                                hits++;
                            }
                        }
                    }
                }
                fine[(l * n) + k] = hits / (8f * 16);
            }
        }
        var blurred = PlanetaryInverse.Apply(fine, n, n, f => transfer(f * Fine));
        var plane = new float[Size * Size];
        for (var j = 0; j < Size; j++)
        {
            for (var i = 0; i < Size; i++)
            {
                double sum = 0;
                for (var l = 0; l < Fine; l++)
                {
                    for (var k = 0; k < Fine; k++)
                    {
                        sum += blurred[(((j * Fine) + l) * n) + (i * Fine) + k];
                    }
                }
                plane[(j * Size) + i] = (float)((sum / (Fine * Fine)) + 0.2 + (0.002 * i) - (0.001 * j));
            }
        }
        return plane;
    }

    [Theory]
    [InlineData(48.3, 46.6, 0, 0)]
    [InlineData(47.8, 49.4, 0.6, -0.3)]
    public void AMoonsBlurredDiskReadsTheKernelToTheFinestBand(double x, double y, double driftX, double driftY)
    {
        var plane = Moon(x, y, Gaussian, driftX, driftY);
        var read = PlanetaryMoonProbe.Read(plane, Size, Size, x + 1.7, y - 1.2, Radius, driftX, driftY, _ => 1).ShouldNotBeNull();

        TestContext.Current.TestOutputHelper?.WriteLine($"found at {read.X:0.000}, {read.Y:0.000} (true {x}, {y}), flux {read.Flux:0.000}");
        read.X.ShouldBe(x, 0.02);
        read.Y.ShouldBe(y, 0.02);
        foreach (var f in new[] { 0.1, 0.2, 0.3, 0.4, 0.45 })
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{f}: read {read.Transfer.At(f):0.000}, the kernel's {Gaussian(f):0.000}, the model's {read.Model.At(f):0.000}");
            read.Transfer.At(f).ShouldBe(Gaussian(f), 0.02);
        }
    }

    [Fact]
    public void AMoonReadsTheKernelOverTheDiffractionItIsGiven()
    {
        static double Diffraction(double f) => Math.Max(0, 1 - (f / 0.9));
        var plane = Moon(48.4, 47.7, f => Gaussian(f) * Diffraction(f));

        var read = PlanetaryMoonProbe.Read(plane, Size, Size, 48, 48, Radius, 0, 0, Diffraction).ShouldNotBeNull();
        foreach (var f in new[] { 0.1, 0.2, 0.3, 0.4, 0.45 })
        {
            read.Transfer.At(f).ShouldBe(Gaussian(f), 0.02);
        }
    }

    [Fact]
    public void AnElongatedKernelReadsItsTwoDirectionsInTheirSectors()
    {
        // Twice as blurred along x as along y: a sector about either axis averages the transfer over its own directions.
        const double sigmaX = 1.2, sigmaY = 0.6;
        var plane = ElongatedMoon(48.3, 46.6, sigmaX, sigmaY);
        static double Expected(double f, double axisDeg)
        {
            double sum = 0;
            for (var k = -30; k <= 30; k++)
            {
                var theta = (axisDeg + k) * Math.PI / 180;
                var (c, s) = (Math.Cos(theta), Math.Sin(theta));
                sum += Math.Exp(-2 * Math.PI * Math.PI * f * f * ((sigmaX * sigmaX * c * c) + (sigmaY * sigmaY * s * s)));
            }
            return sum / 61;
        }
        foreach (var axis in new[] { 0.0, 90.0 })
        {
            var read = PlanetaryMoonProbe.Read(plane, Size, Size, 48, 47, Radius, 0, 0, _ => 1, sectorDeg: axis).ShouldNotBeNull();
            foreach (var f in new[] { 0.1, 0.2, 0.3 })
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"{axis} deg, {f}: read {read.Transfer.At(f):0.000}, the kernel's {Expected(f, axis):0.000}");
                read.Transfer.At(f).ShouldBe(Expected(f, axis), 0.03);
            }
        }
    }

    [Fact]
    public void AQuadraticRimTakesACurvedSkyOffAWiderSquare()
    {
        var plane = Moon(48.3, 46.6, Gaussian);
        for (var j = 0; j < Size; j++)
        {
            for (var i = 0; i < Size; i++)
            {
                var (u, v) = ((i - 48) / 48.0, (j - 47) / 48.0);
                plane[(j * Size) + i] += (float)((0.3 * u * u) - (0.2 * u * v) + (0.25 * v * v));
            }
        }

        var read = PlanetaryMoonProbe.Read(plane, Size, Size, 48, 47, Radius, 0, 0, _ => 1, reach: 24, quadratic: true).ShouldNotBeNull();
        read.Flux.ShouldBe(Math.PI * Radius * Radius, 0.02);
        foreach (var f in new[] { 0.1, 0.2, 0.3, 0.4 })
        {
            read.Transfer.At(f).ShouldBe(Gaussian(f), 0.02);
        }
    }

    // A uniform disk at (x, y) blurred by a Gaussian of sigmaX along x and sigmaY along y on the fine grid, then summed into pixels.
    private static float[] ElongatedMoon(double x, double y, double sigmaX, double sigmaY)
    {
        var n = Size * Fine;
        var fine = new float[n * n];
        var reach = Radius + 1;
        for (var l = (int)((y - reach + 0.5) * Fine); l <= (int)((y + reach + 0.5) * Fine); l++)
        {
            for (var k = (int)((x - reach + 0.5) * Fine); k <= (int)((x + reach + 0.5) * Fine); k++)
            {
                var hits = 0;
                for (var v = 0; v < 4; v++)
                {
                    for (var u = 0; u < 4; u++)
                    {
                        var px = ((k + ((u + 0.5) / 4)) / Fine) - 0.5 - x;
                        var py = ((l + ((v + 0.5) / 4)) / Fine) - 0.5 - y;
                        if ((px * px) + (py * py) < Radius * Radius)
                        {
                            hits++;
                        }
                    }
                }
                fine[(l * n) + k] = hits / 16f;
            }
        }
        var blurred = Separable(fine, n, sigmaX * Fine, sigmaY * Fine);
        var plane = new float[Size * Size];
        for (var j = 0; j < Size; j++)
        {
            for (var i = 0; i < Size; i++)
            {
                double sum = 0;
                for (var l = 0; l < Fine; l++)
                {
                    for (var k = 0; k < Fine; k++)
                    {
                        sum += blurred[(((j * Fine) + l) * n) + (i * Fine) + k];
                    }
                }
                plane[(j * Size) + i] = (float)(sum / (Fine * Fine));
            }
        }
        return plane;
    }

    // A Gaussian blur, separably, of sigmaX samples along x and sigmaY along y.
    private static float[] Separable(float[] plane, int n, double sigmaX, double sigmaY)
    {
        static double[] Kernel(double sigma)
        {
            var half = (int)Math.Ceiling(4 * sigma);
            var kernel = new double[(2 * half) + 1];
            double sum = 0;
            for (var i = -half; i <= half; i++)
            {
                kernel[i + half] = Math.Exp(-0.5 * i * i / (sigma * sigma));
                sum += kernel[i + half];
            }
            for (var i = 0; i < kernel.Length; i++)
            {
                kernel[i] /= sum;
            }
            return kernel;
        }
        var (kx, ky) = (Kernel(sigmaX), Kernel(sigmaY));
        var (hx, hy) = (kx.Length / 2, ky.Length / 2);
        var rows = new float[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                double sum = 0;
                for (var i = -hx; i <= hx; i++)
                {
                    var at = x + i;
                    if (at >= 0 && at < n)
                    {
                        sum += kx[i + hx] * plane[(y * n) + at];
                    }
                }
                rows[(y * n) + x] = (float)sum;
            }
        }
        var result = new float[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                double sum = 0;
                for (var j = -hy; j <= hy; j++)
                {
                    var at = y + j;
                    if (at >= 0 && at < n)
                    {
                        sum += ky[j + hy] * rows[(at * n) + x];
                    }
                }
                result[(y * n) + x] = (float)sum;
            }
        }
        return result;
    }

    [Fact]
    public void AHalfStackIsReadWhereItsWholeStackFoundTheMoon()
    {
        var plane = Moon(48.3, 46.6, Gaussian);
        var read = PlanetaryMoonProbe.Read(plane, Size, Size, 48.3, 46.6, Radius, 0, 0, _ => 1, searchPx: 0).ShouldNotBeNull();

        (read.X, read.Y).ShouldBe((48.3, 46.6));
        read.Transfer.At(0.3).ShouldBe(Gaussian(0.3), 0.02);
    }

    [Fact]
    public void TheRenderDrawsAMoonWestOfTheDiskAtItsLevel()
    {
        const int size = 160;
        var values = new float[72 * 36];
        Array.Fill(values, 1f);
        var map = new PlanetMap(values, 72, 36);
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, new DateTimeOffset(2022, 9, 3, 12, 11, 0, TimeSpan.Zero));
        // North up on a y-down image, so west is to the right: a moon two radii west is 40 px right of the centre.
        var disk = new DiskPlacement(60.5, 80.5, 20, -90);
        var render = PlanetaryRender.Render(map, aspect, disk, size, size, 0.95, supersample: 4, [new MoonDisk(2, 0, 0.25, 0.5)]);

        double inner = 0;
        var count = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (((x - 60.5) * (x - 60.5)) + ((y - 80.5) * (y - 80.5)) < 0.64 * 400)
                {
                    inner += render[(y * size) + x];
                    count++;
                }
            }
        }
        // The moon is 10 px across: its light sums to its level times the disk's mean times its area (16 rays a pixel count it to 2 %).
        double light = 0, sumX = 0, sumY = 0;
        for (var y = 70; y < 92; y++)
        {
            for (var x = 90; x < 112; x++)
            {
                var v = render[(y * size) + x];
                (light, sumX, sumY) = (light + v, sumX + (v * x), sumY + (v * y));
            }
        }
        (sumX / light).ShouldBe(100.5, 0.01);
        (sumY / light).ShouldBe(80.5, 0.01);
        light.ShouldBe(0.5 * (inner / count) * Math.PI * 25, 0.02 * light);

        // Mirrored, the same moon is to the left, inside the disk's own side of the frame.
        var mirrored = PlanetaryRender.Render(map, aspect, disk with { Mirrored = true }, size, size, 0.95, supersample: 4, [new MoonDisk(2, 0, 0.25, 0.5)]);
        mirrored[(80 * size) + 20].ShouldBeGreaterThan(0f);
        mirrored[(80 * size) + 100].ShouldBe(0f);
    }

    [Fact]
    public void ThreeMoonsSayWhichEndOfTheAxisIsNorth()
    {
        const int size = 200;
        var disk = new DiskPlacement(100, 100, 20, 154);
        ImmutableArray<MoonDisk> moons = [new MoonDisk(-1.9, -0.42, 0.05, 1), new MoonDisk(-3.45, 0.67, 0.05, 1), new MoonDisk(4.1, 0.14, 0.05, 1)];
        var plane = new float[size * size];
        foreach (var moon in moons)
        {
            var (x, y) = disk.ImagePoint(moon.X, moon.Y);
            plane[((int)Math.Round(y) * size) + (int)Math.Round(x)] = 1;
        }

        var turned = PlanetaryMoonProbe.Orient(plane, size, size, disk with { NorthAngleDeg = 334 }, moons);
        turned.NorthAngleDeg.ShouldBe(154, 1e-9);
        turned.Mirrored.ShouldBeFalse();
        var flipped = PlanetaryMoonProbe.Orient(plane, size, size, disk with { Mirrored = true }, moons);
        flipped.NorthAngleDeg.ShouldBe(154, 1e-9);
        flipped.Mirrored.ShouldBeFalse();
    }
}
