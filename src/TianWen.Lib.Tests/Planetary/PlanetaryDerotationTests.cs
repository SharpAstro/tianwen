using System;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A planet's image carried through its own rotation (docs/plans/planetary-restoration.md, R6): the spheroid's projection both
/// ways, and a de-rotation judged against the planet rendered at the target instant.
/// </summary>
public class PlanetaryDerotationTests
{
    private const int Size = 128;
    private static readonly DateTimeOffset Night = new(2024, 12, 15, 12, 56, 44, TimeSpan.Zero);
    private static readonly DiskPlacement Disk = new(63.7, 64.2, 50, 30);

    // Belts, and features along them every 60 degrees of longitude, smooth enough for a render and a Lanczos-3 resample to
    // follow: a map whose rotation shows, which a banded map's would not.
    private static PlanetMap SpottedMap()
    {
        var values = new float[360 * 180];
        for (var row = 0; row < 180; row++)
        {
            var latitude = (89.5 - row) * Math.PI / 180;
            for (var column = 0; column < 360; column++)
            {
                var longitude = (column + 0.5) * Math.PI / 180;
                values[(row * 360) + column] = (float)(1 - (0.3 * Math.Pow(Math.Sin(4 * latitude), 2)) + (0.25 * Math.Cos(6 * longitude) * Math.Pow(Math.Cos(latitude), 2)));
            }
        }
        return new PlanetMap(values, 360, 180);
    }

    [Fact]
    public void AProjectedPointUnprojectsToItsOwnLatitudeAndLongitude()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var projection = new PlanetaryProjection(aspect, Disk);
        var checkedPoints = 0;
        for (var latitude = -60.0; latitude <= 60; latitude += 15)
        {
            for (var offset = -70.0; offset <= 70; offset += 20)
            {
                var west = aspect.CentralMeridianIII + offset;
                projection.TryProject(latitude, west, out var x, out var y).ShouldBeTrue();
                projection.TryUnproject(x, y, out var backLatitude, out var backWest).ShouldBeTrue();
                backLatitude.ShouldBe(latitude, 1e-9);
                Math.IEEERemainder(backWest - west, 360).ShouldBe(0, 1e-9);
                checkedPoints++;
            }
        }
        checkedPoints.ShouldBe(9 * 8);
        // The far side is no point of the image.
        projection.TryProject(0, aspect.CentralMeridianIII + 180, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TheDisksCentreSeesTheSubObserverPoint()
    {
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night);
        var projection = new PlanetaryProjection(aspect, Disk);
        projection.TryUnproject(Disk.CenterX, Disk.CenterY, out var latitude, out var west).ShouldBeTrue();
        latitude.ShouldBe(aspect.SubObserverLatitude, 1e-6);
        Math.IEEERemainder(west - aspect.CentralMeridianIII, 360).ShouldBe(0, 1e-9);
    }

    [Fact]
    public void AnImageDerotatedTenMinutesOnIsThePlanetThen()
    {
        // Rendered at two instants ten minutes apart (6 degrees of rotation, about 5 px at the centre of a 50 px disk), the
        // first carried to the second through the spheroid matches the second where a flat image rotation could not.
        var map = SpottedMap();
        var (from, to) = (PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night), PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night.AddMinutes(10)));
        var earlier = Image.FromChannel(ToPlane(PlanetaryRender.Render(map, from, Disk, Size, Size, 0.95, supersample: 2)));
        var later = PlanetaryRender.Render(map, to, Disk, Size, Size, 0.95, supersample: 2);

        var derotated = PlanetaryDerotation.Derotate(earlier, from, to, Disk, 0.95);
        var (none, done) = (DiskRms(earlier.GetChannelSpan(0), later), DiskRms(derotated.Image.GetChannelSpan(0), later, derotated.Covered));
        // North the wrong way round turns the planet backwards: twice the rotation, where none was better.
        var backwards = PlanetaryDerotation.Derotate(earlier, from, to, Disk with { NorthAngleDeg = Disk.NorthAngleDeg + 180 }, 0.95);
        var wrong = DiskRms(backwards.Image.GetChannelSpan(0), later, backwards.Covered);

        TestContext.Current.TestOutputHelper?.WriteLine($"RMS against the later render inside 0.9 radii: none {none:0.00000}, derotated {done:0.00000}, north flipped {wrong:0.00000}");
        done.ShouldBeLessThan(none * 0.1);
        wrong.ShouldBeGreaterThan(none);
    }

    [Fact]
    public void APixelWhoseSourceLiesNearTheLimbIsNotCovered()
    {
        // Over 31 minutes (18.8 degrees) the side the rotation turns into view reads its sources beyond 0.9 radii, where a
        // stack is its blurred edge rather than Minnaert's law: those pixels say they were not de-rotated, the rest say they were.
        var map = SpottedMap();
        var (from, to) = (PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night), PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night.AddMinutes(31)));
        var earlier = Image.FromChannel(ToPlane(PlanetaryRender.Render(map, from, Disk, Size, Size, 0.95, supersample: 2)));
        var derotated = PlanetaryDerotation.Derotate(earlier, from, to, Disk, 0.95);
        var source = new PlanetaryProjection(from, Disk);
        var target = new PlanetaryProjection(to, Disk);
        var (checkedCovered, checkedNot) = (0, 0);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (!target.TryUnproject(x, y, out var latitude, out var west) || !source.TryProject(latitude, west, out var sx, out var sy))
                {
                    derotated.Covered[(y * Size) + x].ShouldBeFalse();
                    continue;
                }
                var inside = Math.Sqrt(((sx - Disk.CenterX) * (sx - Disk.CenterX)) + ((sy - Disk.CenterY) * (sy - Disk.CenterY))) < PlanetaryDerotation.SourceRadiusLimit * Disk.EquatorialRadius;
                derotated.Covered[(y * Size) + x].ShouldBe(inside);
                (checkedCovered, checkedNot) = inside ? (checkedCovered + 1, checkedNot) : (checkedCovered, checkedNot + 1);
            }
        }
        checkedCovered.ShouldBeGreaterThan(0);
        checkedNot.ShouldBeGreaterThan(0);
        // What was relit stays within the render's own range, which a source at the limb broke.
        var max = 0f;
        foreach (var v in derotated.Image.GetChannelSpan(0))
        {
            max = Math.Max(max, v);
        }
        var peak = 0f;
        foreach (var v in earlier.GetChannelSpan(0))
        {
            peak = Math.Max(peak, v);
        }
        max.ShouldBeLessThan(peak * 1.5f);
    }

    private static float[,] ToPlane(float[] values)
    {
        var plane = new float[Size, Size];
        Buffer.BlockCopy(values, 0, plane, 0, values.Length * sizeof(float));
        return plane;
    }

    // The RMS difference inside 0.9 equatorial radii, where the limb's lighting and the strip the rotation turns into view are
    // left out, over the pixels a de-rotation covered when it says.
    private static double DiskRms(ReadOnlySpan<float> a, float[] b, bool[]? covered = null)
    {
        double sum = 0;
        var count = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var (dx, dy) = (x - Disk.CenterX, y - Disk.CenterY);
                if ((dx * dx) + (dy * dy) < 0.81 * Disk.EquatorialRadius * Disk.EquatorialRadius && (covered is null || covered[(y * Size) + x]))
                {
                    var d = a[(y * Size) + x] - b[(y * Size) + x];
                    sum += d * d;
                    count++;
                }
            }
        }
        return Math.Sqrt(sum / count);
    }
}
