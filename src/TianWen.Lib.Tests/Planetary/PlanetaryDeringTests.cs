using System;
using System.Collections.Immutable;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R8 follow-up 2's ways to sharpen a disk without a ring (docs/plans/planetary-restoration.md): the limb channel leaves an exactly
/// modelled limb unsharpened, feathered gains leave the sky as it was, and gains fitted under a non-negative composite keep it so.
/// </summary>
public class PlanetaryDeringTests
{
    private const int Size = 128;
    // A disk small enough that the sky R3 reads past 2.5 radii is inside the window.
    private static readonly MetricDisk Disk = new MetricDisk(63.5, 63.5, 20);
    private static readonly double[] Strong = [4.8, 4.3, 3.7, 2.7, 1.5, 0.6];

    private static double Gaussian(double sigma, double f) => Math.Exp(-2 * Math.PI * Math.PI * sigma * sigma * f * f);

    // A disk darkened to its limb as mu^0.9 (Jupiter's red Minnaert k, about), each pixel the mean of 8 x 8 cells, on a sky of zero:
    // sharp, as the limb fit's model is. A hard-edged pixel disk would ring under any band-limited target (0.011 here), the
    // edge's own aliasing and not the channel's.
    private static float[] SharpDisk()
    {
        var plane = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                double sum = 0;
                for (var v = 0; v < 8; v++)
                {
                    for (var u = 0; u < 8; u++)
                    {
                        var r = Disk.RadiiAt(x - 0.5 + ((u + 0.5) / 8), y - 0.5 + ((v + 0.5) / 8));
                        sum += r < 1 ? Math.Pow(Math.Sqrt(1 - (r * r)), 0.9) : 0;
                    }
                }
                plane[(y * Size) + x] = (float)(sum / 64);
            }
        }
        return plane;
    }

    [Fact]
    public void TheLimbChannelLeavesAnExactlyModelledLimbUnrung()
    {
        var sharp = SharpDisk();
        var blurred = PlanetaryInverse.Apply(sharp, Size, Size, f => Gaussian(1.6, f));
        var plain = PlanetaryDering.Sharpen(blurred, Size, Size, Strong);
        var channel = PlanetaryDering.LimbChannel(blurred, Size, Size, sharp, f => Gaussian(1.6, f), f => Gaussian(0.5, f),
            p => PlanetaryDering.Sharpen(p, Size, Size, Strong));
        var (plainRing, channelRing) = (PlanetaryMetrics.LimbUndershoot(plain, Size, Size, Disk), PlanetaryMetrics.LimbUndershoot(channel, Size, Size, Disk));
        TestContext.Current.TestOutputHelper?.WriteLine($"undershoot: sharpened {plainRing:0.0000}, the limb as its own channel {channelRing:0.0000}");
        // What is left is the band-limited target's own floor on the pixel grid (0.0013 here), two orders under the sharpening's.
        plainRing.ShouldBeGreaterThan(0.05);
        channelRing.ShouldBeLessThan(0.002);
        channelRing.ShouldBeLessThan(plainRing / 100);
    }

    [Fact]
    public void FeatheredGainsLeaveTheSkyAsItWas()
    {
        var random = new Random(3);
        var blurred = PlanetaryInverse.Apply(SharpDisk(), Size, Size, f => Gaussian(1.6, f)).Select(v => v + (float)(0.01 * random.NextDouble())).ToArray();
        // Three layers reach 8 px, so the disk's middle, 20 px in, takes their boost in full.
        double[] three = [4.8, 4.3, 3.7];
        var feathered = PlanetaryDering.Feathered(blurred, Size, Size, Disk, three);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (Disk.RadiiAt(x, y) >= 1)
                {
                    feathered[(y * Size) + x].ShouldBe(blurred[(y * Size) + x], 1e-5f);
                }
            }
        }
        // Deep inside, the boost is the gains' own.
        var plain = PlanetaryDering.Sharpen(blurred, Size, Size, three);
        feathered[(64 * Size) + 64].ShouldBe(plain[(64 * Size) + 64], 1e-4f);
    }

    [Fact]
    public void ABoundedSharpeningAddsNoLightOutsideTheLimbAndKeepsTheDisksBoost()
    {
        // Sharpened plainly, the blurred disk rings outside its limb, above the sky as well as below it (#1168); bounded, it is never
        // brighter there than the stack nor below the sky, and no ring climbs back outside the limb. Inside, the boost is the plain one.
        var blurred = PlanetaryInverse.Apply(SharpDisk(), Size, Size, f => Gaussian(1.6, f));
        var plain = PlanetaryDering.Sharpen(blurred, Size, Size, Strong);
        var bounded = PlanetaryDering.Bounded(plain, blurred, Size, Size, Disk);
        var (plainRebound, boundedRebound) = (PlanetaryMetrics.LimbRebound(plain, Size, Size, Disk), PlanetaryMetrics.LimbRebound(bounded, Size, Size, Disk));
        TestContext.Current.TestOutputHelper?.WriteLine($"rebound outside the limb: sharpened {plainRebound:0.0000}, bounded {boundedRebound:0.0000}");
        plainRebound.ShouldBeGreaterThan(0.01);
        boundedRebound.ShouldBeLessThan(plainRebound / 10);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var i = (y * Size) + x;
                bounded[i].ShouldBeGreaterThanOrEqualTo(0f);
                if (Disk.RadiiAt(x, y) > 1)
                {
                    bounded[i].ShouldBeLessThanOrEqualTo(Math.Max(blurred[i], 0f));
                }
                else
                {
                    bounded[i].ShouldBe(Math.Max(plain[i], 0f));
                }
            }
        }
    }

    [Fact]
    public void ABoundedSharpeningSharpensAMoonAndStillHoldsTheHalo()
    {
        // The bound held a moon outside the limb as stacked (2022-09-03 Red: 0.024 above the sky where floored made it 0.101, #1181).
        // A moon is a local maximum of the stack and the planet's halo is not, so the moon keeps its sharpening and the halo stays held.
        var scene = SharpDisk();
        var (mx, my) = (113, 64);
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                scene[((my + dy) * Size) + mx + dx] = 0.6f;
            }
        }
        var blurred = PlanetaryInverse.Apply(scene, Size, Size, f => Gaussian(1.6, f));
        var plain = PlanetaryDering.Sharpen(blurred, Size, Size, Strong);
        var bounded = PlanetaryDering.Bounded(plain, blurred, Size, Size, Disk);

        var at = (my * Size) + mx;
        TestContext.Current.TestOutputHelper?.WriteLine($"the moon's peak: stacked {blurred[at]:0.000}, sharpened {plain[at]:0.000}, bounded {bounded[at]:0.000}");
        plain[at].ShouldBeGreaterThan(blurred[at] * 1.5f, "the sharpening lifts the moon");
        bounded[at].ShouldBe(plain[at], "and the bound leaves it lifted");
        PlanetaryMetrics.LimbRebound(bounded, Size, Size, Disk).ShouldBeLessThan(PlanetaryMetrics.LimbRebound(plain, Size, Size, Disk) / 10);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (Disk.RadiiAt(x, y) > 1 && ((x - mx) * (x - mx)) + ((y - my) * (y - my)) > 25)
                {
                    bounded[(y * Size) + x].ShouldBeLessThanOrEqualTo(Math.Max(blurred[(y * Size) + x], 0f));
                }
            }
        }
    }

    [Fact]
    public void ACompositeHeldNonNegativeStaysSo()
    {
        Func<double, double> kernel = f => Gaussian(1.6, f);
        var (rows, offsets) = PlanetaryDering.CompositeRows(kernel, PlanetaryWaveletGains.ScoredBands);
        // A target that asks for far more than the blur can give back: unconstrained, the composite goes negative.
        var a = new double[4, 4];
        for (var j = 0; j < 4; j++)
        {
            a[j, j] = 1;
        }
        double[] b = [6, 5, 4, 2];
        double Worst(double[] x) => rows.Select((row, i) => offsets[i] + row.Select((v, j) => v * x[j]).Sum()).Min();
        var free = PlanetaryCeilings.Solve(a, b);
        var held = PlanetaryWaveletGains.SolveNonNegative(a, b, rows, offsets);
        var peak = offsets.Max(Math.Abs);
        TestContext.Current.TestOutputHelper?.WriteLine($"the composite's lowest value: free {Worst(free):G3}, held {Worst(held):G3} of a peak {peak:G3}; gains {string.Join(", ", held.Select(g => g.ToString("0.00")))}");
        Worst(free).ShouldBeLessThan(-0.01 * peak);
        Worst(held).ShouldBeGreaterThan(-1e-5 * peak);
    }

    [Fact]
    public void SmoothingAveragesTheRingsThatHoldAReading()
    {
        var noisy = new RadialTransfer([1.0, 0.9, 1.1, 0.7, 0.9, 0.5, 0.7, 0, 0], 16);
        var smoothed = PlanetaryDering.Smoothed(noisy, 1);
        smoothed.Values[2].ShouldBe((0.9 + 1.1 + 0.7) / 3, 1e-12);
        smoothed.Values[6].ShouldBe((0.5 + 0.7) / 2, 1e-12);
        smoothed.Values[7].ShouldBe(0);
    }
}
