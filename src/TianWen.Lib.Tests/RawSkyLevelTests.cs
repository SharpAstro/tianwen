using System;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Grading;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The stacker's grade reads its sky LEVEL off the raw light (#2003), in the frame's own counts and on a mosaic at its green
/// photosites, so it is on the scale of the viewer's references and of <see cref="LightGrading.NightSkyCloudFrom"/>. A field's stars
/// barely move the median.
/// </summary>
public class RawSkyLevelTests
{
    [Fact]
    public void AMosaicsSkyLevelIsItsGreenMedianInCounts()
    {
        const int size = 64;
        var plane = new float[size, size];
        var noise = new Random(7);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // RGGB: red at (even, even), blue at (odd, odd), green on the other two.
                var level = (y % 2, x % 2) switch { (0, 0) => 1500f, (1, 1) => 1200f, _ => 3300f };
                plane[y, x] = level + (float)(noise.NextDouble() * 20 - 10);
            }
        }
        for (var s = 0; s < 40; s++)
        {
            plane[noise.Next(size), noise.Next(size)] = 60000f; // stars: under 1 % of the photosites
        }
        var meta = new ImageMeta("Synth", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), FrameType.Light, "", 3.76f, 3.76f, 530,
            FocuserPositions.None, Filter.None, 1, 1, -5f, SensorType.RGGB, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        var raw = new Image([plane], BitDepth.Int16, 60000f, 0f, 0f, meta);

        LightGrading.RawSkyLevel(raw).ShouldBe(3300f, 15f);
    }
}
