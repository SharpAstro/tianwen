using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A colour capture's planes read one at a time, and a colour synthetic capture made on a Bayer sensor
/// (docs/plans/planetary-restoration.md, R5a): three colours of one atmosphere, each photosite from its own colour.
/// </summary>
public class PlanetaryBayerTwinTests
{
    private const int Size = 96;
    private const double Radius = 20;
    private static readonly DateTimeOffset Night = new DateTimeOffset(2024, 12, 15, 12, 40, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    public void EachChannelsPhaseIsWhereTheSplitTakesItsPhotositesFrom(int ox, int oy)
    {
        // Every photosite a tracer of where it is: the split's plane pixel (i, j) must be the sensor's (2i + px, 2j + py).
        var mosaic = new float[6, 8];
        for (var y = 0; y < 6; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                mosaic[y, x] = (y * 10) + x + 1;
            }
        }
        var split = new Image([mosaic], BitDepth.Float32, 100f, 0f, 0f, new ImageMeta { SensorType = SensorType.RGGB, BayerOffsetX = ox, BayerOffsetY = oy })
            .SplitBayerChannels();
        for (var c = 0; c < 4; c++)
        {
            var (px, py) = CfaPlaneStream.PhaseOf(c, ox, oy);
            var plane = split.GetChannelSpan(c);
            for (var j = 0; j < 3; j++)
            {
                for (var i = 0; i < 4; i++)
                {
                    plane[(j * 4) + i].ShouldBe(mosaic[(2 * j) + py, (2 * i) + px], $"channel {c}, plane pixel ({i}, {j})");
                }
            }
        }
    }

    [Fact]
    public async Task AColourPlaneOfABayerSerIsAMonoStreamOfItsPhotosites()
    {
        var path = PlanetarySerFixtures.NewTempPath();
        try
        {
            var frames = new ushort[2][];
            for (var f = 0; f < 2; f++)
            {
                frames[f] = new ushort[8 * 6];
                for (var i = 0; i < frames[f].Length; i++)
                {
                    frames[f][i] = (ushort)((1000 * (f + 1)) + i);
                }
            }
            DateTimeOffset[] times = [Night, Night.AddMilliseconds(4)];
            PlanetarySerFixtures.WriteSer(path, 8, 6, SerColorId.BayerRGGB, frames, times);

            using var source = SerFrameStream.Open(path);
            using var blue = new CfaPlaneStream(source, CfaPlaneStream.Blue);
            (blue.Width, blue.Height, blue.Layout, blue.FrameCount).ShouldBe((4, 3, PlanetaryFrameLayout.Mono, 2));
            blue.TimestampOf(1).ShouldBe(times[1]);

            var frame = await blue.LoadAsync(1, TestContext.Current.CancellationToken);
            try
            {
                frame.ChannelCount.ShouldBe(1);
                frame.ImageMeta.SensorType.ShouldBe(SensorType.Monochrome);
                var (px, py) = CfaPlaneStream.PhaseOf(CfaPlaneStream.Blue, 0, 0);
                var plane = frame.GetChannelSpan(0);
                // The stream decodes to [0, 1] of the 16-bit full scale; the ordering of the photosites is what is checked.
                var scale = plane[0] / frames[1][(py * 8) + px];
                for (var j = 0; j < 3; j++)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        plane[(j * 4) + i].ShouldBe(frames[1][(((2 * j) + py) * 8) + (2 * i) + px] * scale, 1e-6f);
                    }
                }
            }
            finally
            {
                frame.Release();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task EachPhotositeTakesItsOwnColourOfOneAtmosphere()
    {
        // Red, green and blue at their own levels and wavelengths, blue's disk 2 px below red's (the atmosphere's dispersion), one
        // seed: each colour's photosites hold its own level and its own disk, and the three colours move together frame by frame.
        ImmutableArray<double> none = [0, 0, 0, 0];
        var times = ImmutableArray.Create(Night, Night.AddMilliseconds(4), Night.AddMilliseconds(8), Night.AddMilliseconds(12));
        var map = PlanetaryDegradeTests.BandedMap();
        var centre = new DiskPlacement((Size / 2) - 0.3, (Size / 2) + 0.2, Radius, NorthAngleDeg: -80);
        var levels = (Red: 12000.0, Green: 20000.0, Blue: 7000.0);
        var red = new BayerColour(map, centre with { CenterY = centre.CenterY - 1 }, Options(610e-9, levels.Red));
        var green = new BayerColour(map, centre, Options(535e-9, levels.Green));
        var blue = new BayerColour(map, centre with { CenterY = centre.CenterY + 1 }, Options(460e-9, levels.Blue));
        var frames = new ushort[times.Length][];

        var (r, g, b) = await PlanetaryDegrade.MakeBayerAsync(CatalogIndex.Jupiter, times, red, green, blue, 0.49, none, none, [], Size, Size, 0, 0,
            (index, samples) => frames[index] = [.. samples], cancellationToken: TestContext.Current.CancellationToken);

        for (var t = 0; t < times.Length; t++)
        {
            // The seeing's tilt is the air's, the same path difference at every wavelength: the colours move as one.
            Math.Abs(r[t].ShiftX - g[t].ShiftX).ShouldBeLessThan(0.1, $"frame {t}: red and green moved apart in x");
            Math.Abs(b[t].ShiftY - g[t].ShiftY).ShouldBeLessThan(0.1, $"frame {t}: blue and green moved apart in y");
        }
        var (rx, ry, rl) = CentroidAndLevel(frames[0], CfaPlaneStream.Red);
        var (gx, gy, gl) = CentroidAndLevel(frames[0], CfaPlaneStream.Green1);
        var (bx, by, bl) = CentroidAndLevel(frames[0], CfaPlaneStream.Blue);
        TestContext.Current.TestOutputHelper?.WriteLine($"levels {rl:0} {gl:0} {bl:0}; centres red {rx:0.00}, {ry:0.00}, green {gx:0.00}, {gy:0.00}, blue {bx:0.00}, {by:0.00}");
        rl.ShouldBe(levels.Red, levels.Red * 0.04);
        gl.ShouldBe(levels.Green, levels.Green * 0.04);
        bl.ShouldBe(levels.Blue, levels.Blue * 0.04);
        // Blue sits 2 px below red, each colour where its own placement put it, whichever photosites it was read through.
        (by - ry).ShouldBe(2, 0.25);
        (bx - rx).ShouldBe(0, 0.25);
    }

    [Fact]
    public async Task ColoursOfTwoAtmospheresAreRefused()
    {
        var map = PlanetaryDegradeTests.BandedMap();
        var placement = new DiskPlacement(Size / 2, Size / 2, Radius, 0);
        var green = new BayerColour(map, placement, Options(535e-9, 20000));
        var red = green with { Options = green.Options with { Seed = 2 } };
        await Should.ThrowAsync<ArgumentException>(() => PlanetaryDegrade.MakeBayerAsync(CatalogIndex.Jupiter, [Night], red, green, green, 0.49, [0], [0], [], Size, Size, 0, 0,
            (_, _) => { }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static DegradeOptions Options(double wavelengthM, double diskLevel) => new DegradeOptions(new Pupil(0.254, ObstructionRatio: 0.23, Vanes: 4, VaneWidthM: 0.001), wavelengthM)
    {
        R0M = 0.1,
        FullScaleAdu = 65535,
        OffsetAdu = 100,
        ReadNoiseAdu = 0,
        ElectronsPerAdu = 1000,
        DiskLevelAdu = diskLevel,
        ScreenSamples = 128,
        KeepScreenTilt = true,
    };

    // A colour's photosites alone: their disk's centroid in the sensor's pixels, over the offset, and its mean inside 0.8 radii.
    private static (double X, double Y, double Level) CentroidAndLevel(ushort[] frame, int channel)
    {
        var (px, py) = CfaPlaneStream.PhaseOf(channel, 0, 0);
        double sum = 0, sx = 0, sy = 0;
        for (var y = py; y < Size; y += 2)
        {
            for (var x = px; x < Size; x += 2)
            {
                var v = frame[(y * Size) + x] - 100.0;
                sum += v;
                sx += v * x;
                sy += v * y;
            }
        }
        var (cx, cy) = (sx / sum, sy / sum);
        double inside = 0;
        var count = 0;
        for (var y = py; y < Size; y += 2)
        {
            for (var x = px; x < Size; x += 2)
            {
                if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) < 0.64 * Radius * Radius)
                {
                    inside += frame[(y * Size) + x] - 100.0;
                    count++;
                }
            }
        }
        return (cx, cy, inside / count);
    }
}
