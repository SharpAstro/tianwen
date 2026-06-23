using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class BayerSplitTests
{
    private static Image Mosaic(float[,] pixels, int offsetX, int offsetY)
        => new Image([pixels], BitDepth.Float32, 1f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.RGGB, BayerOffsetX = offsetX, BayerOffsetY = offsetY });

    [Fact]
    public void Split_rggb_offset00_deinterleaves_into_R_G1_G2_B_halfres()
    {
        // 4x4 mosaic, each pixel value = y*10 + x so every sample is a unique tracer.
        var px = new float[4, 4];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                px[y, x] = (y * 10) + x;
            }
        }

        var split = Mosaic(px, 0, 0).SplitBayerChannels();

        split.ChannelCount.ShouldBe(4);
        split.Width.ShouldBe(2);
        split.Height.ShouldBe(2);

        // R plane (channel 0): src[2*sy, 2*sx]
        split[0, 0, 0].ShouldBe(0f);
        split[0, 0, 1].ShouldBe(2f);
        split[0, 1, 0].ShouldBe(20f);
        split[0, 1, 1].ShouldBe(22f);

        // G1 plane (channel 1): green sharing the red row -> src[2*sy, 2*sx + 1]
        split[1, 0, 0].ShouldBe(1f);
        split[1, 1, 1].ShouldBe(23f);

        // G2 plane (channel 2): green sharing the blue row -> src[2*sy + 1, 2*sx]
        split[2, 0, 0].ShouldBe(10f);
        split[2, 1, 1].ShouldBe(32f);

        // B plane (channel 3): src[2*sy + 1, 2*sx + 1]
        split[3, 0, 0].ShouldBe(11f);
        split[3, 1, 1].ShouldBe(33f);
    }

    [Theory]
    [InlineData(0, 0)] // RGGB
    [InlineData(1, 0)] // GRBG
    [InlineData(0, 1)] // GBRG
    [InlineData(1, 1)] // BGGR
    public void Split_honors_offset_so_each_subplane_holds_one_cfa_colour(int offsetX, int offsetY)
    {
        // Encode each pixel's CFA colour (under this offset) as its value: R=0.1, G=0.5, B=0.9.
        // After the split, the R plane must be all-R, both G planes all-G, the B plane all-B.
        const float red = 0.1f, green = 0.5f, blue = 0.9f;
        var px = new float[6, 6];
        for (var y = 0; y < 6; y++)
        {
            var yp = ((y - offsetY) % 2 + 2) % 2;
            for (var x = 0; x < 6; x++)
            {
                var xp = ((x - offsetX) % 2 + 2) % 2;
                px[y, x] = (yp * 2 + xp) switch { 0 => red, 1 or 2 => green, _ => blue };
            }
        }

        var split = Mosaic(px, offsetX, offsetY).SplitBayerChannels();

        for (var sy = 0; sy < 3; sy++)
        {
            for (var sx = 0; sx < 3; sx++)
            {
                split[0, sy, sx].ShouldBe(red);   // R
                split[1, sy, sx].ShouldBe(green); // G1
                split[2, sy, sx].ShouldBe(green); // G2
                split[3, sy, sx].ShouldBe(blue);  // B
            }
        }
    }

    [Fact]
    public void Split_drops_odd_final_row_and_column()
    {
        var split = Mosaic(new float[5, 5], 0, 0).SplitBayerChannels();

        split.Width.ShouldBe(2);
        split.Height.ShouldBe(2);
        split.ChannelCount.ShouldBe(4);
    }

    [Fact]
    public void Split_preserves_sensor_type_and_offset_for_reassembly()
    {
        var split = Mosaic(new float[4, 4], 1, 1).SplitBayerChannels();

        split.ImageMeta.SensorType.ShouldBe(SensorType.RGGB);
        split.ImageMeta.BayerOffsetX.ShouldBe(1);
        split.ImageMeta.BayerOffsetY.ShouldBe(1);
    }

    [Fact]
    public void Split_throws_on_non_bayer_image()
    {
        var mono = new Image([new float[4, 4]], BitDepth.Float32, 1f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.Monochrome });

        Should.Throw<InvalidOperationException>(() => mono.SplitBayerChannels());
    }
}
