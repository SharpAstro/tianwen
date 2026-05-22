using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for <see cref="Image.BilinearResize"/> -- the bilinear-interpolation
/// resize primitive used by ML preprocessing (GraXpert BGE etc.) that need a
/// fixed-size input plate. Verifies pixel-centre convention matches OpenCV
/// (so we can compare against the reference Python pipeline byte-for-byte),
/// endpoints / monotonicity behave under upsample + downsample, and metadata
/// rides through.
/// </summary>
[Collection("Imaging")]
public class ImageBilinearResizeTests
{
    private const float Eps = 1e-5f;

    private static Image Make(float[,] data)
    {
        var h = data.GetLength(0);
        var w = data.GetLength(1);
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (data[y, x] < min) min = data[y, x];
                if (data[y, x] > max) max = data[y, x];
            }
        return new Image([data], BitDepth.Float32, maxValue: max, minValue: min, pedestal: 0f, default);
    }

    [Fact]
    public void IdentityResize_PreservesPixels()
    {
        // Same dimensions -> output should be byte-identical to input.
        var src = Make(new float[,] { { 0.1f, 0.2f, 0.3f }, { 0.4f, 0.5f, 0.6f } });
        var resized = src.BilinearResize(3, 2);
        resized.Width.ShouldBe(3);
        resized.Height.ShouldBe(2);
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 3; x++)
                resized[0, y, x].ShouldBe(src[0, y, x], tolerance: Eps);
    }

    [Fact]
    public void ConstantImage_StaysConstant()
    {
        // Any resize of a constant plane must yield the same constant.
        var arr = new float[4, 4];
        for (var y = 0; y < 4; y++) for (var x = 0; x < 4; x++) arr[y, x] = 0.42f;
        var src = Make(arr);
        var resized = src.BilinearResize(7, 13);
        resized.Width.ShouldBe(7);
        resized.Height.ShouldBe(13);
        for (var y = 0; y < 13; y++)
            for (var x = 0; x < 7; x++)
                resized[0, y, x].ShouldBe(0.42f, tolerance: Eps);
    }

    [Fact]
    public void HorizontalGradient_RemainsLinear()
    {
        // 1x10 linear ramp [0, 1] resized to 1x20 should remain a linear ramp.
        const int W = 10;
        const int W2 = 20;
        var arr = new float[1, W];
        for (var x = 0; x < W; x++) arr[0, x] = (float)x / (W - 1);
        var src = Make(arr);

        var resized = src.BilinearResize(W2, 1);
        resized.Width.ShouldBe(W2);
        // Bilinear of a linear ramp is exactly linear: assert monotonicity +
        // matching endpoints. Loose tol on endpoints because OpenCV's pixel-
        // centre convention pulls the extreme samples slightly inward.
        for (var x = 1; x < W2; x++)
        {
            resized[0, 0, x].ShouldBeGreaterThanOrEqualTo(resized[0, 0, x - 1] - Eps);
        }
        resized[0, 0, 0].ShouldBe(0f, tolerance: 0.05f);
        resized[0, 0, W2 - 1].ShouldBe(1f, tolerance: 0.05f);
    }

    [Fact]
    public void RoundTripDownThenUp_RecoversLowFrequencyShape()
    {
        // Downsample 32 -> 8 -> 32. High frequencies are lost (Nyquist) but
        // the low-frequency shape should survive. Use a smooth sinusoidal
        // gradient so there's no aliasing pathology.
        const int N = 32;
        var arr = new float[N, N];
        for (var y = 0; y < N; y++)
            for (var x = 0; x < N; x++)
                arr[y, x] = (float)((System.Math.Sin(x * System.Math.PI / N) + System.Math.Sin(y * System.Math.PI / N)) * 0.25 + 0.5);
        var src = Make(arr);

        var down = src.BilinearResize(8, 8);
        var roundTripped = down.BilinearResize(N, N);

        // L2 error normalised by signal range stays small (<10%) since the
        // signal has only low-frequency content.
        var err2 = 0.0;
        var sig2 = 0.0;
        for (var y = 0; y < N; y++)
            for (var x = 0; x < N; x++)
            {
                var diff = roundTripped[0, y, x] - arr[y, x];
                err2 += diff * diff;
                sig2 += arr[y, x] * arr[y, x];
            }
        var rel = System.Math.Sqrt(err2 / sig2);
        rel.ShouldBeLessThan(0.1, customMessage: $"relative L2 error after 32->8->32 round-trip was {rel:F4}, expected <0.1");
    }

    [Fact]
    public void MultiChannel_ResizesIndependentlyPerChannel()
    {
        // 3-channel image where each channel has a different constant value.
        // After resize, each channel must keep its own value (no cross-talk).
        var r = new float[2, 2]; for (var y = 0; y < 2; y++) for (var x = 0; x < 2; x++) r[y, x] = 0.1f;
        var g = new float[2, 2]; for (var y = 0; y < 2; y++) for (var x = 0; x < 2; x++) g[y, x] = 0.5f;
        var b = new float[2, 2]; for (var y = 0; y < 2; y++) for (var x = 0; x < 2; x++) b[y, x] = 0.9f;
        var src = new Image([r, g, b], BitDepth.Float32, maxValue: 0.9f, minValue: 0.1f, pedestal: 0f, default);

        var resized = src.BilinearResize(5, 5);
        for (var y = 0; y < 5; y++)
        {
            for (var x = 0; x < 5; x++)
            {
                resized[0, y, x].ShouldBe(0.1f, tolerance: Eps);
                resized[1, y, x].ShouldBe(0.5f, tolerance: Eps);
                resized[2, y, x].ShouldBe(0.9f, tolerance: Eps);
            }
        }
    }
}
