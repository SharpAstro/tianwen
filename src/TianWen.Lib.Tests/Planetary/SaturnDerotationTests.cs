using System;
using System.Globalization;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Saturn de-rotated and its colours moved by their limbs (S5 of docs/plans/planetary-restoration.md, #1234): the globe turns under rings
/// that stay where they lie, and each colour's offset is read by its own ringed limb fit.
/// </summary>
public class SaturnDerotationTests
{
    private const int Width = 200;
    private const int Height = 140;
    private static readonly DateTimeOffset Night = new DateTimeOffset(2022, 10, 9, 11, 25, 0, TimeSpan.Zero);
    private static readonly DiskPlacement Disk = new DiskPlacement(99.6, 70.3, 28, NorthAngleDeg: 271.4);
    private static readonly SaturnRings Rings = SaturnRings.Structured(0.17, 0.70, 0.1, 0.50);

    [Fact]
    public void ASaturnDerotatedTwentyMinutesOnIsThePlanetThenItsRingsWhereTheyLay()
    {
        // Twenty minutes is 11.4 degrees of Saturn's turn, 5.6 px at the middle of a 28 px globe. Carried to the later instant through
        // the spheroid, the globe matches the later render; a ring, the same all the way round, is where it was, never turned with it.
        var map = PlanetaryDerotationTests.SpottedMap();
        var (from, to) = (PhysicalEphemeris.Compute(CatalogIndex.Saturn, Night), PhysicalEphemeris.Compute(CatalogIndex.Saturn, Night.AddMinutes(20)));
        var earlier = Image.FromChannel(ToPlane(PlanetaryRender.Render(map, from, Disk, Width, Height, 0.85, supersample: 2, rings: Rings)));
        var later = PlanetaryRender.Render(map, to, Disk, Width, Height, 0.85, supersample: 2, rings: Rings);

        var derotated = PlanetaryDerotation.Derotate(earlier, from, to, Disk, 0.85);

        var options = PlanetaryLimbFit.OptionsFor(to);
        var rings = new MetricDisk(Disk.CenterX, Disk.CenterY, Disk.EquatorialRadius, options.AxisRatio, Disk.NorthAngleDeg)
        {
            Rings = DiskRings.Of(Disk.NorthAngleDeg, Disk.NorthAngleDeg, options, options.Rings ?? throw new InvalidOperationException("Saturn has rings")),
        };
        var before = earlier.GetChannelSpan(0);
        var after = derotated.Image.GetChannelSpan(0);
        double none = 0, done = 0;
        var (covered, ringPixels) = (0, 0);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width) + x;
                if (rings.RingTouched(x, y))
                {
                    derotated.Covered[i].ShouldBeFalse();
                    after[i].ShouldBe(before[i], 1e-5f);
                    ringPixels++;
                }
                else if (derotated.Covered[i])
                {
                    none += (before[i] - later[i]) * (before[i] - later[i]);
                    done += (after[i] - later[i]) * (after[i] - later[i]);
                    covered++;
                }
            }
        }
        var (rmsNone, rmsDone) = (Math.Sqrt(none / covered), Math.Sqrt(done / covered));
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"the globe against the later render, {covered} px: none {rmsNone:0.00000}, de-rotated {rmsDone:0.00000}; {ringPixels} ring pixels left as they lay"));
        ringPixels.ShouldBeGreaterThan(1000);
        covered.ShouldBeGreaterThan(1000);
        rmsDone.ShouldBeLessThan(rmsNone * 0.2);
    }

    [Fact]
    public void SaturnsColoursAreReadByTheirRingedLimbs()
    {
        // A split master of a ringed Saturn, red and blue moved by what dispersion does: each colour is read by its own limb fit with
        // the rings in its model (the stack used to read Saturn's by correlation, its limb fit unable to stand for the rings).
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, Night);
        var map = PlanetaryDerotationTests.SpottedMap();
        float[] Colour(double dx, double dy, double gain)
        {
            var plane = PlanetaryRender.Render(map, aspect, Disk with { CenterX = Disk.CenterX + dx, CenterY = Disk.CenterY + dy }, Width, Height, 0.85,
                supersample: 2, rings: Rings);
            var blurred = PlanetaryInverse.Apply(plane, Width, Height, f => Math.Exp(-2 * Math.PI * Math.PI * 1.2 * 1.2 * f * f));
            for (var i = 0; i < blurred.Length; i++)
            {
                blurred[i] = (float)((gain * blurred[i]) + 0.01);
            }
            return blurred;
        }
        var (red, green, blue) = (Colour(-1.4, -1.1, 0.9), Colour(0, 0, 1), Colour(1.9, 1.6, 0.7));
        var mosaic = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width) + x;
                mosaic[y, x] = (((y & 1) * 2) + (x & 1)) switch { 0 => red[i], 3 => blue[i], _ => green[i] };
            }
        }
        var split = new Image([mosaic], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.RGGB }).SplitBayerChannels();

        var (_, result) = PlanetaryChannelAlignment.Align(split, PlanetaryFrameLayout.SplitCfa, PlanetaryChannelAlignment.LimbOptionsFor(CatalogIndex.Saturn, Night));

        var read = result.ShouldNotBeNull();
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"read by {read.Reading}: red {read.Red}, blue {read.Blue}, the greens {read.GreenCheck}"));
        read.Reading.ShouldBe(PlanetaryChannelReading.Limb);
        read.Applied.ShouldBeTrue();
        read.Red.Dx.ShouldBe(-1.4, 0.2);
        read.Red.Dy.ShouldBe(-1.1, 0.2);
        read.Blue.Dx.ShouldBe(1.9, 0.2);
        read.Blue.Dy.ShouldBe(1.6, 0.2);
    }

    private static float[,] ToPlane(float[] values)
    {
        var plane = new float[Height, Width];
        Buffer.BlockCopy(values, 0, plane, 0, values.Length * sizeof(float));
        return plane;
    }
}
