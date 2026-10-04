using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// #1212's colour arithmetic (<see cref="PlanetaryColour"/>): a reflectance spectrum through the CIE observer under D65, the joins
/// between OPAL's filters, a disk's mean colour over its sky, the latitude bands' chroma spread, and the rotation average, whose
/// first version swung the Sun round the planet.
/// </summary>
public class PlanetaryColourTests
{
    private static readonly DateTimeOffset Night = new DateTimeOffset(2022, 9, 3, 12, 10, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWhiteReflectorRendersWhiteAndAGreyOneGrey(bool monotoneCubic)
    {
        double[] nm = [400, 500, 600, 700];
        var white = PlanetaryColour.SrgbOfReflectance(nm, [1, 1, 1, 1], monotoneCubic);
        white.R.ShouldBe(1, 1e-9);
        white.G.ShouldBe(1, 1e-9);
        white.B.ShouldBe(1, 1e-9);
        var grey = PlanetaryColour.SrgbOfReflectance(nm, [0.4, 0.4, 0.4, 0.4], monotoneCubic);
        grey.R.ShouldBe(0.4, 1e-9);
        grey.G.ShouldBe(0.4, 1e-9);
        grey.B.ShouldBe(0.4, 1e-9);
    }

    [Fact]
    public void JupitersReflectanceRendersAWarmWhiteAndTheTwoJoinsAgree()
    {
        // OPAL 2024's disk-mean I/F at the composite's geometry (docs/plans/planetary-restoration.md, "A planetary master's colour"):
        // dark in the violet, nearly flat from 500 nm, so a warm white, nowhere near the composite's R/G 1.14.
        // Jupiter's five filters, F658N at the red end.
        string[] jupiters = ["F395N", "F467M", "F502N", "F631N", "F658N"];
        var pivots = new double[jupiters.Length];
        for (var f = 0; f < pivots.Length; f++)
        {
            pivots[f] = PlanetaryColour.OpalVisiblePivots.Single(p => p.Name == jupiters[f]).PivotNm;
        }
        double[] reflectance = [0.3834, 0.5274, 0.5511, 0.5873, 0.5988];
        var linear = PlanetaryColour.SrgbOfReflectance(pivots, reflectance, monotoneCubic: false);
        var cubic = PlanetaryColour.SrgbOfReflectance(pivots, reflectance, monotoneCubic: true);
        TestContext.Current.TestOutputHelper?.WriteLine($"linear R/G {linear.R / linear.G:0.000} B/G {linear.B / linear.G:0.000}; cubic R/G {cubic.R / cubic.G:0.000} B/G {cubic.B / cubic.G:0.000}");
        (cubic.R / cubic.G).ShouldBe(1.033, 0.005);
        (cubic.B / cubic.G).ShouldBe(0.873, 0.005);
        linear.ChromaDistance(cubic).ShouldBeLessThan(0.005, "F467M fills the gap where a broadband blue lies, so the join matters little");
    }

    [Fact]
    public void AnOpalReadmesTableGivesEachVisibleFiltersFactorAndK()
    {
        // As OPAL's readmes write their tables (Jupiter 2022's and Saturn 2022's rows, tabs and a stray space as found): the visible
        // filters, bluest first, ultraviolet ones and a note after a value passed over (S6, #1235).
        string[] jupiter = ["F658N\t.999\t\t\t.00417", "F631N\t.999\t\t\t.00347", "F502N\t.950\t\t\t.00348", "F467M \t.950\t\t\t.00338",
            "F395N\t.850\t\t\t.00330", "F343N\t.850\t\t\t.00213 (rotation 2 only)", "F275W\t.520\t\t\t.00231"];
        string[] saturn = ["Filter\tMinnaert k\tI/F scale factor (FITS files)", "F395N\t0.40\t\t.00245", "F467M\t0.85\t\t.00536",
            "F502N\t0.65\t\t.00302", "F631N\t0.80\t\t.00321", "F763M\t0.85\t\t.00399"];

        var jupiterFilters = PlanetaryColour.ReadmeFilters(jupiter);
        var saturnFilters = PlanetaryColour.ReadmeFilters(saturn);

        jupiterFilters.Select(f => f.Name).ShouldBe(["F395N", "F467M", "F502N", "F631N", "F658N"]);
        jupiterFilters[3].ShouldBe(new OpalFilter("F631N", 630.4, 0.999, 0.00347));
        saturnFilters.Select(f => f.Name).ShouldBe(["F395N", "F467M", "F502N", "F631N", "F763M"]);
        saturnFilters[0].ShouldBe(new OpalFilter("F395N", 395.3, 0.40, 0.00245));
        saturnFilters[4].MinnaertK.ShouldBe(0.85);
    }

    [Fact]
    public void TheMonotoneCubicPassesThroughItsSamplesAndNeverOvershoots()
    {
        double[] nm = [395.3, 468.3, 501.0, 630.4, 656.4];
        double[] rho = [0.38, 0.53, 0.55, 0.587, 0.599];
        var slopes = PlanetaryColour.MonotoneSlopes(nm, rho);
        for (var k = 0; k < nm.Length; k++)
        {
            PlanetaryColour.Interpolate(nm, rho, slopes, nm[k]).ShouldBe(rho[k], 1e-12);
        }
        for (var lambda = 380.0; lambda <= 780; lambda += 0.5)
        {
            var value = PlanetaryColour.Interpolate(nm, rho, slopes, lambda);
            value.ShouldBeInRange(rho[0] - 1e-12, rho[^1] + 1e-12);
            if (lambda > nm[0] && lambda < nm[^1])
            {
                var k = Array.FindLastIndex(nm, x => x <= lambda);
                value.ShouldBeInRange(Math.Min(rho[k], rho[k + 1]) - 1e-12, Math.Max(rho[k], rho[k + 1]) + 1e-12);
            }
        }
    }

    [Fact]
    public void BandsOfOneColourHaveNoChromaSpreadAndTwoColoursHaveTheirOwn()
    {
        PlanetaryColour.ChromaSpread([new LinearRgb(2, 1, 0.5), new LinearRgb(4, 2, 1)], [100, 300]).ShouldBe(0, 1e-12);
        // Mean colour (1.5, 1, 1): chromaticity (3/7, 2/7). The bands' (1/3, 1/3) and (1/2, 1/4) lie 0.10648 and 0.07986 from it.
        PlanetaryColour.ChromaSpread([new LinearRgb(1, 1, 1), new LinearRgb(2, 1, 1)], [100, 100]).ShouldBe(0.094116, 1e-5);
    }

    [Fact]
    public void TheDiskMeanReadsAColourBackOverItsSky()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var placement = new DiskPlacement(99.5, 101.2, 60, NorthAngleDeg: -80);
        var render = PlanetaryRender.Render(new PlanetMap(Uniform(1f), 360, 180), aspect, placement, 200, 200, minnaertK: 0.9);
        var (red, green, blue) = (new float[render.Length], new float[render.Length], new float[render.Length]);
        for (var i = 0; i < render.Length; i++)
        {
            (red[i], green[i], blue[i]) = ((0.8f * render[i]) + 0.10f, (0.6f * render[i]) + 0.05f, (0.4f * render[i]) + 0.02f);
        }
        var disk = new MetricDisk(placement.CenterX, placement.CenterY, placement.EquatorialRadius, PlanetaryLimbFit.OptionsFor(aspect).AxisRatio, placement.NorthAngleDeg);
        var sky = PlanetaryColour.Sky(red, green, blue, 200, 200, disk);
        var mean = PlanetaryColour.DiskMean(red, green, blue, 200, 200, disk, sky);
        sky.R.ShouldBe(0.10, 1e-4);
        sky.G.ShouldBe(0.05, 1e-4);
        sky.B.ShouldBe(0.02, 1e-4);
        (mean.R / mean.G).ShouldBe(0.8 / 0.6, 1e-3);
        (mean.B / mean.G).ShouldBe(0.4 / 0.6, 1e-3);
    }

    [Fact]
    public void TurningAPlanetCarriesItsSunWithIt()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var turned = aspect.TurnedTo(aspect.CentralMeridianIII + 123);
        turned.CentralMeridianIII.ShouldBe(aspect.CentralMeridianIII + 123, 1e-12);
        var (west, north, toward) = PhysicalEphemeris.SunOnTheDisk(aspect);
        var (turnedWest, turnedNorth, turnedToward) = PhysicalEphemeris.SunOnTheDisk(turned);
        turnedWest.ShouldBe(west, 1e-12);
        turnedNorth.ShouldBe(north, 1e-12);
        turnedToward.ShouldBe(toward, 1e-12);
    }

    [Fact]
    public void ABandedPlanetsDiskMeanDoesNotChangeAsItTurns()
    {
        // A map with no longitude structure shows the same disk whichever side faces us. Turning only the central meridian moved
        // the Sun round the planet instead, and a rotation's disk means ranged 262 % (#1212).
        var map = new PlanetMap(Banded(latitude => 1 - (0.3 * Math.Exp(-Math.Pow((latitude - 10) / 5, 2)))), 360, 180);
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var (mean, min, max) = PlanetaryColour.RotationMeanIf(map, ifScale: 1, minnaertK: 0.95, aspect, stepDeg: 60);
        TestContext.Current.TestOutputHelper?.WriteLine($"disk mean {mean:0.0000}, from {min:0.0000} to {max:0.0000}");
        ((max - min) / mean).ShouldBeLessThan(0.002);
    }

    [Fact]
    public void AShrunkDiskLiesWherePlacedAtSays()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        aspect = aspect with { SubSolarLongitudeIII = aspect.CentralMeridianIII, SubSolarLatitude = aspect.SubObserverLatitude, PhaseAngle = 0 };
        var placement = new DiskPlacement(201.3, 187.8, 120, NorthAngleDeg: -90);
        var render = PlanetaryRender.Render(new PlanetMap(Uniform(1f), 360, 180), aspect, placement, 400, 400, minnaertK: 1);
        const double factor = 0.3;
        var (shrunk, width, height) = PlanetaryColour.Shrink(render, 400, 400, factor);
        var placed = PlanetaryColour.PlacedAt(placement, factor);
        double sum = 0, sx = 0, sy = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = shrunk[(y * width) + x];
                sum += v;
                sx += v * x;
                sy += v * y;
            }
        }
        (sx / sum).ShouldBe(placed.CenterX, 0.05);
        (sy / sum).ShouldBe(placed.CenterY, 0.05);
        placed.EquatorialRadius.ShouldBe(36, 1e-12);
    }

    [Fact]
    public void ABalancedDiskTakesTheTargetsColourOverItsOwnSky()
    {
        // A camera's orange disk over a grey sky: one gain a channel and the sky taken off bring the disk's mean to Jupiter's colour.
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var placement = new DiskPlacement(99.5, 101.2, 60, NorthAngleDeg: -80);
        var render = PlanetaryRender.Render(new PlanetMap(Uniform(1f), 360, 180), aspect, placement, 200, 200, minnaertK: 0.9);
        var planes = new float[3][,];
        (float Gain, float Sky)[] camera = [(0.9f, 0.04f), (0.6f, 0.03f), (0.3f, 0.05f)];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[200, 200];
            for (var i = 0; i < render.Length; i++)
            {
                planes[c][i / 200, i % 200] = (camera[c].Gain * render[i]) + camera[c].Sky;
            }
        }
        var master = new Image(planes, BitDepth.Float32, 1, 0, 0, new ImageMeta { SensorType = SensorType.Color });
        var disk = new MetricDisk(placement.CenterX, placement.CenterY, placement.EquatorialRadius, PlanetaryLimbFit.OptionsFor(aspect).AxisRatio, placement.NorthAngleDeg);
        var target = PlanetaryColourBalance.JupiterDiskColour;
        var (gains, sky) = PlanetaryColourBalance.GainsFor(master, disk, target);
        var balanced = PlanetaryColourBalance.Apply(master, gains, sky, saturation: 1, about: new LinearRgb(1, 1, 1));
        var mean = PlanetaryColour.DiskMean(balanced.GetChannelSpan(0), balanced.GetChannelSpan(1), balanced.GetChannelSpan(2), 200, 200, disk, default);
        mean.ChromaDistance(target).ShouldBeLessThan(1e-4);
        balanced.GetChannelSpan(2)[0].ShouldBe(0, 1e-4, "the sky goes to zero in every channel");
    }

    [Fact]
    public void SaturationAboutGreyKeepsEachPixelsLuminanceAndScalesItsColour()
    {
        float[][,] planes = [new float[,] { { 0.6f, 0.2f } }, new float[,] { { 0.5f, 0.25f } }, new float[,] { { 0.3f, 0.3f } }];
        var master = new Image(planes, BitDepth.Float32, 1, 0, 0, new ImageMeta { SensorType = SensorType.Color });
        var saturated = PlanetaryColourBalance.Apply(master, new LinearRgb(1, 1, 1), default, saturation: 2, about: new LinearRgb(1, 1, 1));
        for (var x = 0; x < 2; x++)
        {
            var (r, g, b) = (planes[0][0, x], planes[1][0, x], planes[2][0, x]);
            var (sr, sg, sb) = (saturated.GetChannelSpan(0)[x], saturated.GetChannelSpan(1)[x], saturated.GetChannelSpan(2)[x]);
            var y = (0.212671 * r) + (0.715160 * g) + (0.072169 * b);
            ((0.212671 * sr) + (0.715160 * sg) + (0.072169 * sb)).ShouldBe(y, 1e-6);
            (sr - y).ShouldBe(2 * (r - y), 1e-6);
            (sb - y).ShouldBe(2 * (b - y), 1e-6);
        }
    }

    [Fact]
    public void SaturationAboutAColourLeavesThatColourAloneAndKeepsLuminance()
    {
        var about = PlanetaryColourBalance.JupiterDiskColour;
        float[][,] planes = [new float[,] { { 0.5170f, 0.6f } }, new float[,] { { 0.5f, 0.5f } }, new float[,] { { 0.4305f, 0.3f } }];
        var master = new Image(planes, BitDepth.Float32, 1, 0, 0, new ImageMeta { SensorType = SensorType.Color });
        var saturated = PlanetaryColourBalance.Apply(master, new LinearRgb(1, 1, 1), default, saturation: 2, about);
        // The first pixel is the disk's colour at half its luminance-one level: left alone.
        saturated.GetChannelSpan(0)[0].ShouldBe(0.5170f, 1e-5f);
        saturated.GetChannelSpan(2)[0].ShouldBe(0.4305f, 1e-5f);
        // The second keeps its luminance and doubles its departure from the disk's colour at that luminance.
        var (r, g, b) = (0.6, 0.5, 0.3);
        var y = (0.212671 * r) + (0.715160 * g) + (0.072169 * b);
        var scale = y / ((0.212671 * about.R) + (0.715160 * about.G) + (0.072169 * about.B));
        var (sr, sb) = (saturated.GetChannelSpan(0)[1], saturated.GetChannelSpan(2)[1]);
        ((0.212671 * sr) + (0.715160 * saturated.GetChannelSpan(1)[1]) + (0.072169 * sb)).ShouldBe(y, 1e-6);
        (sr - (scale * about.R)).ShouldBe(2 * (r - (scale * about.R)), 1e-6);
        (sb - (scale * about.B)).ShouldBe(2 * (b - (scale * about.B)), 1e-6);
    }

    [Fact(Timeout = 300_000)]
    public async Task AColourJupiterIsBalancedAtTheOwnersSaturationAndAnythingElseSaysWhyNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var placement = new DiskPlacement(119.5, 121.2, 70, NorthAngleDeg: -80);
        var render = PlanetaryRender.Render(new PlanetMap(Uniform(1f), 360, 180), aspect, placement, 240, 240, minnaertK: 0.9);
        var planes = new float[3][,];
        (float Gain, float Sky)[] camera = [(0.9f, 0.04f), (0.6f, 0.03f), (0.3f, 0.05f)];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[240, 240];
            for (var i = 0; i < render.Length; i++)
            {
                planes[c][i / 240, i % 240] = (camera[c].Gain * render[i]) + camera[c].Sky;
            }
        }
        var master = new Image(planes, BitDepth.Float32, 1, 0, 0, new ImageMeta { SensorType = SensorType.Color });

        var (balance, how) = await Task.Run(() => PlanetaryColourBalance.For(master, CatalogIndex.Jupiter, Night), ct);
        TestContext.Current.TestOutputHelper?.WriteLine(how);
        var applied = balance.ShouldNotBeNull().Apply(master);
        balance.Saturation.ShouldBe(PlanetaryColourBalance.DefaultSaturation);
        balance.HeaderCards()["CBALSAT"].Value.ShouldBe(PlanetaryColourBalance.DefaultSaturation);
        var disk = new MetricDisk(placement.CenterX, placement.CenterY, placement.EquatorialRadius, PlanetaryLimbFit.OptionsFor(aspect).AxisRatio, placement.NorthAngleDeg);
        var mean = PlanetaryColour.DiskMean(applied.GetChannelSpan(0), applied.GetChannelSpan(1), applied.GetChannelSpan(2), 240, 240, disk, default);
        mean.ChromaDistance(PlanetaryColourBalance.JupiterDiskColour).ShouldBeLessThan(1e-4, "saturated about the disk's own colour, a disk of one colour keeps it");

        PlanetaryColourBalance.For(master, CatalogIndex.Mars, Night).Balance.ShouldBeNull();
        var mono = new Image([planes[1]], BitDepth.Float32, 1, 0, 0, new ImageMeta());
        PlanetaryColourBalance.For(mono, CatalogIndex.Jupiter, Night).Balance.ShouldBeNull();
    }

    [Fact(Timeout = 300_000)]
    public async Task ASaturnsGlobeIsBalancedToSaturnsColourWhereItsRingsLeaveItClear()
    {
        // A ringed Saturn through a camera's colour cast, its rings bluer than its globe: the balance reads the globe where the rings leave
        // it clear and takes it to Saturn's colour, whatever colour the rings are (S6, #1235).
        var ct = TestContext.Current.CancellationToken;
        var night = new DateTimeOffset(2022, 10, 9, 11, 25, 0, TimeSpan.Zero);
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, night);
        var options = PlanetaryLimbFit.OptionsFor(aspect);
        var rings = options.Rings ?? throw new InvalidOperationException("Saturn has rings");
        var placement = new DiskPlacement(119.6, 85.3, 34, NorthAngleDeg: 271.4);
        const int width = 240, height = 170;
        var render = PlanetaryRender.Render(new PlanetMap(Uniform(1f), 360, 180), aspect, placement, width, height, minnaertK: 0.85, supersample: 2, rings: rings);
        var disk = new MetricDisk(placement.CenterX, placement.CenterY, placement.EquatorialRadius, options.AxisRatio, placement.NorthAngleDeg)
        {
            Rings = DiskRings.Of(placement.NorthAngleDeg, placement.NorthAngleDeg, options, rings),
        };
        var planes = new float[3][,];
        (float Gain, float Sky)[] camera = [(0.9f, 0.04f), (0.6f, 0.03f), (0.3f, 0.05f)];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[height, width];
            for (var i = 0; i < render.Length; i++)
            {
                var (x, y) = (i % width, i / width);
                var tint = c == 2 && disk.RingTouched(x, y) ? 3f : 1f;
                planes[c][y, x] = (tint * camera[c].Gain * render[i]) + camera[c].Sky;
            }
        }
        var master = new Image(planes, BitDepth.Float32, 1, 0, 0, new ImageMeta { SensorType = SensorType.Color });

        var (balance, how) = await Task.Run(() => PlanetaryColourBalance.For(master, CatalogIndex.Saturn, night), ct);
        TestContext.Current.TestOutputHelper?.WriteLine(how);
        var applied = balance.ShouldNotBeNull().Apply(master);
        balance.Planet.ShouldBe(CatalogIndex.Saturn);
        how.ShouldContain("Saturn's colour");
        var mean = PlanetaryColour.DiskMean(applied.GetChannelSpan(0), applied.GetChannelSpan(1), applied.GetChannelSpan(2), width, height, disk, default);
        var ringless = disk with { Rings = null };
        var withRings = PlanetaryColour.DiskMean(applied.GetChannelSpan(0), applied.GetChannelSpan(1), applied.GetChannelSpan(2), width, height, ringless, default);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"globe R/G {mean.R / mean.G:0.0000} B/G {mean.B / mean.G:0.0000}; with the rings across it R/G {withRings.R / withRings.G:0.0000} B/G {withRings.B / withRings.G:0.0000}"));
        mean.ChromaDistance(PlanetaryColourBalance.SaturnDiskColour).ShouldBeLessThan(1e-3, "the globe clear of the rings is Saturn's colour");
        withRings.ChromaDistance(PlanetaryColourBalance.SaturnDiskColour).ShouldBeGreaterThan(0.01, "the rings across the globe are bluer, so a read through them would not be");
    }

    private static float[] Uniform(float value) => Banded(_ => value);

    // A 360 by 180 map, one sample a degree, its value a function of planetographic latitude alone.
    private static float[] Banded(Func<double, double> ofLatitude)
    {
        var values = new float[360 * 180];
        for (var row = 0; row < 180; row++)
        {
            var value = (float)ofLatitude(90 - (row + 0.5));
            for (var column = 0; column < 360; column++)
            {
                values[(row * 360) + column] = value;
            }
        }
        return values;
    }
}
