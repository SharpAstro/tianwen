using Shouldly;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for <see cref="TensorImageConverter.ToNhwcTensor"/> /
/// <see cref="TensorImageConverter.FromNhwcTensor"/> -- the NHWC layout
/// adapter needed by TensorFlow-style models (GraXpert BGE etc.). Verifies
/// the channels-innermost stripe pattern is correct and round-trips
/// preserve every pixel byte-for-byte.
/// </summary>
[Collection("Imaging")]
public class TensorImageConverterNhwcTests
{
    private static Image MakeRgb(float[,] r, float[,] g, float[,] b)
    {
        var h = r.GetLength(0);
        var w = r.GetLength(1);
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                foreach (var v in new[] { r[y, x], g[y, x], b[y, x] })
                {
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
        return new Image([r, g, b], BitDepth.Float32, maxValue: max, minValue: min, pedestal: 0f, default);
    }

    [Fact]
    public void ToNhwcTensor_HasChannelsInnermost()
    {
        // Build an Image where each channel is a known constant so the
        // tensor stripe is unambiguous. The NHWC layout means the buffer
        // should be [R, G, B, R, G, B, ...] -- one channel triple per pixel.
        var r = new float[2, 2]; for (var y = 0; y < 2; y++) for (var x = 0; x < 2; x++) r[y, x] = 0.1f;
        var g = new float[2, 2]; for (var y = 0; y < 2; y++) for (var x = 0; x < 2; x++) g[y, x] = 0.5f;
        var b = new float[2, 2]; for (var y = 0; y < 2; y++) for (var x = 0; x < 2; x++) b[y, x] = 0.9f;
        var src = MakeRgb(r, g, b);

        var tensor = TensorImageConverter.ToNhwcTensor(src);
        tensor.Dimensions.ToArray().ShouldBe(new[] { 1, 2, 2, 3 });
        var buf = tensor.Buffer.Span.ToArray();
        // 4 pixels × 3 channels = 12 floats. Pattern: 0.1, 0.5, 0.9, repeated.
        buf.Length.ShouldBe(12);
        for (var i = 0; i < 4; i++)
        {
            buf[i * 3 + 0].ShouldBe(0.1f);
            buf[i * 3 + 1].ShouldBe(0.5f);
            buf[i * 3 + 2].ShouldBe(0.9f);
        }
    }

    [Fact]
    public void NhwcRoundTrip_PreservesPixelsExactly()
    {
        // Per-pixel distinct values so any layout error would show up.
        var r = new float[2, 3];
        var g = new float[2, 3];
        var b = new float[2, 3];
        var k = 0;
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                r[y, x] = k * 0.01f;
                g[y, x] = k * 0.01f + 100f;
                b[y, x] = k * 0.01f + 200f;
                k++;
            }
        }
        var src = MakeRgb(r, g, b);

        var tensor = TensorImageConverter.ToNhwcTensor(src);
        var roundTripped = TensorImageConverter.FromNhwcTensor(tensor, src);

        roundTripped.Width.ShouldBe(src.Width);
        roundTripped.Height.ShouldBe(src.Height);
        roundTripped.ChannelCount.ShouldBe(src.ChannelCount);
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < 2; y++)
            {
                for (var x = 0; x < 3; x++)
                {
                    roundTripped[c, y, x].ShouldBe(src[c, y, x]);
                }
            }
        }
    }

    [Fact]
    public void NhwcRoundTrip_AgreesWithNchwRoundTrip()
    {
        // Both layouts must produce the same Image when round-tripped through
        // their respective tensor types -- a sanity check that the layout
        // adapters are consistent.
        var r = new float[3, 3]; for (var y = 0; y < 3; y++) for (var x = 0; x < 3; x++) r[y, x] = 0.1f + 0.01f * (y + x);
        var g = new float[3, 3]; for (var y = 0; y < 3; y++) for (var x = 0; x < 3; x++) g[y, x] = 0.4f + 0.02f * (y - x);
        var b = new float[3, 3]; for (var y = 0; y < 3; y++) for (var x = 0; x < 3; x++) b[y, x] = 0.7f + 0.03f * y;
        var src = MakeRgb(r, g, b);

        var nchwTensor = TensorImageConverter.ToNchwTensor(src);
        var nhwcTensor = TensorImageConverter.ToNhwcTensor(src);
        var fromNchw = TensorImageConverter.FromNchwTensor(nchwTensor, src);
        var fromNhwc = TensorImageConverter.FromNhwcTensor(nhwcTensor, src);

        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < 3; y++)
            {
                for (var x = 0; x < 3; x++)
                {
                    fromNchw[c, y, x].ShouldBe(fromNhwc[c, y, x]);
                }
            }
        }
    }
}
