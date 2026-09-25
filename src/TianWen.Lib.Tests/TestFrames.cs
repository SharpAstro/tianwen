using TianWen.Lib.Imaging;

namespace TianWen.Lib.Tests;

/// <summary>
/// Frames shaped like a camera's, over a recycled buffer, so a test can watch who holds the pixels
/// (<see cref="ChannelBuffer.RefCount"/>, <see cref="ChannelBuffer.IsReleased"/>) rather than infer it.
/// </summary>
internal static class TestFrames
{
    /// <summary>
    /// A mono frame over a recycled camera buffer: mostly background with one star, which is also what keeps
    /// the stretch's median/MAD scan out of its degenerate case. <paramref name="star"/> is the star's peak,
    /// so two frames can be told apart by <see cref="StarPeak"/>.
    /// </summary>
    /// <param name="clobberOnRecycle">
    /// Recycling is not a bookkeeping event: the camera writes the NEXT frame into this very array. Modelling
    /// that as a clobber (every pixel set to 0.5) is what gives an across-a-release test teeth, since the raw
    /// <c>float[,]</c> stays readable after a release and a stale read would otherwise look like a pass.
    /// </param>
    public static Image BufferedMono(out ChannelBuffer buffer, int width = 48, int height = 32,
        bool clobberOnRecycle = false, float star = 0.9f)
    {
        var data = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = 0.02f + (x * 7 + y * 13) % 17 * 0.0005f;
            }
        }

        data[height / 2, width / 2] = star;
        data[height / 2, width / 2 + 1] = star * 0.6f;
        data[height / 2 + 1, width / 2] = star * 0.6f;

        var owned = new ChannelBuffer(
            data,
            onRelease: recycled =>
            {
                if (clobberOnRecycle)
                {
                    for (var y = 0; y < recycled.GetLength(0); y++)
                    {
                        for (var x = 0; x < recycled.GetLength(1); x++)
                        {
                            recycled[y, x] = 0.5f;
                        }
                    }
                }
            });
        buffer = owned;

        return new Image(
            [new Channel(data, default, 0f, star, 0) { Buffer = owned }],
            BitDepth.Float32,
            pedestal: 0f,
            new ImageMeta { SensorType = SensorType.Monochrome });
    }

    /// <summary>The peak <see cref="BufferedMono"/> put at the frame's centre.</summary>
    public static float StarPeak(Image image) => image.GetChannelSpan(0)[image.Height / 2 * image.Width + image.Width / 2];
}
