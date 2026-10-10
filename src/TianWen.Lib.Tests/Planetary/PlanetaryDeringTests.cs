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
        // Lifted on the surface about it rather than kept whole (#1301): here the bound's surface and the sharpening's agree to 0.1 %.
        bounded[at].ShouldBe(plain[at], 0.01f * plain[at], "and the bound leaves it lifted");
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

    // A disk of one, of radius 40 px, on a 192 px frame with a glow falling as exp(-(r - R) / 10 px) from 0.3 at its limb, cut to the sky's zero
    // 40 px out, so the sky's noise is nothing and a source need only clear the 0.01 floor. A moon (a Gaussian of 1.2 px, `moonPeak` high) 14
    // px past the limb to its left, its reach and the ring about it inside 1.5 radii, where the model alone is drawn; to the right, 16 px
    // out, a pixel raised by `bump` over the glow's slope: a local maximum, as a noise peak is.
    private const int GlowSize = 192;
    private static readonly MetricDisk GlowDisk = new MetricDisk(95.5, 95.5, 40);

    private static float[] Glowing(double glowScale, double moonPeak, double bump, out (int X, int Y) moon, out (int X, int Y) notMoon)
    {
        moon = ((int)(GlowDisk.X - GlowDisk.Radius - 14), 96);
        notMoon = ((int)(GlowDisk.X + GlowDisk.Radius + 16), 96);
        var plane = new float[GlowSize * GlowSize];
        for (var y = 0; y < GlowSize; y++)
        {
            for (var x = 0; x < GlowSize; x++)
            {
                var r = GlowDisk.RadiiAt(x, y) * GlowDisk.Radius;
                var glow = r < GlowDisk.Radius ? 1 : r < GlowDisk.Radius + 40 ? glowScale * 0.3 * Math.Exp(-(r - GlowDisk.Radius) / 10) : 0;
                var m = moonPeak * Math.Exp(-(((x - moon.X) * (x - moon.X)) + ((y - moon.Y) * (y - moon.Y))) / (2 * 1.2 * 1.2));
                plane[(y * GlowSize) + x] = (float)(glow + m);
            }
        }
        plane[(notMoon.Y * GlowSize) + notMoon.X] += (float)bump;
        return plane;
    }

    [Fact]
    public void AMaximumOnAGlowsSlopeIsNoMoonAndAMoonOnItIs()
    {
        // #1301: beside Saturn's ring tip the blue glow held a noise maximum standing 0.0136 of the disk above its box's median, past the
        // 0.01 floor, and the sharpening kept it as a moon: a hard-edged disc of the stack's glow. Above the plane its surroundings make it
        // stands nowhere; a moon does.
        var plane = Glowing(1, 0.08, 0.0097, out var moon, out var notMoon);
        var (byMedian, _) = PlanetaryMetrics.SourcePeak(plane, GlowSize, GlowSize, notMoon.X, notMoon.Y);
        var byPlane = PlanetaryMetrics.PeakAbovePlane(plane, GlowSize, GlowSize, notMoon.X, notMoon.Y);
        var moonByPlane = PlanetaryMetrics.PeakAbovePlane(plane, GlowSize, GlowSize, moon.X, moon.Y);
        TestContext.Current.TestOutputHelper?.WriteLine($"the slope's maximum: {byMedian:0.0000} above its box's median, {byPlane:+0.0000;-0.0000} above its plane; the moon {moonByPlane:0.0000} above its plane");

        byMedian.ShouldBeGreaterThan(0.01, "by its box's median it stands as a source would");
        byPlane.ShouldBeLessThan(0.01);
        moonByPlane.ShouldBeGreaterThan(0.06);
        PlanetaryMetrics.CompactSources(plane, GlowSize, GlowSize, GlowDisk, count: PlanetaryDering.MaxMoons).ShouldHaveSingleItem().ShouldBe(moon);
    }

    [Fact]
    public void TheLimbsLastBandTakesTheModelAsItsFloorWhenAskedAndNothingElseMoves()
    {
        // #1329: at strength 2 a sharpening's negative lobe left the outline's last pixels at the sky, just inside the glow drawn past it.
        // Asked, the band from 0.95 of the outline to it is floored at the model; inside it and past the outline nothing changes.
        var (sharpened, stacked, model) = (new float[Size * Size], new float[Size * Size], new float[Size * Size]);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var (i, r) = ((y * Size) + x, Disk.RadiiAt(x, y));
                stacked[i] = r <= 1 ? 1f : 0f;
                model[i] = r <= 1.2 ? 0.4f : 0f;
                sharpened[i] = r is >= 0.95 and <= 1 ? -0.2f : r < 0.95 ? 0.1f : 0.3f;
            }
        }

        var atSky = PlanetaryDering.Outside(sharpened, stacked, Size, Size, Disk, PlanetaryDering.OutsideLimb.ModelFeathered, model: model);
        var atModel = PlanetaryDering.Outside(sharpened, stacked, Size, Size, Disk, PlanetaryDering.OutsideLimb.ModelFeathered, model: model, modelInLimbBand: true);

        int band = 0, elsewhereMoved = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var (i, r) = ((y * Size) + x, Disk.RadiiAt(x, y));
                if (r is >= PlanetaryDering.LimbBandInner and <= 1)
                {
                    band++;
                    atSky[i].ShouldBe(0f, "floored at the sky, as before");
                    atModel[i].ShouldBe(0.4f, "floored at the model");
                }
                else if (atSky[i] != atModel[i])
                {
                    elsewhereMoved++;
                }
            }
        }
        band.ShouldBeGreaterThan(100);
        elsewhereMoved.ShouldBe(0, "inside the band's start and past the outline everything is as it was");
    }

    [Fact]
    public void APixelTheSharpeningDrivesBelowTheSkyTakesTheModelWhenAskedAndNothingAboveTheSkyMoves()
    {
        // #1471: at strength 2 a sharpening's negative lobe drove Saturn's C ring and the gap inside it past the sky, where the truth is a dim
        // glow. Asked, a pixel inside the outline the sharpening leaves at or below the sky takes the model; any other pixel is as it was.
        var (sharpened, stacked, model) = (new float[Size * Size], new float[Size * Size], new float[Size * Size]);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var (i, r) = ((y * Size) + x, Disk.RadiiAt(x, y));
                stacked[i] = r <= 1 ? 1f : 0f;
                model[i] = r <= 1.2 ? 0.4f : 0f;
                // Below the sky in a ring about half the radius, at it on a row, above it everywhere else inside.
                sharpened[i] = r is >= 0.45 and <= 0.55 ? -0.2f : y == 63 && r < 0.4 ? 0f : r <= 1 ? 0.1f : 0.3f;
            }
        }

        var atSky = PlanetaryDering.Outside(sharpened, stacked, Size, Size, Disk, PlanetaryDering.OutsideLimb.ModelFeathered, model: model);
        var atModel = PlanetaryDering.Outside(sharpened, stacked, Size, Size, Disk, PlanetaryDering.OutsideLimb.ModelFeathered, model: model, modelUnderSky: true);

        int under = 0, elsewhereMoved = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var i = (y * Size) + x;
                if (Disk.RadiiAt(x, y) <= 1 && sharpened[i] <= 0)
                {
                    under++;
                    atSky[i].ShouldBe(0f, "floored at the sky, as before");
                    atModel[i].ShouldBe(0.4f, "the model where the sharpening reached the sky");
                }
                else if (BitConverter.SingleToInt32Bits(atSky[i]) != BitConverter.SingleToInt32Bits(atModel[i]))
                {
                    elsewhereMoved++;
                }
            }
        }
        under.ShouldBeGreaterThan(100);
        elsewhereMoved.ShouldBe(0, "a pixel the sharpening leaves above the sky, and any past the outline, is as it was to the bit");
    }

    [Fact]
    public void AMoonKeptInTheModelsZoneAddsItsOwnLightAndNoneOfTheGlowItSitsIn()
    {
        // #1301: a moon's sharpening was kept whole within its reach, the stack's glow with it, while the planet's model was drawn around it;
        // where the stack's glow stands above the model's, that is a disc with a hard edge. The moon now stands on the model's surface.
        var stacked = Glowing(1, 0.08, 0, out var moon, out _);
        // The sharpening: the moon lifted to 0.2, the glow as stacked. The model through the pupil alone: a quarter of the stack's glow.
        var sharpened = Glowing(1, 0.2, 0, out _, out _);
        var model = Glowing(0.25, 0, 0, out _, out _);
        var drawn = PlanetaryDering.Outside(sharpened, stacked, GlowSize, GlowSize, GlowDisk, PlanetaryDering.OutsideLimb.ModelFeathered, model: model);

        double Ring(float[] plane, double from, double to)
        {
            var (sum, n) = (0.0, 0);
            for (var y = moon.Y - 7; y <= moon.Y + 7; y++)
            {
                for (var x = moon.X - 7; x <= moon.X + 7; x++)
                {
                    var d = Math.Sqrt(((x - moon.X) * (x - moon.X)) + ((y - moon.Y) * (y - moon.Y)));
                    if (d > from && d <= to)
                    {
                        (sum, n) = (sum + plane[(y * GlowSize) + x], n + 1);
                    }
                }
            }
            return sum / n;
        }
        var step = Ring(drawn, 4, 5) - Ring(drawn, 5, 6);
        var modelStep = Ring(model, 4, 5) - Ring(model, 5, 6);
        var at = (moon.Y * GlowSize) + moon.X;
        var lift = drawn[at] - model[at];
        TestContext.Current.TestOutputHelper?.WriteLine($"at the reach the drawn plane steps {step:+0.0000;-0.0000} (the model's own slope {modelStep:+0.0000;-0.0000}); the moon stands {lift:0.0000} on the model");

        PlanetaryMetrics.CompactSources(stacked, GlowSize, GlowSize, GlowDisk, count: PlanetaryDering.MaxMoons).ShouldHaveSingleItem().ShouldBe(moon);
        // Kept whole, the stack's glow stood three quarters above the model's within the reach: a step of 0.055 and the moon at 0.253. A
        // plane through a glow this curved (0.3 of the disk at the limb, falling over 10 px) lies a little above it, so the moon stands
        // about 6 % low; on the real captures its peak moved by at most 1 % (#1301).
        step.ShouldBe(modelStep, 0.005, "no step where the moon meets the model");
        lift.ShouldBe(0.2f, 0.02f, "the moon keeps its sharpened light, and none of the glow");
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
    public void BoundedsSuccessorsPutTheirOwnLightOutsideTheLimbAndLeaveTheDiskAlone()
    {
        // #1171's three candidates against bounded's trough: inside the limb each is the sharpened plane held at the sky; outside it, the
        // stack as it is, bounded but at or above the stack times its glow share, or bounded blended to the stack by 1.1 radii.
        var blurred = PlanetaryInverse.Apply(SharpDisk(), Size, Size, f => Gaussian(1.6, f));
        var sharpened = PlanetaryDering.Sharpen(blurred, Size, Size, Strong);
        var share = Enumerable.Repeat(0.5f, Size * Size).ToArray();
        var held = PlanetaryDering.Outside(sharpened, blurred, Size, Size, Disk, PlanetaryDering.OutsideLimb.Stack);
        var floored = PlanetaryDering.Outside(sharpened, blurred, Size, Size, Disk, PlanetaryDering.OutsideLimb.ModelFloor, share);
        var blended = PlanetaryDering.Outside(sharpened, blurred, Size, Size, Disk, PlanetaryDering.OutsideLimb.Blended);
        var bounded = PlanetaryDering.Bounded(sharpened, blurred, Size, Size, Disk);
        // The default (#1171): the model out to 1.5 radii, the stack by the plane's inscribed circle (here 2.5 radii, 50 of 64 px).
        var model = PlanetaryInverse.Apply(SharpDisk(), Size, Size, f => Gaussian(0.5, f));
        var feathered = PlanetaryDering.Outside(sharpened, blurred, Size, Size, Disk, PlanetaryDering.OutsideLimb.ModelFeathered, model: model);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var (i, r) = ((y * Size) + x, Disk.RadiiAt(x, y));
                if (r <= 1)
                {
                    feathered[i].ShouldBe(Math.Max(sharpened[i], 0f));
                    held[i].ShouldBe(Math.Max(sharpened[i], 0f));
                    floored[i].ShouldBe(Math.Max(sharpened[i], 0f));
                    blended[i].ShouldBe(Math.Max(sharpened[i], 0f));
                    continue;
                }
                var stack = Math.Max(blurred[i], 0f);
                if (r <= 1.5)
                {
                    feathered[i].ShouldBe(Math.Max(model[i], 0f), "the model, never the sharpening, out to 1.5 radii");
                }
                else if (r >= 2.5)
                {
                    feathered[i].ShouldBe(blurred[i], 1e-6f);
                }
                held[i].ShouldBe(blurred[i]);
                floored[i].ShouldBeGreaterThanOrEqualTo(0.5f * stack - 1e-7f);
                floored[i].ShouldBeLessThanOrEqualTo(stack + 1e-7f);
                if (r >= 1.1)
                {
                    blended[i].ShouldBe(stack, 1e-6f);
                }
                else
                {
                    blended[i].ShouldBeInRange(Math.Min(bounded[i], stack) - 1e-6f, Math.Max(bounded[i], stack) + 1e-6f);
                }
            }
        }
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
