using System;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The colour reading #1273's study is made with and the look it chose: OKLab against Ottosson's own values; a planet's cast, spread and rim
/// read off a synthetic disk, where a planet of one colour has no spread and a rim pushed the other way round the hue circle reads as one;
/// the look's chroma curve, its preset, and the preview's own lift of the chroma.
/// </summary>
public class PlanetaryColourReadingTests
{
    [Fact]
    public void OkLabAgreesWithOttossonsValuesAndComesBack()
    {
        // sRGB's primaries and white in OKLab, as Ottosson lists them.
        var white = OkLab.FromLinearSrgb(1, 1, 1);
        white.L.ShouldBe(1, 1e-4);
        white.Chroma.ShouldBeLessThan(1e-4);
        var red = OkLab.FromLinearSrgb(1, 0, 0);
        red.L.ShouldBe(0.6280, 5e-4);
        red.A.ShouldBe(0.2249, 5e-4);
        red.B.ShouldBe(0.1258, 5e-4);
        var blue = OkLab.FromLinearSrgb(0, 0, 1);
        blue.L.ShouldBe(0.4520, 5e-4);
        blue.A.ShouldBe(-0.0325, 5e-4);
        blue.B.ShouldBe(-0.3115, 5e-4);

        var (r, g, b) = OkLab.FromLinearSrgb(0.31, 0.22, 0.09).ToLinearSrgb();
        // Ottosson's coefficients are given to ten digits, so the way back is that exact.
        r.ShouldBe(0.31, 1e-6);
        g.ShouldBe(0.22, 1e-6);
        b.ShouldBe(0.09, 1e-6);
        OkLab.HueDistanceDeg(170, -170).ShouldBe(20, 1e-12, "the shorter way round");
    }

    [Fact]
    public void APlanetOfOneColourHasNoSpreadAndARimPushedToItsOppositeHueReadsAsOne()
    {
        const int size = 96;
        var disk = new MetricDisk(47.5, 47.5, 30);
        var yellow = (R: 0.30f, G: 0.24f, B: 0.13f);
        var (red, green, blue) = Planet(size, disk, yellow, rim: null);

        var plain = PlanetaryColourReading.Read(red, green, blue, size, size, disk, default);
        plain.Spread.ShouldBeLessThan(1e-6, "one colour all over");
        plain.Cast.HueDeg.ShouldBeInRange(60, 110, "yellow lies between +a and +b");
        plain.RimHueOffsetDeg.ShouldBeLessThan(1, "a rim of the planet's own colour, dimmer");

        // The same planet with its rim cyan: the reading's hue offset there must say so.
        var (cr, cg, cb) = Planet(size, disk, yellow, rim: (0.05f, 0.12f, 0.14f));
        var cyan = PlanetaryColourReading.Read(cr, cg, cb, size, size, disk, default);
        cyan.RimHueOffsetDeg.ShouldBeGreaterThan(120, "a cyan rim round a yellow planet");
        cyan.Cast.Chroma.ShouldBe(plain.Cast.Chroma, 1e-9, "the interior is untouched");
    }

    [Fact]
    public void ALookRaisesEachPixelsChromaAboutGreyKeepingItsHueAndLeavesTheDarkAndTheEdgeAsTheyWere()
    {
        const int size = 128;
        var disk = new MetricDisk(63.5, 63.5, 50);
        var (master, red, green, blue) = BandedPlanet(size, disk);
        var before = PlanetaryColourReading.Read(red, green, blue, size, size, disk, default);

        var (looked, gains) = PlanetaryColourLook.Apply(master, disk, ColourLook.Uniform(2));
        gains.ShouldAllBe(g => Math.Abs(g - 2) < 1e-12);
        var (lr, lg, lb) = (looked.GetChannelSpan(0).ToArray(), looked.GetChannelSpan(1).ToArray(), looked.GetChannelSpan(2).ToArray());
        var after = PlanetaryColourReading.Read(lr, lg, lb, size, size, disk, default);
        after.Luminance.ShouldBe(before.Luminance, before.Luminance * 0.01, "lightness kept, so the planet's level too");

        // The belt and the zone each twice as far from grey, each at its own hue and lightness: the zone, less tinted than the planet's
        // mean, keeps its own hue, where a look about the mean pushed it past grey toward blue.
        foreach (var (y, what) in (ReadOnlySpan<(int, string)>)[(8 * 6 + 4, "the belt"), (8 * 7 + 4, "the zone")])
        {
            var i = (y * size) + 63;
            var (was, now) = (Normalised(red, green, blue, i, before.Luminance), Normalised(lr, lg, lb, i, before.Luminance));
            (now.Chroma / was.Chroma).ShouldBe(2, 1e-3, $"{what}'s chroma doubled");
            OkLab.HueDistanceDeg(was.HueDeg, now.HueDeg).ShouldBeLessThan(0.1, $"{what}'s hue kept");
            now.L.ShouldBe(was.L, 1e-4, $"{what}'s lightness kept");
        }
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size) + x;
                if (disk.ClearRadiiAt(x, y) >= PlanetaryColourLook.NoneFrom)
                {
                    (lr[i], lg[i], lb[i]).ShouldBe((red[i], green[i], blue[i]), "past the fade, the master as it was");
                }
                else if ((y / 8) % 5 == 0)
                {
                    (lr[i], lg[i], lb[i]).ShouldBe((red[i], green[i], blue[i]), "a gap darker than a twentieth of the planet, as it was");
                }
            }
        }
        looked.ImageMeta.IsColourBalanced.ShouldBeTrue("still a balanced master, one black point");

        // Fitted to the doubled picture's reading, the look lands the master's chroma on it at every quantile.
        var (fitted, _) = PlanetaryColourLook.Apply(master, disk, ColourLook.FittedTo(after));
        var reached = PlanetaryColourReading.Read(fitted.GetChannelSpan(0), fitted.GetChannelSpan(1), fitted.GetChannelSpan(2), size, size, disk, default);
        foreach (var q in (ReadOnlySpan<double>)[0.10, 0.50, 0.90])
        {
            (reached.ChromaAt(q) / after.ChromaAt(q)).ShouldBe(1, 0.03, $"the chroma's {q:0.00} quantile fitted");
        }
        master.Release();
        looked.Release();
        fitted.Release();

        static OkLab Normalised(float[] r, float[] g, float[] b, int i, double luminance) => OkLab.FromLinearSrgb(r[i] / luminance, g[i] / luminance, b[i] / luminance);
    }

    [Fact]
    public void TheBoostedLookWhitensThePlanetsLessColouredHalfAndStrengthensItsMoreColouredHalf()
    {
        const int size = 128;
        var disk = new MetricDisk(63.5, 63.5, 50);
        var (master, red, green, blue) = BandedPlanet(size, disk);
        var before = PlanetaryColourReading.Read(red, green, blue, size, size, disk, default);

        var (looked, gains) = PlanetaryColourLook.Apply(master, disk, ColourLook.Boosted);
        var after = PlanetaryColourReading.Read(looked.GetChannelSpan(0), looked.GetChannelSpan(1), looked.GetChannelSpan(2), size, size, disk, default);

        // The owner's S-curve (#1273): below one through the 45th percentile, above it from the 50th.
        var grid = PlanetaryColourReading.QuantileGrid;
        for (var k = 0; k < grid.Length; k++)
        {
            if (grid[k] <= 0.40)
            {
                gains[k].ShouldBeLessThan(1, $"the {grid[k]:0.00} quantile held below the master's chroma");
            }
            else if (grid[k] >= 0.50)
            {
                gains[k].ShouldBeGreaterThan(1, $"the {grid[k]:0.00} quantile raised");
            }
        }
        after.ChromaAt(0.10).ShouldBeLessThan(before.ChromaAt(0.10) * 0.8, "the zones whiter");
        after.ChromaAt(0.90).ShouldBeGreaterThan(before.ChromaAt(0.90) * 1.3, "the belts stronger");
        // Each pixel keeps its hue; the planet's MEAN hue leans a little toward the belts', whose chroma grew.
        var (lr, lg, lb) = (looked.GetChannelSpan(0).ToArray(), looked.GetChannelSpan(1).ToArray(), looked.GetChannelSpan(2).ToArray());
        foreach (var (y, what) in (ReadOnlySpan<(int, string)>)[(8 * 6 + 4, "the belt"), (8 * 7 + 4, "the zone")])
        {
            var i = (y * size) + 63;
            var was = OkLab.FromLinearSrgb(red[i] / before.Luminance, green[i] / before.Luminance, blue[i] / before.Luminance);
            var now = OkLab.FromLinearSrgb(lr[i] / before.Luminance, lg[i] / before.Luminance, lb[i] / before.Luminance);
            OkLab.HueDistanceDeg(was.HueDeg, now.HueDeg).ShouldBeLessThan(0.1, $"{what}'s hue kept");
        }
        master.Release();
        looked.Release();
    }

    [Fact]
    public void ThePostedLookMovesEachPixelAboutThePlanetsOwnColourAndKeepsItsLightness()
    {
        // C2 (#1305): every pixel's saturation, OKLab (a, b) over L, taken to q m + p (s - m) about the interior's mean m, lightness kept. So the
        // planet's colour goes to q of itself and every pixel's departure from it grows p times, along its hue and across it.
        const int size = 128;
        var disk = new MetricDisk(63.5, 63.5, 50);
        var (master, red, green, blue) = BandedPlanet(size, disk);
        var luminance = PlanetaryColourReading.Read(red, green, blue, size, size, disk, default).Luminance;
        var look = new ColourLook { AboutPlanet = (2, 0.5) };

        var (looked, gains) = PlanetaryColourLook.Apply(master, disk, look);
        gains.ShouldAllBe(g => g == 1, "no chroma curve");
        var (lr, lg, lb) = (looked.GetChannelSpan(0).ToArray(), looked.GetChannelSpan(1).ToArray(), looked.GetChannelSpan(2).ToArray());
        var (meanA, meanB) = MeanSaturation(red, green, blue);
        var (afterA, afterB) = MeanSaturation(lr, lg, lb);
        afterA.ShouldBe(0.5 * meanA, 0.02 * Math.Abs(meanA), "the planet's colour, half of itself");
        afterB.ShouldBe(0.5 * meanB, 0.02 * Math.Abs(meanB));
        foreach (var (y, what) in (ReadOnlySpan<(int, string)>)[(8 * 6 + 4, "the belt"), (8 * 7 + 4, "the zone")])
        {
            var i = (y * size) + 63;
            var (was, now) = (OkLab.FromLinearSrgb(red[i] / luminance, green[i] / luminance, blue[i] / luminance),
                OkLab.FromLinearSrgb(lr[i] / luminance, lg[i] / luminance, lb[i] / luminance));
            ((now.A / now.L) - afterA).ShouldBe(2 * ((was.A / was.L) - meanA), 0.002, $"{what}'s departure along a, doubled");
            ((now.B / now.L) - afterB).ShouldBe(2 * ((was.B / was.L) - meanB), 0.002, $"{what}'s departure along b, doubled");
            now.L.ShouldBe(was.L, 1e-4, $"{what}'s lightness kept");
        }
        for (var i = 0; i < red.Length; i++)
        {
            if (disk.ClearRadiiAt(i % size, i / size) >= PlanetaryColourLook.NoneFrom)
            {
                (lr[i], lg[i], lb[i]).ShouldBe((red[i], green[i], blue[i]), "past the fade, the master as it was");
            }
        }
        look.HeaderCards()["CLOOK"].Value.ShouldBe("posted");

        // Posted is each planet's own fit; any other look is the same on every planet.
        ColourLook.Posted.For(CatalogIndex.Saturn).AboutPlanet.ShouldBe((2.80, 0.65));
        ColourLook.Posted.For(CatalogIndex.Jupiter).AboutPlanet.ShouldBe((2.95, 0.30));
        ColourLook.Boosted.For(CatalogIndex.Saturn).ShouldBeSameAs(ColourLook.Boosted);
        master.Release();
        looked.Release();

        // The mean saturation over the interior the look takes in full, on the master's own normalisation.
        (double A, double B) MeanSaturation(float[] r, float[] g, float[] b)
        {
            var (sumA, sumB, n) = (0.0, 0.0, 0);
            for (var i = 0; i < r.Length; i++)
            {
                var lab = OkLab.FromLinearSrgb(r[i] / luminance, g[i] / luminance, b[i] / luminance);
                if (disk.ClearRadiiAt(i % size, i / size) <= PlanetaryColourLook.FullInside && lab.L > 0
                    && ((0.2126 * r[i]) + (0.7152 * g[i]) + (0.0722 * b[i])) / luminance >= PlanetaryColourLook.LitFrom)
                {
                    (sumA, sumB, n) = (sumA + (lab.A / lab.L), sumB + (lab.B / lab.L), n + 1);
                }
            }
            return (sumA / n, sumB / n);
        }
    }

    [Fact]
    public void APlanetsPreviewShowsMoreChromaThanItsLinearPlanesHold()
    {
        // The planetary preview's mid-tone lift bends each channel on its own and the sRGB curve again on screen, so what a post is compared
        // with is the master as shown, never its linear planes (#1273's study read the one against the other and was void for it).
        const int size = 128;
        var disk = new MetricDisk(63.5, 63.5, 50);
        var (master, red, green, blue) = BandedPlanet(size, disk);
        var linear = PlanetaryColourReading.Read(red, green, blue, size, size, disk, default);
        var shown = PlanetaryReferenceJudge.ReadShown(master, disk);

        shown.ChromaAt(0.50).ShouldBeGreaterThan(linear.ChromaAt(0.50) * 1.3, "the preview shows more chroma than the planes hold");
        OkLab.HueDistanceDeg(linear.Cast.HueDeg, shown.Cast.HueDeg).ShouldBeLessThan(15, "a stronger tint of the same colour, not another");
        master.Release();
    }

    [Fact]
    public void AMasterIsMadeReadyForALookByTheRoutineTheViewerAndPlanetaryLookShare()
    {
        // #1277: the viewer's colour control and `planetary-look` make a master ready by one routine, the limb fitted for the disk the look
        // reads and a master in the camera's colours balanced first, and the viewer's look is the verb's to the bit.
        var at = new DateTimeOffset(2024, 12, 15, 12, 56, 42, TimeSpan.Zero);
        var balanced = RenderedJupiter(at, balanced: true);
        var (prepared, refusal) = PlanetaryColourLook.Prepare(balanced, CatalogIndex.Jupiter, at);
        var ready = prepared.ShouldNotBeNull(refusal);
        ready.Balanced.ShouldBeNull("a balanced master is taken as it is");
        ReferenceEquals(ready.Master, balanced).ShouldBeTrue("and is not copied");
        ready.Disk.Radius.ShouldBe(40, 1, "the limb fit finds the rendered disk");

        var (looked, lookRefusal) = PlanetaryColourLook.OnMaster(balanced, CatalogIndex.Jupiter, at, ColourLook.Boosted);
        looked.ShouldNotBeNull(lookRefusal);
        var (applied, _) = PlanetaryColourLook.Apply(balanced, ready.Disk, ColourLook.Boosted);
        Differing(looked, applied).ShouldBe(0, "the look on a master made ready is the look applied over its fitted disk");
        Differing(looked, balanced).ShouldBeGreaterThan(0, "and it moves the colour");

        // A master left in the camera's colours is balanced first, into a new image the caller owns.
        var (fromCamera, cameraRefusal) = PlanetaryColourLook.Prepare(RenderedJupiter(at, balanced: false), CatalogIndex.Jupiter, at);
        var balancedFirst = fromCamera.ShouldNotBeNull(cameraRefusal);
        balancedFirst.Balanced.ShouldNotBeNull("it says how it was balanced");
        balancedFirst.Master.ImageMeta.IsColourBalanced.ShouldBeTrue();

        PlanetaryColourLook.Prepare(Image.FromChannel(new float[16, 16]), CatalogIndex.Jupiter, at).Refusal.ShouldNotBeNull().ShouldContain("three-channel");
        PlanetaryColourLook.Prepare(balanced, CatalogIndex.Mars, at).Refusal.ShouldNotBeNull().ShouldContain("Jupiter and Saturn");
    }

    /// <summary>
    /// A colour master of Jupiter as a planetary stack writes one, rendered at <paramref name="at"/> (its exposure's middle): the spotted map
    /// through the ephemeris on a 128 px frame, the disk 40 px in radius, tan with its darker places redder (a range of chroma for the look's
    /// quantiles), on a black sky; its OBJECT the planet, and marked balanced when asked (as <c>CBALSAT</c> marks a written master).
    /// </summary>
    internal static Image RenderedJupiter(DateTimeOffset at, bool balanced)
    {
        const int size = 128;
        var duration = TimeSpan.FromSeconds(60);
        var plane = PlanetaryRender.Render(PlanetaryDerotationTests.SpottedMap(), PhysicalEphemeris.Compute(CatalogIndex.Jupiter, at),
            new DiskPlacement(63.6, 64.3, 40, 30), size, size, 0.95, supersample: 2);
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[size, size];
        }
        var max = 0f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var v = plane[(y * size) + x];
                (planes[0][y, x], planes[1][y, x], planes[2][y, x]) = (0.55f * v, 0.47f * v, 0.33f * MathF.Pow(v, 1.4f));
                max = MathF.Max(max, 0.55f * v);
            }
        }
        var meta = new ImageMeta("test", at - (duration / 2), duration, FrameType.Light, "", 0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN,
            SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN, ObjectName: "Jupiter") { IsColourBalanced = balanced };
        return new Image(planes, BitDepth.Float32, max, 0f, 0f, meta);
    }

    private static int Differing(Image a, Image b)
    {
        a.ChannelCount.ShouldBe(b.ChannelCount);
        var differing = 0;
        for (var c = 0; c < a.ChannelCount; c++)
        {
            var x = a.GetChannelSpan(c);
            var y = b.GetChannelSpan(c);
            for (var i = 0; i < x.Length; i++)
            {
                if (x[i] != y[i])
                {
                    differing++;
                }
            }
        }
        return differing;
    }

    // A balanced master of a belt and a paler zone in bands across the disk, each paler to the left (a quantile map needs a range of chroma,
    // not two ties), every fifth band a dark gap, on a black sky; with its planes as flat arrays.
    private static (Image Master, float[] R, float[] G, float[] B) BandedPlanet(int size, MetricDisk disk)
    {
        var (red, green, blue) = (new float[size * size], new float[size * size], new float[size * size]);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size) + x;
                if (disk.ClearRadiiAt(x, y) < 1.2)
                {
                    var (r, g, b) = ((y / 8) % 5) switch
                    {
                        0 => (0.006f, 0.005f, 0.002f),
                        1 or 3 => (0.32f, 0.22f, 0.12f),
                        _ => (0.30f, 0.26f, 0.18f),
                    };
                    var (grey, s) = ((r + g + b) / 3, 0.4f + (0.6f * x / size));
                    (red[i], green[i], blue[i]) = (grey + (s * (r - grey)), grey + (s * (g - grey)), grey + (s * (b - grey)));
                }
            }
        }
        var master = new Image([Plane(red), Plane(green), Plane(blue)], BitDepth.Float32, 0.32f, 0, 0, new ImageMeta { IsColourBalanced = true });
        return (master, red, green, blue);

        float[,] Plane(float[] flat)
        {
            var plane = new float[size, size];
            for (var i = 0; i < flat.Length; i++)
            {
                plane[i / size, i % size] = flat[i];
            }
            return plane;
        }
    }

    // A disk of one colour on a black sky, its outer tenth (0.9 to 1.1 of the radius) another colour when asked.
    private static (float[] R, float[] G, float[] B) Planet(int size, MetricDisk disk, (float R, float G, float B) colour, (float R, float G, float B)? rim)
    {
        var (r, g, b) = (new float[size * size], new float[size * size], new float[size * size]);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var at = disk.ClearRadiiAt(x, y);
                var c = (R: 0f, G: 0f, B: 0f);
                if (at < PlanetaryColourReading.InteriorOutline)
                {
                    c = colour;
                }
                else if (at < 1)
                {
                    c = rim ?? (colour.R * 0.5f, colour.G * 0.5f, colour.B * 0.5f);
                }
                else if (at < PlanetaryColourReading.RimOutline && rim is { } fringe)
                {
                    c = (fringe.R * 0.3f, fringe.G * 0.3f, fringe.B * 0.3f);
                }
                var i = (y * size) + x;
                (r[i], g[i], b[i]) = c;
            }
        }
        return (r, g, b);
    }
}
