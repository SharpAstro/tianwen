using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What moves against System III (docs/plans/planetary-restoration.md, R6 part 3): a band of a rendered planet given a wind
/// between two instants is found moving by it, and the rest is found at rest.
/// </summary>
public class PlanetaryZonalDriftTests
{
    private const int Size = 128;
    private static readonly DateTimeOffset Night = new(2024, 12, 15, 13, 0, 0, TimeSpan.Zero);
    private static readonly DiskPlacement Disk = new(63.7, 64.2, 50, 30);

    // Features every 20 degrees of longitude at every latitude, and the band from 20 to 28 N carried `eastDeg` east.
    private static PlanetMap Map(double eastDeg)
    {
        var values = new float[720 * 360];
        for (var row = 0; row < 360; row++)
        {
            var latitude = 89.75 - (row * 0.5);
            var moved = latitude is > 20 and < 28 ? eastDeg : 0;
            for (var column = 0; column < 720; column++)
            {
                // Column 0 begins at 360 degrees west, falling to the right (PlanetMap). A feature carried east comes to a smaller
                // west longitude, so the map at W shows what lay at W + east.
                var west = 360 - (360.0 * (column + 0.5) / 720) + moved;
                values[(row * 720) + column] = (float)(1 + (0.25 * Math.Cos(18 * west * Math.PI / 180) * Math.Cos(2 * latitude * Math.PI / 180)));
            }
        }
        return new PlanetMap(values, 720, 360);
    }

    [Fact]
    public void ABandCarriedEastIsFoundMovingAndTheRestAtRest()
    {
        var (earlier, later) = (PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night), PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night.AddMinutes(30)));
        var first = Image.FromChannel(ToPlane(PlanetaryRender.Render(Map(0), earlier, Disk, Size, Size, 0.95, supersample: 2)));
        var second = Image.FromChannel(ToPlane(PlanetaryRender.Render(Map(0.6), later, Disk, Size, Size, 0.95, supersample: 2)));

        const double step = 0.25;
        var (minLatitude, rows) = (-40.0, 321);
        var westLow = Math.Max(earlier.CentralMeridianIII, later.CentralMeridianIII) - 40;
        var columns = (int)Math.Floor((Math.Min(earlier.CentralMeridianIII, later.CentralMeridianIII) + 40 - westLow) / step) + 1;
        var a = PlanetaryZonalDrift.Project(first, new PlanetaryProjection(earlier, Disk), 0.95, minLatitude, rows, westLow, columns, step);
        var b = PlanetaryZonalDrift.Project(second, new PlanetaryProjection(later, Disk), 0.95, minLatitude, rows, westLow, columns, step);
        var drift = PlanetaryZonalDrift.Drift(a, b, step);

        double ShiftAround(double latitude) => Enumerable.Range(0, rows).Where(r => Math.Abs(minLatitude + (r * step) - latitude) <= 1)
            .Select(r => drift[r].ShiftDeg).Where(double.IsFinite).Average();
        var (moving, still) = (ShiftAround(24), ShiftAround(0));
        TestContext.Current.TestOutputHelper?.WriteLine($"shift around 24 N {moving:+0.000;-0.000} deg, around the equator {still:+0.000;-0.000} deg");
        // Carried 0.6 degrees east, the band comes 0.6 degrees lower in west longitude; the equator stays put.
        moving.ShouldBe(-0.6, 0.1);
        still.ShouldBe(0, 0.05);
    }

    [Fact]
    public void TheNorthTemperateJetMovesAQuarterOfADegreeInHalfAnHour()
    {
        // 150 m/s east at 24 N (the jet Tollefson et al. 2017 measure at 144 to 160 m/s) on a circle of latitude 66,000 km in
        // radius is 0.234 degrees of longitude in 30 minutes, to smaller west longitudes.
        PlanetaryZonalDrift.WindOf(-0.234, 1800, 24).ShouldBe(150, 1);
        PlanetaryZonalDrift.WindOf(0.234, 1800, 24).ShouldBe(-150, 1);
    }

    private static float[,] ToPlane(float[] values)
    {
        var plane = new float[Size, Size];
        Buffer.BlockCopy(values, 0, plane, 0, values.Length * sizeof(float));
        return plane;
    }
}
