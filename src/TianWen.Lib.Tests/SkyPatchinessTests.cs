using System;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="Image.SkyPatchiness"/> (#1951): a smooth gradient, which twilight and light pollution draw, must not read as
/// patchy, while uneven cloud must, by a wide margin; and a Bayer mosaic is read at one colour, so its photosite levels are
/// not patchiness. <see cref="SolarAltitude"/>: the Sun is up at noon, astronomically dark at midnight, and twilight says
/// which way it is going.
/// </summary>
public class SkyPatchinessTests
{
    private const int Size = 640;

    [Fact]
    public void ASmoothGradientIsNotPatchy()
    {
        var flat = Patchiness(Sky(gradient: 0f, cloud: 0f));
        var graded = Patchiness(Sky(gradient: 400f, cloud: 0f));

        // A gradient forty times the noise across the frame: a plain MAD would rise many-fold, the fitted surface takes it out.
        graded.ShouldBeLessThan(flat * 1.5f);
    }

    [Fact]
    public void UnevenCloudIsPatchyByAWideMargin()
    {
        var clear = Patchiness(Sky(gradient: 200f, cloud: 0f));
        var cloudy = Patchiness(Sky(gradient: 200f, cloud: 60f));

        cloudy.ShouldBeGreaterThan(clear * 4f);
    }

    [Fact]
    public void AMosaicsPhotositeLevelsAreNotPatchiness()
    {
        // Red and blue sit 30 % above green, as an OSC sky does: read at one green phase, the frame is as smooth as a mono one.
        var mono = Patchiness(Sky(gradient: 0f, cloud: 0f));
        var mosaic = Patchiness(Sky(gradient: 0f, cloud: 0f, mosaic: true));

        mosaic.ShouldBeLessThan(mono * 1.5f);
    }

    [Fact]
    public void AFrameTooSmallToFitHasNoPatchiness()
        => new Image([new float[100, 100]], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()).SkyPatchiness(0).ShouldBe(float.NaN);

    [Fact]
    public void TheSunIsUpAtNoonAndDarkAtMidnight()
    {
        // Melbourne, the 2026-05-14 night of #1951: local noon (UTC+10) and midnight.
        SolarAltitude.Degrees(new DateTimeOffset(2026, 5, 14, 2, 0, 0, TimeSpan.Zero), -37.876, 145.178).ShouldBeGreaterThan(20);
        SolarAltitude.Degrees(new DateTimeOffset(2026, 5, 14, 14, 0, 0, TimeSpan.Zero), -37.876, 145.178).ShouldBeLessThan(-40);
        SolarAltitude.Twilight(new DateTimeOffset(2026, 5, 14, 14, 0, 0, TimeSpan.Zero), -37.876, 145.178).ShouldBeNull();
    }

    [Fact]
    public void TwilightSaysWhichWayTheSunIsGoing()
    {
        // 06:30 and 18:00 local: past astronomical dark, before sunrise and after sunset.
        SolarAltitude.Twilight(new DateTimeOffset(2026, 5, 14, 6, 30, 0, TimeSpan.FromHours(10)), -37.876, 145.178).ShouldBe(TwilightPeriod.Dawn);
        SolarAltitude.Twilight(new DateTimeOffset(2026, 5, 14, 18, 0, 0, TimeSpan.FromHours(10)), -37.876, 145.178).ShouldBe(TwilightPeriod.Dusk);
    }

    [Fact]
    public void AnUnknownSiteHasNoSunAltitude()
    {
        SolarAltitude.Degrees(DateTimeOffset.UtcNow, double.NaN, double.NaN).ShouldBe(double.NaN);
        SolarAltitude.Twilight(DateTimeOffset.UtcNow, double.NaN, double.NaN).ShouldBeNull();
    }

    private static float Patchiness(Image image) => image.SkyPatchiness(0);

    /// <summary>
    /// A 640 px sky at 1000 with noise 10, a left-to-right gradient of <paramref name="gradient"/> across the frame, and
    /// <paramref name="cloud"/> worth of uneven blobs some 150 px across, as cloud lit from below makes.
    /// </summary>
    private static Image Sky(float gradient, float cloud, bool mosaic = false)
    {
        var rng = new Random(1951);
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var noise = (float)(Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble())) * 10f;
                var blobs = MathF.Sin(x / 47f) * MathF.Sin(y / 53f + 1.3f) * MathF.Cos((x + y) / 71f);
                var level = 1000f + gradient * x / Size + cloud * blobs + noise;
                // RGGB from (0, 0): red at even row and column, blue at odd row and column, green between.
                var isGreen = ((x + y) & 1) == 1;
                plane[y, x] = mosaic && !isGreen ? level * 1.3f : level;
            }
        }
        var meta = mosaic ? new ImageMeta { SensorType = SensorType.RGGB } : new ImageMeta();
        return new Image([plane], BitDepth.Float32, 4000f, 0f, 0f, meta);
    }
}
