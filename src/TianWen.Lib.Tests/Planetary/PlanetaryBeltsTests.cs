using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A planet's zonal profile and its belts' edges (docs/plans/planetary-restoration.md, R6 part 3): read off a render of a banded
/// map, they land where the map puts them, and north turned over lays them the wrong way round.
/// </summary>
public class PlanetaryBeltsTests
{
    private const int Size = 128;
    private static readonly DateTimeOffset Night = new(2024, 12, 15, 13, 0, 0, TimeSpan.Zero);
    private static readonly DiskPlacement Disk = new(63.7, 64.2, 50, 30);

    // Belts with sharp, unequal edges, north and south unlike each other: a dark band from 7 to 17 N, a darker one from 9 to
    // 20 S, and a bright zone 25 to 30 N.
    private static PlanetMap BandedMap()
    {
        static double Step(double x, double at, double width) => 0.5 * (1 + Math.Tanh((x - at) / width));
        var values = new float[720 * 360];
        for (var row = 0; row < 360; row++)
        {
            var latitude = 89.75 - (row * 0.5);
            var value = 1.0 - (0.3 * Step(latitude, 7, 0.8) * (1 - Step(latitude, 17, 0.8))) - (0.4 * Step(latitude, -20, 0.8) * (1 - Step(latitude, -9, 0.8)))
                + (0.2 * Step(latitude, 25, 0.8) * (1 - Step(latitude, 30, 0.8)));
            for (var column = 0; column < 720; column++)
            {
                values[(row * 720) + column] = (float)value;
            }
        }
        return new PlanetMap(values, 720, 360);
    }

    [Fact]
    public void ARenderedPlanetsBeltsLieWhereItsMapPutsThem()
    {
        var map = BandedMap();
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var render = PlanetaryRender.Render(map, aspect, Disk, Size, Size, 0.95, supersample: 2);
        var profile = PlanetaryBelts.FromImage(render, Size, Size, new PlanetaryProjection(aspect, Disk), aspect.CentralMeridianIII, 0.95);
        var reference = PlanetaryBelts.FromMap(map, blurSigmaDeg: 0);

        var (shift, correlation) = PlanetaryBelts.Offset(profile, reference);
        TestContext.Current.TestOutputHelper?.WriteLine($"offset {shift:+0.00;-0.00} deg, correlation {correlation:0.000}");
        shift.ShouldBe(0, 0.3);
        correlation.ShouldBeGreaterThan(0.8);

        // The map's six edges, each found in the render within a quarter of a degree.
        var edges = PlanetaryBelts.Edges(reference);
        edges.Select(e => Math.Round(e.Latitude)).ShouldBe([-20, -9, 7, 17, 25, 30]);
        foreach (var edge in edges)
        {
            var found = PlanetaryBelts.Edges(profile).Where(s => Math.Sign(s.Slope) == Math.Sign(edge.Slope)).MinBy(s => Math.Abs(s.Latitude - edge.Latitude));
            found.Latitude.ShouldBe(edge.Latitude, 0.5);
        }
    }

    [Fact]
    public void AMapBlurredByACoreAndAHaloIsTheirMix()
    {
        var map = BandedMap();
        PlanetaryBelts.FromMap(map, 1.5, 0, 0).Albedo.ShouldBe(PlanetaryBelts.FromMap(map, 1.5).Albedo);
        var mixed = PlanetaryBelts.FromMap(map, 1.5, 0.3, 4).Albedo;
        var (core, halo) = (PlanetaryBelts.FromMap(map, 1.5).Albedo, PlanetaryBelts.FromMap(map, 4).Albedo);
        for (var b = 0; b < mixed.Length; b += 17)
        {
            if (double.IsFinite(mixed[b]))
            {
                mixed[b].ShouldBe((0.7 * core[b]) + (0.3 * halo[b]), 1e-9);
            }
        }
    }

    [Fact]
    public void NorthTurnedOverMirrorsTheBelts()
    {
        var map = BandedMap();
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var render = PlanetaryRender.Render(map, aspect, Disk, Size, Size, 0.95, supersample: 2);
        var reference = PlanetaryBelts.FromMap(map, blurSigmaDeg: 0);
        var right = PlanetaryBelts.Offset(PlanetaryBelts.FromImage(render, Size, Size, new PlanetaryProjection(aspect, Disk), aspect.CentralMeridianIII, 0.95), reference);
        var turned = PlanetaryBelts.Offset(PlanetaryBelts.FromImage(render, Size, Size, new PlanetaryProjection(aspect, Disk with { NorthAngleDeg = Disk.NorthAngleDeg + 180 }),
            aspect.CentralMeridianIII, 0.95), reference);
        TestContext.Current.TestOutputHelper?.WriteLine($"correlation: north right {right.Correlation:0.000}, turned over {turned.Correlation:0.000}");
        turned.Correlation.ShouldBeLessThan(right.Correlation - 0.3);
    }
}
