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
/// The derived sharpening on Saturn (S4 of docs/plans/planetary-restoration.md, #1184): the metrics and the sharpening read the planet as
/// its globe and its rings (<see cref="MetricDisk.Rings"/>), its limb where the rings leave it clear, its sky past them, and no ring ansa
/// for a moon.
/// </summary>
public class SaturnSharpeningTests
{
    private static readonly DateTimeOffset Capture = new DateTimeOffset(2022, 10, 9, 11, 25, 0, TimeSpan.Zero);

    // A disk 30 px in radius, its axis along +y (north up the image, toward -y), at the capture's tilt.
    private static MetricDisk Disk()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, Capture);
        var options = PlanetaryLimbFit.OptionsFor(aspect);
        var rings = options.Rings ?? throw new InvalidOperationException("Saturn's options carry its rings");
        return new MetricDisk(100, 100, 30, options.AxisRatio, AxisAngleDeg: 90)
        {
            Rings = DiskRings.Of(northAngleDeg: 270, axisAngleDeg: 90, options, rings),
        };
    }

    [Fact]
    public void ADiskWithRingsReadsItsSkyPastThem()
    {
        var disk = Disk();
        var rings = disk.Rings ?? throw new InvalidOperationException();
        // Along the equator the planet ends at the A ring's edge, along the axis at the globe's limb.
        disk.ClearRadiiAt(100 + (30 * rings.OuterRadii), 100).ShouldBe(1, 1e-9);
        disk.ClearRadiiAt(100, 100 + (30 * disk.AxisRatio)).ShouldBe(1, 1e-9);
        disk.RadiiAt(100 + (30 * rings.OuterRadii), 100).ShouldBe(rings.OuterRadii, 1e-9);
        // The ansae are rings; the sky beyond them and the polar limb are not.
        disk.RingTouched(100 + (30 * 1.8), 100).ShouldBeTrue();
        disk.RingTouched(100 + (30 * (rings.OuterRadii + 0.3)), 100).ShouldBeFalse();
        disk.RingTouched(100, 100 - (30 * 0.95 * disk.AxisRatio)).ShouldBeFalse();
        // North is up the image (-y) and the observer sees it (B > 0), so the rings' near half crosses the globe below the centre: the
        // band there is ring, its mirror above the centre is globe the far half lies behind.
        var crossing = 30 * 1.8 * rings.SinB;
        disk.RingTouched(100, 100 + crossing).ShouldBeTrue();
        disk.RingTouched(100, 100 - crossing).ShouldBeFalse();
        // Without rings, the old disk, bit for bit.
        var plain = disk with { Rings = null };
        plain.ClearRadiiAt(150, 120).ShouldBe(plain.RadiiAt(150, 120));
        plain.RingTouched(150, 100).ShouldBeFalse();
    }

    [Fact]
    public void TheDerivedSharpeningSharpensASaturnAndKeepsItsRings()
    {
        // A Saturn drawn by S1 through the Newtonian, blurred by a seeing the sharpening does not know, with a little noise: the
        // derivation runs (no preset), and the globe, the limb and the rings come out truer than the blurred plane, nothing standing
        // past the rings above what the blur left there.
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, Capture);
        const double Scale = 0.302;
        const int Width = 320, Height = 240;
        var placement = new DiskPlacement(160.37, 120.62, aspect.AngularDiameterArcsec / 2 / Scale, NorthAngleDeg: 271.4);
        var newtonian = new Pupil(0.254, ObstructionRatio: 58.0 / 254, Vanes: 4, VaneWidthM: 0.001, VaneAngleDeg: 28);
        var rings = SaturnRings.Structured(0.17, 0.70, 0.1, 0.50);
        var truth = PlanetaryRender.RenderDiffracted(SaturnLimbFitTests.SyntheticSaturn(), aspect, placement, Width, Height, minnaertK: 0.85, newtonian, 550e-9, Scale,
            rings: rings);
        var seen = PsfKernel.Moffat(4.5, 3).Convolve(truth, Width, Height);
        var random = new Random(11);
        var peak = seen.Max();
        var plane = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                plane[y, x] = (float)(0.02 + (0.6 * seen[(y * Width) + x] / peak) + (0.002 * PhaseScreen.Gaussian(random)));
            }
        }
        var input = Image.FromChannel(plane);

        var result = PlanetarySharpening.Sharpen(input, new PlanetarySharpenOptions(CatalogIndex.Saturn, Capture, newtonian));

        result.ShouldNotBeNull();
        result.Derived.ShouldBeTrue();
        var options = PlanetaryLimbFit.OptionsFor(aspect);
        var disk = new MetricDisk(placement.CenterX, placement.CenterY, placement.EquatorialRadius, options.AxisRatio, placement.NorthAngleDeg)
        {
            Rings = DiskRings.Of(placement.NorthAngleDeg, placement.NorthAngleDeg, options, options.Rings ?? throw new InvalidOperationException()),
        };
        var reference = PlanetaryMetrics.Normalise(truth, Width, Height, disk);
        var before = PlanetaryMetrics.Normalise(input.GetChannelSpan(0), Width, Height, disk);
        var after = PlanetaryMetrics.Normalise(result.Sharpened.GetChannelSpan(0), Width, Height, disk);
        var (bandsBefore, bandsAfter) = (Bands(before), Bands(after));
        var (limbBefore, limbAfter) = (PlanetaryMetrics.LimbProfileError(before, reference, Width, Height, disk), PlanetaryMetrics.LimbProfileError(after, reference, Width, Height, disk));
        var (ringsBefore, pastBefore) = PlanetaryMetrics.RingProfileError(before, reference, Width, Height, disk);
        var (ringsAfter, pastAfter) = PlanetaryMetrics.RingProfileError(after, reference, Width, Height, disk);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"gains {string.Join(", ", result.Gains.Select(g => g.ToString("0.00", CultureInfo.InvariantCulture)))}; bands 1 to 4 {bandsBefore:0.000} -> {bandsAfter:0.000}; " +
            $"limb {limbBefore:0.0000} -> {limbAfter:0.0000}; rings {ringsBefore:0.0000} -> {ringsAfter:0.0000}; past their edge {pastBefore:+0.0000;-0.0000} -> {pastAfter:+0.0000;-0.0000}"));
        bandsAfter.ShouldBeLessThan(0.8 * bandsBefore);
        limbAfter.ShouldBeLessThan(limbBefore);
        ringsAfter.ShouldBeLessThan(ringsBefore);
        pastAfter.ShouldBeLessThanOrEqualTo(pastBefore);
        result.Sharpened.Release();

        // The viewer's and the GUI's way in derives too, where it used to answer with the preset (#1184).
        var (gains, how, limb) = PlanetaryBestStack.DeriveGains(input, CatalogIndex.Saturn, Capture, newtonian);
        gains.IsDefaultOrEmpty.ShouldBeFalse(how);
        limb.ShouldNotBeNull();

        double Bands(float[] normalised) => PlanetaryMetrics.Fidelity(normalised, reference, Width, Height, disk).Take(4).Sum(b => b.Error);
    }
}
