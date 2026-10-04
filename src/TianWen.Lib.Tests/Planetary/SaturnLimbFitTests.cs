using System;
using System.Globalization;
using System.Linq;
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
/// T1 for Saturn (docs/plans/planetary-restoration.md, S2, #1232): the limb fit with the rings in its model, measured on a Saturn the
/// render drew (S1) at the 2022-10-09 colour capture's geometry and scale, through the Newtonian and T1's three Moffat seeings. The rule,
/// set before measuring: the centre within 0.2 px, the equatorial radius within 0.5 % and the axis within 0.2 degree.
/// </summary>
public class SaturnLimbFitTests
{
    private static readonly DateTimeOffset Capture = new DateTimeOffset(2022, 10, 9, 11, 25, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(3.0, 3.0)]
    [InlineData(6.0, 3.0)]
    [InlineData(6.0, 2.0)]
    public void TheLimbFitFindsARenderedSaturnWhereItWasPut(double seeingFwhm, double beta)
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, Capture);
        // The capture's own scale: its restacked master's globe fits at 29.6 px (#1237), 8.95 arcsec of equatorial radius.
        const double Scale = 0.302;
        var radius = aspect.AngularDiameterArcsec / 2 / Scale;
        var placement = new DiskPlacement(160.37, 120.62, radius, NorthAngleDeg: 103.7);
        var newtonian = new Pupil(0.254, ObstructionRatio: 58.0 / 254, Vanes: 4, VaneWidthM: 0.001, VaneAngleDeg: 28);
        var truth = PlanetaryRender.RenderDiffracted(SyntheticSaturn(), aspect, placement, 320, 240, minnaertK: 0.9, newtonian, 550e-9, Scale,
            rings: SaturnRings.Main);
        var seen = PsfKernel.Moffat(seeingFwhm, beta).Convolve(truth, 320, 240);

        var image = new float[240, 320];
        for (var y = 0; y < 240; y++)
        {
            for (var x = 0; x < 320; x++)
            {
                image[y, x] = seen[(y * 320) + x];
            }
        }
        var options = PlanetaryLimbFit.OptionsFor(aspect);
        var fit = PlanetaryLimbFit.Fit(Image.FromChannel(image), options);

        fit.ShouldNotBeNull();
        var f = fit.Value;
        var radiusError = (f.EquatorialRadius - radius) / radius;
        var axisError = Math.Abs(Math.IEEERemainder(f.AxisAngleDeg - placement.NorthAngleDeg, 180));
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"seeing FWHM {seeingFwhm} px, beta {beta}: centre off {f.CenterX - placement.CenterX:+0.000;-0.000}, {f.CenterY - placement.CenterY:+0.000;-0.000} px, " +
            $"radius {f.EquatorialRadius:0.000} against {radius:0.000} ({radiusError:+0.00%;-0.00%}), axis off {axisError:0.000} deg, north {f.NorthAngleDeg:0.0} (put at {placement.NorthAngleDeg}), " +
            $"k {f.LimbDarkening:0.00}, sigma {f.PsfSigma:0.00}, wing {f.HaloFraction:0.000} at {f.HaloWidth:0.00} px, " +
            $"rings {string.Join(", ", (f.RingLevels ?? []).Select(l => l.ToString("0.000", CultureInfo.InvariantCulture)))}, rms {f.RmsResidual:0.0000}, {f.Iterations} iterations"));
        Math.Abs(f.CenterX - placement.CenterX).ShouldBeLessThan(0.2, "the centre, x");
        Math.Abs(f.CenterY - placement.CenterY).ShouldBeLessThan(0.2, "the centre, y");
        Math.Abs(radiusError).ShouldBeLessThan(0.005, "the equatorial radius");
        axisError.ShouldBeLessThan(0.2, "the axis");
    }

    [Fact]
    public void TheFlatFitReadsTheLightBesideTheCassiniDivisionIntoIt()
    {
        // The limb fit's rings are four flat levels; a real B ring brightens outward to the division and the A ring dims outward, and
        // the real 2022-10-09 capture's fit reads the division at 0.40 to 0.45 of the globe. Drawn flat, a twin's division read 0.01;
        // drawn with SaturnRings.Structured it reads as the real one does (S3, #1233).
        var flat = DivisionRead(SaturnRings.Main);
        var structured = DivisionRead(SaturnRings.Structured(0.12, 0.85, 0.1, 0.6));
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"the division read at {flat.Division:0.000} of the globe drawn flat (B {flat.B:0.000}, A {flat.A:0.000}), at {structured.Division:0.000} drawn structured (B {structured.B:0.000}, A {structured.A:0.000})"));
        flat.Division.ShouldBeLessThan(0.1);
        structured.Division.ShouldBeGreaterThan(0.3);

        static (double B, double Division, double A) DivisionRead(SaturnRings rings)
        {
            var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, Capture);
            // A colour plane of the capture: 0.302"/px at twice the pitch, through its seeing.
            const double Scale = 0.604;
            var placement = new DiskPlacement(80.3, 60.4, aspect.AngularDiameterArcsec / 2 / Scale, NorthAngleDeg: 91.4);
            var newtonian = new Pupil(0.254, ObstructionRatio: 58.0 / 254, Vanes: 4, VaneWidthM: 0.001, VaneAngleDeg: 28);
            var truth = PlanetaryRender.RenderDiffracted(SyntheticSaturn(), aspect, placement, 160, 120, minnaertK: 0.85, newtonian, 535e-9, Scale, rings: rings);
            var seen = PsfKernel.Moffat(3.5, 3).Convolve(truth, 160, 120);
            var image = new float[120, 160];
            for (var y = 0; y < 120; y++)
            {
                for (var x = 0; x < 160; x++)
                {
                    image[y, x] = seen[(y * 160) + x];
                }
            }
            var levels = PlanetaryLimbFit.Fit(Image.FromChannel(image), PlanetaryLimbFit.OptionsFor(aspect))?.RingLevels ?? throw new InvalidOperationException("no ringed fit");
            return (levels[1], levels[2], levels[3]);
        }
    }

    // A Saturn to measure against: a bright equatorial zone, belts where Saturn's lie (planetographic), and the polar regions darker
    // from 60 degrees, the north's hexagon among them. Not a likeness; what matters is that the globe is not uniform where the fit
    // assumes it is.
    internal static PlanetMap SyntheticSaturn()
    {
        const int W = 1440, H = 720;
        var values = new float[W * H];
        for (var row = 0; row < H; row++)
        {
            var latitude = 90 - ((row + 0.5) * 180.0 / H);
            var albedo = 1.0;
            albedo -= Band(latitude, 12, 22, 0.18);   // North Equatorial Belt
            albedo -= Band(latitude, -22, -12, 0.15); // South Equatorial Belt
            albedo -= Band(latitude, 35, 42, 0.1);    // North Temperate Belt
            albedo *= 1 - (0.35 * Smooth((Math.Abs(latitude) - 60) / 20));
            for (var column = 0; column < W; column++)
            {
                values[(row * W) + column] = (float)albedo;
            }
        }
        return new PlanetMap(values, W, H);
    }

    private static double Band(double latitude, double from, double to, double depth)
        => latitude > from && latitude < to ? depth * Math.Sin(Math.PI * (latitude - from) / (to - from)) : 0;

    private static double Smooth(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - (2 * t));
}
