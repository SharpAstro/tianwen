using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A channel with no finite pixel anywhere is not a dim channel, it is an ABSENT one, and the
/// integration that produced it failed. Every strategy's master goes through
/// <see cref="IntegratedMaster.Labelled"/>, so that is where it is refused, for all of them at once.
/// </summary>
/// <remarks>
/// The failure it exists for: a hot-pixel mask built from one Bayer colour's noise scale flagged
/// 100.000% of another colour's photosites, drizzle deposited nothing into that plane, and the eta
/// Carinae ASI294MC master was written with an all-NaN blue channel. Nothing downstream objected --
/// the session reported success, the enhancer turned the NaN plane into a pastel blur, and a gallery
/// card was the first thing that showed it. The cause is fixed in <c>BadPixelDetection</c>; this is
/// the backstop for the next route to the same outcome.
/// </remarks>
public class IntegratedMasterEmptyChannelTests
{
    private const int Size = 8;

    private static float[,] Plane(Func<int, int, float> value)
    {
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[y, x] = value(y, x);
            }
        }
        return plane;
    }

    private static Image Master(params float[][,] planes)
    {
        var meta = new ImageMeta("synthetic", DateTime.UnixEpoch, TimeSpan.FromSeconds(10),
            FrameType.Light, "", 4.63f, 4.63f, 121, 8, Filter.Unknown, 1, 1,
            21f, SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image(planes, BitDepth.Float32, 1f, 0f, 0f, meta);
    }

    [Fact]
    public void AMasterWhoseChannelHasNoFinitePixelIsRefused()
    {
        var master = Master(
            Plane((_, _) => 0.5f),
            Plane((_, _) => 0.5f),
            Plane((_, _) => float.NaN));

        var ex = Should.Throw<InvalidOperationException>(() => IntegratedMaster.Labelled(master, normalised: true));
        ex.Message.ShouldContain("channel 2 of 3");
    }

    /// <summary>
    /// The test is ZERO finite pixels and nothing looser, because a real master is full of legitimate
    /// holes: a drizzle canvas is NaN wherever no frame reached, and 53 of 79 masters in one bake
    /// carry interior holes as well as the ring. A single surviving sample is enough to say the plane
    /// was integrated.
    /// </summary>
    [Fact]
    public void AChannelThatIsMostlyCanvasRingIsStillAMaster()
    {
        var master = Master(
            Plane((_, _) => 0.5f),
            Plane((_, _) => 0.5f),
            Plane((y, x) => y == Size / 2 && x == Size / 2 ? 0.5f : float.NaN));

        Should.NotThrow(() => IntegratedMaster.Labelled(master, normalised: true));
    }
}
