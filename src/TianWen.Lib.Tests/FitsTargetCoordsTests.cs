using System;
using System.IO;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Round-trips <see cref="ImageMeta.TargetRA"/>/<see cref="ImageMeta.TargetDec"/>
/// through WriteToFitsFile -> TryReadFitsFile, pinning the reader fix that now
/// populates the pointing coordinates from the RA/DEC (and OBJCTRA/OBJCTDEC)
/// header keywords. Before the fix the reader ignored them, so masters lost
/// their pointing metadata and a `--no-plate-solve` master carried no hint to
/// (re-)plate-solve from.
/// </summary>
public class FitsTargetCoordsTests
{
    [Fact]
    public void WriteThenRead_PreservesTargetCoords()
    {
        var meta = new ImageMeta(
            Instrument: "test",
            ExposureStartTime: DateTimeOffset.UnixEpoch,
            ExposureDuration: TimeSpan.FromSeconds(60),
            FrameType: FrameType.None,
            Telescope: "SH61 EDPH",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 270,
            FocusPos: 0,
            Filter: Filter.Unknown,
            BinX: 1,
            BinY: 1,
            CCDTemperature: -5f,
            SensorType: SensorType.Monochrome,
            BayerOffsetX: 0,
            BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: -37.877f,
            Longitude: 145.1775f,
            ObjectName: "Skull and Crossbones Nebula",
            TargetRA: 7.8722,
            TargetDec: -26.4306);
        var img = new Image([new float[8, 8]], BitDepth.Float32, 1f, 0f, 0f, meta);

        var path = Path.Combine(Path.GetTempPath(), $"tw-targetcoords-{Guid.NewGuid():N}.fits");
        try
        {
            img.WriteToFitsFile(path);

            Image.TryReadFitsFile(path, out var read).ShouldBeTrue();
            read.ShouldNotBeNull();
            read.ImageMeta.TargetRA.ShouldBe(7.8722, 1e-3);
            read.ImageMeta.TargetDec.ShouldBe(-26.4306, 1e-3);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
