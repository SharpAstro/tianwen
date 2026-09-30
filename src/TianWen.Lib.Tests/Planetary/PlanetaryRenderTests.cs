using System;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The rendered truth (docs/plans/planetary-restoration.md, T1 and R2 part 1, #1050): first that the render puts every point
/// where the spheroid, the ephemeris and the map's conventions say (each against a formula of its own, never against the
/// limb fit), then T1 itself, the limb fit measured on a rendered Jupiter whose geometry is known exactly.
/// </summary>
public class PlanetaryRenderTests
{
    private static readonly DateTimeOffset Night = new DateTimeOffset(2022, 9, 3, 12, 10, 0, TimeSpan.Zero);

    // Jupiter as it was on 2022-09-03, lit from the observer's own direction: no phase.
    private static PlanetAspect FullyLit(PlanetAspect aspect)
        => aspect with { SubSolarLongitudeIII = aspect.CentralMeridianIII, SubSolarLatitude = aspect.SubObserverLatitude, PhaseAngle = 0 };

    private static PlanetAspect EquatorOn(PlanetAspect aspect)
        => aspect with { SubObserverLatitude = 0, SubObserverLatitudeCentric = 0, SubSolarLatitude = 0 };

    private static PlanetMap Uniform() => new PlanetMap(Fill(360, 180, (_, _) => 1f), 360, 180);

    private static float[] Fill(int width, int height, Func<double, double, float> albedo)
    {
        var values = new float[width * height];
        for (var row = 0; row < height; row++)
        {
            var latitude = 90 - ((row + 0.5) * 180.0 / height);
            for (var column = 0; column < width; column++)
            {
                values[(row * width) + column] = albedo(latitude, 360 - ((column + 0.5) * 360.0 / width));
            }
        }
        return values;
    }

    [Fact]
    public void AUniformlyBrightDiskHasTheEphemerisShapeWhereItWasPut()
    {
        // At no phase Minnaert's k = 0.5 lights every point of the disk alike (mu0^k mu^(k-1) = 1), so the render is the
        // outline alone: its area, centroid and second moments are the ellipse's.
        var aspect = FullyLit(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night));
        var placement = new DiskPlacement(63.3, 64.7, 40, NorthAngleDeg: 110);
        var image = PlanetaryRender.Render(Uniform(), aspect, placement, 128, 128, minnaertK: 0.5);

        var (sum, cx, cy, angle, ratio) = Moments(image, 128, 128);
        var expectedRatio = PlanetaryLimbFit.ApparentAxisRatio(aspect.Flattening, aspect.SubObserverLatitudeCentric);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"area {sum:0.0} (ellipse {Math.PI * 40 * 40 * expectedRatio:0.0}), centre {cx:0.000}, {cy:0.000}, minor axis {angle:0.00} deg, ratio {ratio:0.0000} (ephemeris {expectedRatio:0.0000})");

        sum.ShouldBe(Math.PI * 40 * 40 * expectedRatio, Math.PI * 40 * 40 * 0.001);
        cx.ShouldBe(63.3, 0.01);
        cy.ShouldBe(64.7, 0.01);
        // The short axis is the polar one.
        AngleBetweenAxes(angle, 110).ShouldBeLessThan(0.2);
        ratio.ShouldBe(expectedRatio, 0.001);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMeridianThirtyDegreesPastTheCentralOneLiesOnTheEastSide(bool mirrored)
    {
        // A map dark but for a bright meridian at 200 degrees west. With the central meridian at 170 that meridian is 30
        // degrees east of it (a point east of the central meridian has not yet crossed it, so its west longitude is the
        // larger), and east lies left of north up unless the image is mirrored.
        var stripe = new PlanetMap(Fill(3600, 1800, (_, west) => Math.Abs(west - 200) < 0.5 ? 1f : 0f), 3600, 1800);
        var aspect = EquatorOn(FullyLit(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night))) with { CentralMeridianIII = 170, SubSolarLongitudeIII = 170 };
        var placement = new DiskPlacement(100, 100, 80, NorthAngleDeg: -90, mirrored);
        var image = PlanetaryRender.Render(stripe, aspect, placement, 200, 200, minnaertK: 0.5);

        var peak = PeakAlongRow(image, 200, 100);
        var expected = 100 + ((mirrored ? 1 : -1) * 80 * Math.Sin(30 * Math.PI / 180));
        TestContext.Current.TestOutputHelper?.WriteLine($"mirrored {mirrored}: the meridian crosses the equator at x {peak:0.00} (expected {expected:0.00})");
        peak.ShouldBe(expected, 0.2);

        // And at the central meridian itself, it runs through the centre.
        var onCentral = PlanetaryRender.Render(stripe, aspect with { CentralMeridianIII = 200, SubSolarLongitudeIII = 200 }, placement, 200, 200, minnaertK: 0.5);
        PeakAlongRow(onCentral, 200, 100).ShouldBe(100, 0.1);
    }

    [Fact]
    public void ALatitudeCircleLiesWhereTheOblateSpheroidPutsIt()
    {
        // A bright circle at 40 degrees planetographic, seen equator-on. On the central meridian it lies at q sin(beta) of the
        // equatorial radius above the centre, beta the reduced latitude, tan(beta) = q tan(40): 0.577 radii for Jupiter,
        // where a sphere would put it at 0.643 and a planetocentric reading of the map at 0.555.
        var circle = new PlanetMap(Fill(3600, 1800, (latitude, _) => Math.Abs(latitude - 40) < 0.5 ? 1f : 0f), 3600, 1800);
        var aspect = EquatorOn(FullyLit(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night)));
        var placement = new DiskPlacement(100, 100, 80, NorthAngleDeg: -90);
        var image = PlanetaryRender.Render(circle, aspect, placement, 200, 200, minnaertK: 0.5);

        var q = 1 - aspect.Flattening;
        var beta = Math.Atan(q * Math.Tan(40 * Math.PI / 180));
        var expected = 100 - (80 * q * Math.Sin(beta));
        var peak = PeakAlongColumn(image, 200, 200, 100);
        TestContext.Current.TestOutputHelper?.WriteLine($"40 degrees north crosses the central meridian at y {peak:0.00} (expected {expected:0.00})");
        peak.ShouldBe(expected, 0.2);
    }

    [Fact]
    public void ThePhaseDarkensTheLimbAwayFromTheSun()
    {
        // The Sun 20 degrees west of the central meridian lights the west limb and leaves a terminator on the east: with north
        // up, west is on the right.
        var aspect = EquatorOn(FullyLit(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night)));
        aspect = aspect with { SubSolarLongitudeIII = aspect.CentralMeridianIII - 20, PhaseAngle = 20 };
        var image = PlanetaryRender.Render(Uniform(), aspect, new DiskPlacement(100, 100, 80, NorthAngleDeg: -90), 200, 200, minnaertK: 1);

        double east = 0, west = 0;
        for (var y = 0; y < 200; y++)
        {
            for (var x = 0; x < 200; x++)
            {
                if (x < 100)
                {
                    east += image[(y * 200) + x];
                }
                else if (x > 100)
                {
                    west += image[(y * 200) + x];
                }
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"west half {west:0.0}, east half {east:0.0}");
        west.ShouldBeGreaterThan(east * 1.2);
        // The east limb itself is past the terminator, dark.
        image[(100 * 200) + 22].ShouldBe(0f);
    }

    [Fact]
    public void DiffractionKeepsTheFluxAndSoftensTheLimb()
    {
        var aspect = FullyLit(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night));
        var placement = new DiskPlacement(63.3, 64.7, 40, NorthAngleDeg: 110);
        var sharp = PlanetaryRender.Render(Uniform(), aspect, placement, 128, 128, minnaertK: 0.5);
        var newtonian = new Pupil(0.254, ObstructionRatio: 58.0 / 254, Vanes: 4, VaneWidthM: 0.001);
        var diffracted = PlanetaryRender.RenderDiffracted(Uniform(), aspect, placement, 128, 128, minnaertK: 0.5, newtonian, 550e-9, arcsecPerPixel: 0.49);

        var (sharpSum, sharpX, sharpY, _, _) = Moments(sharp, 128, 128);
        var (sum, x, y, _, _) = Moments(diffracted, 128, 128);
        TestContext.Current.TestOutputHelper?.WriteLine($"flux {sum:0.0} against {sharpSum:0.0}; centre moved {x - sharpX:+0.000;-0.000}, {y - sharpY:+0.000;-0.000} px");
        sum.ShouldBe(sharpSum, sharpSum * 0.002);
        x.ShouldBe(sharpX, 0.01);
        y.ShouldBe(sharpY, 0.01);
        // At 0.49"/px the Airy core is under a pixel; at 0.05"/px it spans five, and the limb takes more pixels to fall.
        var fineSharp = PlanetaryRender.Render(Uniform(), aspect, placement, 128, 128, minnaertK: 0.5);
        var fineDiffracted = PlanetaryRender.RenderDiffracted(Uniform(), aspect, placement, 128, 128, minnaertK: 0.5, newtonian, 550e-9, arcsecPerPixel: 0.05);
        TestContext.Current.TestOutputHelper?.WriteLine($"edge 90 to 10 percent: {EdgeWidth(fineSharp, 128, 64)} px sharp, {EdgeWidth(fineDiffracted, 128, 64)} px through the pupil at 0.05\"/px");
        EdgeWidth(fineDiffracted, 128, 64).ShouldBeGreaterThan(EdgeWidth(fineSharp, 128, 64) + 1);
    }

    /// <summary>
    /// T1 (docs/plans/planetary-restoration.md, R1's pre-registered geometry, decided here): a Jupiter with belts, dark poles
    /// and a red spot, rendered at 2022-09-03's geometry through the Newtonian and blurred by a Moffat seeing the fit's
    /// Gaussian does not model, is fitted with <see cref="PlanetaryLimbFit"/>. The pre-registration: centre within 0.2 px,
    /// equatorial radius within 0.5 %.
    /// </summary>
    [Theory]
    [InlineData(3.0, 3.0)]
    [InlineData(6.0, 3.0)]
    [InlineData(6.0, 2.0)]
    public void TheLimbFitFindsARenderedJupiterWhereItWasPut(double seeingFwhm, double beta)
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var scale = 0.487;
        var radius = aspect.AngularDiameterArcsec / 2 / scale;
        var placement = new DiskPlacement(160.37, 120.62, radius, NorthAngleDeg: 112.3);
        var newtonian = new Pupil(0.254, ObstructionRatio: 58.0 / 254, Vanes: 4, VaneWidthM: 0.001, VaneAngleDeg: 28);
        var truth = PlanetaryRender.RenderDiffracted(SyntheticJupiter(), aspect, placement, 320, 240, minnaertK: 0.95, newtonian, 550e-9, scale);
        var seen = PsfKernel.Moffat(seeingFwhm, beta).Convolve(truth, 320, 240);

        var image = new float[240, 320];
        for (var y = 0; y < 240; y++)
        {
            for (var x = 0; x < 320; x++)
            {
                image[y, x] = seen[(y * 320) + x];
            }
        }
        var fit = PlanetaryLimbFit.Fit(Image.FromChannel(image), PlanetaryLimbFit.OptionsFor(aspect));

        fit.ShouldNotBeNull();
        var f = fit.Value;
        var radiusError = (f.EquatorialRadius - radius) / radius;
        var sun = PhysicalEphemeris.SunOnTheDisk(aspect);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"the Sun in the disk frame: west {sun.West:+0.00000;-0.00000}, north {sun.North:+0.00000;-0.00000}, toward {sun.Toward:0.00000}; " +
            $"phase {Math.Acos(sun.Toward) * 180 / Math.PI:0.000} deg (ephemeris {aspect.PhaseAngle:0.000}); the lit side {Math.Atan2(sun.North, Math.Abs(sun.West)) * 180 / Math.PI:+0.0;-0.0} deg off the equator");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"seeing FWHM {seeingFwhm} px, beta {beta}: centre off {f.CenterX - placement.CenterX:+0.000;-0.000}, {f.CenterY - placement.CenterY:+0.000;-0.000} px, " +
            $"radius {f.EquatorialRadius:0.000} against {radius:0.000} ({radiusError:+0.00%;-0.00%}), axis {f.AxisAngleDeg:0.00} deg, k {f.LimbDarkening:0.00}, sigma {f.PsfSigma:0.00}, " +
            $"wing {f.HaloFraction:0.000} at {f.HaloWidth:0.00} px, side {f.SunSide}");
        Math.Abs(f.CenterX - placement.CenterX).ShouldBeLessThan(0.2);
        Math.Abs(f.CenterY - placement.CenterY).ShouldBeLessThan(0.2);
        Math.Abs(radiusError).ShouldBeLessThan(0.005);
    }

    // A Jupiter to measure against: belts where Jupiter's are (planetographic), the polar regions darker from 50 degrees, and
    // a red spot at 22 degrees south. Not a likeness; what matters is that the disk is not uniform where the fit assumes it is.
    private static PlanetMap SyntheticJupiter()
        => new PlanetMap(Fill(1440, 720, (latitude, west) =>
        {
            var albedo = 1.0;
            albedo -= Band(latitude, 7, 17, 0.35);   // North Equatorial Belt
            albedo -= Band(latitude, -20, -7, 0.3);  // South Equatorial Belt
            albedo -= Band(latitude, 24, 30, 0.2);   // North Temperate Belt
            albedo -= Band(latitude, -30, -26, 0.15);
            albedo *= 1 - (0.45 * Smooth((Math.Abs(latitude) - 50) / 25));
            var dLat = (latitude + 22) / 5;
            var dLon = Math.IEEERemainder(west - 60, 360) / 9;
            albedo -= 0.3 * Math.Exp(-((dLat * dLat) + (dLon * dLon)));
            return (float)albedo;
        }), 1440, 720);

    private static double Band(double latitude, double from, double to, double depth)
        => latitude > from && latitude < to ? depth * Math.Sin(Math.PI * (latitude - from) / (to - from)) : 0;

    private static double Smooth(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - (2 * t));

    // The weighted area, centroid, the direction of the minor axis (degrees, from +x toward +y) and the axis ratio from the
    // second moments.
    private static (double Sum, double X, double Y, double MinorAxisDeg, double Ratio) Moments(float[] image, int width, int height)
    {
        double sum = 0, sx = 0, sy = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var w = image[(y * width) + x];
                sum += w;
                sx += w * x;
                sy += w * y;
            }
        }
        var cx = sx / sum;
        var cy = sy / sum;
        double xx = 0, yy = 0, xy = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var w = image[(y * width) + x];
                xx += w * (x - cx) * (x - cx);
                yy += w * (y - cy) * (y - cy);
                xy += w * (x - cx) * (y - cy);
            }
        }
        var trace = xx + yy;
        var root = Math.Sqrt(((xx - yy) * (xx - yy)) + (4 * xy * xy));
        var major = (trace + root) / 2;
        var minor = (trace - root) / 2;
        var majorAngle = 0.5 * Math.Atan2(2 * xy, xx - yy) * 180 / Math.PI;
        return (sum, cx, cy, majorAngle + 90, Math.Sqrt(minor / major));
    }

    private static double AngleBetweenAxes(double a, double b)
    {
        var d = Math.Abs(Math.IEEERemainder(a - b, 180));
        return d;
    }

    // The intensity-weighted position of the brightest run along a row (within 3 px of its maximum).
    private static double PeakAlongRow(float[] image, int width, int row)
    {
        var best = 0;
        for (var x = 1; x < width; x++)
        {
            if (image[(row * width) + x] > image[(row * width) + best])
            {
                best = x;
            }
        }
        double sum = 0, weighted = 0;
        for (var x = Math.Max(0, best - 3); x <= Math.Min(width - 1, best + 3); x++)
        {
            sum += image[(row * width) + x];
            weighted += image[(row * width) + x] * x;
        }
        return weighted / sum;
    }

    private static double PeakAlongColumn(float[] image, int width, int height, int column)
    {
        var best = 0;
        for (var y = 1; y < height; y++)
        {
            if (image[(y * width) + column] > image[(best * width) + column])
            {
                best = y;
            }
        }
        double sum = 0, weighted = 0;
        for (var y = Math.Max(0, best - 3); y <= Math.Min(height - 1, best + 3); y++)
        {
            sum += image[(y * width) + column];
            weighted += image[(y * width) + column] * y;
        }
        return weighted / sum;
    }

    // How many pixels the edge takes to fall from 90 to 10 percent of the disk's level, along a row outward from the centre.
    private static int EdgeWidth(float[] image, int width, int row)
    {
        var level = image[(row * width) + (width / 2)];
        int? high = null, low = null;
        for (var x = width / 2; x < width; x++)
        {
            var v = image[(row * width) + x];
            if (high is null && v < 0.9 * level)
            {
                high = x;
            }
            if (low is null && v < 0.1 * level)
            {
                low = x;
            }
        }
        return (low ?? width) - (high ?? width);
    }
}
