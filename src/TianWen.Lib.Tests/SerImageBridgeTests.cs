using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class SerImageBridgeTests
{
    [Theory]
    [InlineData(SerColorId.Mono, SensorType.Monochrome, 0, 0)]
    [InlineData(SerColorId.BayerRGGB, SensorType.RGGB, 0, 0)]
    [InlineData(SerColorId.BayerGRBG, SensorType.RGGB, 1, 0)]
    [InlineData(SerColorId.BayerGBRG, SensorType.RGGB, 0, 1)]
    [InlineData(SerColorId.BayerBGGR, SensorType.RGGB, 1, 1)]
    [InlineData(SerColorId.Rgb, SensorType.Color, 0, 0)]
    [InlineData(SerColorId.Bgr, SensorType.Color, 0, 0)]
    [InlineData(SerColorId.BayerCYYM, SensorType.Monochrome, 0, 0)] // CYGM family is unmodelled -> mono
    public void ToSensorType_maps_colorId(SerColorId colorId, SensorType expectedSensor, int expectedX, int expectedY)
    {
        var (sensor, x, y) = colorId.ToSensorType();

        sensor.ShouldBe(expectedSensor);
        x.ShouldBe(expectedX);
        y.ShouldBe(expectedY);
    }

    [Fact]
    public void FillUnitFloat_bayer_copies_normalized_mosaic()
    {
        var path = NewTempPath();
        try
        {
            // 2x2 RGGB mosaic, 16-bit: raw samples in row-major (y*w + x) order.
            ushort[] frame = [0, 65535, 21845, 43690];
            WriteSingleFrame(path, 2, 2, SerColorId.BayerRGGB, 16, frame);

            using var reader = SerReader.Open(path);
            var channels = new[] { new float[4] };
            var scratch = new ushort[reader.SamplesPerFrame];

            var count = SerImageBridge.FillUnitFloat(reader, 0, scratch, channels);

            count.ShouldBe(1);
            channels[0][0].ShouldBe(0f);
            channels[0][1].ShouldBe(1f, 1e-5);
            channels[0][2].ShouldBe(21845f / 65535f, 1e-5);
            channels[0][3].ShouldBe(43690f / 65535f, 1e-5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FillUnitFloat_bgr_deinterleaves_and_swaps_to_rgb()
    {
        var path = NewTempPath();
        try
        {
            // One pixel (1x1), BGR interleaving stores B, G, R.
            ushort[] frame = [65535, 0, 13107];
            WriteSingleFrame(path, 1, 1, SerColorId.Bgr, 16, frame);

            using var reader = SerReader.Open(path);
            var channels = new[] { new float[1], new float[1], new float[1] };
            var scratch = new ushort[reader.SamplesPerFrame];

            var count = SerImageBridge.FillUnitFloat(reader, 0, scratch, channels);

            count.ShouldBe(3);
            channels[0][0].ShouldBe(13107f / 65535f, 1e-5); // R <- stored index 2
            channels[1][0].ShouldBe(0f);                    // G <- stored index 1
            channels[2][0].ShouldBe(1f, 1e-5);              // B <- stored index 0
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ToImage_bayer_yields_mono_mosaic_with_rggb_sensor_and_offset()
    {
        var path = NewTempPath();
        try
        {
            ushort[] frame = [0, 65535, 21845, 43690];
            WriteSingleFrame(path, 2, 2, SerColorId.BayerGRBG, 16, frame);

            using var reader = SerReader.Open(path);
            var image = SerImageBridge.ToImage(reader, 0);

            image.Width.ShouldBe(2);
            image.Height.ShouldBe(2);
            image.ChannelCount.ShouldBe(1);
            image.ImageMeta.SensorType.ShouldBe(SensorType.RGGB);
            image.ImageMeta.BayerOffsetX.ShouldBe(1);
            image.ImageMeta.BayerOffsetY.ShouldBe(0);
            image[0, 0, 0].ShouldBe(0f);                       // (y=0, x=0)
            image[0, 0, 1].ShouldBe(1f, 1e-5);                 // (y=0, x=1)
            image[0, 1, 0].ShouldBe(21845f / 65535f, 1e-5);    // (y=1, x=0)
            image[0, 1, 1].ShouldBe(43690f / 65535f, 1e-5);    // (y=1, x=1)
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ToImage_rgb_yields_three_channels()
    {
        var path = NewTempPath();
        try
        {
            // One pixel (1x1), RGB interleaving stores R, G, B.
            ushort[] frame = [13107, 0, 65535];
            WriteSingleFrame(path, 1, 1, SerColorId.Rgb, 16, frame);

            using var reader = SerReader.Open(path);
            var image = SerImageBridge.ToImage(reader, 0);

            image.ChannelCount.ShouldBe(3);
            image.ImageMeta.SensorType.ShouldBe(SensorType.Color);
            image[0, 0, 0].ShouldBe(13107f / 65535f, 1e-5); // R
            image[1, 0, 0].ShouldBe(0f);                    // G
            image[2, 0, 0].ShouldBe(1f, 1e-5);              // B
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"ser-bridge-{Guid.NewGuid():N}.ser");

    private static void WriteSingleFrame(string path, int width, int height, SerColorId colorId, int depth, ushort[] frame)
    {
        using var writer = new SerWriter(path, width, height, colorId, depth);
        writer.AppendFrame(MemoryMarshal.AsBytes<ushort>(frame));
    }
}
