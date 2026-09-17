using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="Normalizer.ComputeCfaStats"/>, <see cref="Normalizer.ApplyCfa"/> and
/// <see cref="Normalizer.ApplyCfaInPlace"/>: the per-CFA-colour normalisation both drizzle strategies
/// run on every raw frame before deposit.
/// </summary>
/// <remarks>
/// <para>The reference for a colour's statistic is <see cref="Normalizer.ComputeStats(Image)"/> taken
/// on that colour's own plane out of <see cref="Image.SplitBayerChannels"/>, never the traversal under
/// test, so the two stats paths are held to one definition of which samples count: every sample but
/// NaN, infinities included. A review once read <c>StatisticsHelper.CompactFinite</c> by its name,
/// claimed the per-channel path dropped infinities and the CFA path did not, and changed the CFA path
/// to match the name; this test failed on that change, which is how the claim was found to be wrong.</para>
/// <para>The in-place form exists for the tile drizzle strategy, which owns its calibrated frame and
/// caches it for the whole run: the copy was a whole plane per frame, about 36 MB at 3008 squared, all
/// of it garbage the moment the normalised frame replaced the calibrated one.</para>
/// </remarks>
public class NormalizerCfaTests
{
    private const int Height = 10;
    private const int Width = 12;
    private const float Target = 0.5f;

    /// <summary>
    /// A mosaic whose value at every photosite is <paramref name="value"/>(y, x), in the pattern the
    /// offsets name.
    /// </summary>
    private static Image Mosaic(int bayerOffsetX, int bayerOffsetY, float pedestal, Func<int, int, float> value)
    {
        var plane = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                plane[y, x] = value(y, x);
            }
        }

        var meta = new ImageMeta { Instrument = "synth-cfa", SensorType = SensorType.RGGB, BayerOffsetX = bayerOffsetX, BayerOffsetY = bayerOffsetY };
        return new Image([plane], BitDepth.Float32, maxValue: 4096f, minValue: 0f, pedestal: pedestal, imageMeta: meta);
    }

    /// <summary>
    /// Distinct finite values everywhere, with a large share of every colour non-finite: +inf on
    /// two of every five red and blue photosites, -inf and NaN scattered through green. At forty
    /// percent, whether a median ranks the infinities moves it (red read 92.5 against 147.5 at offset (0, 1)), so the two
    /// paths cannot agree by accident.
    /// </summary>
    private static Image MosaicWithNonFiniteSamples(int bayerOffsetX, int bayerOffsetY)
    {
        var counter = 0;
        return Mosaic(bayerOffsetX, bayerOffsetY, pedestal: 3f, (y, x) =>
        {
            var k = counter++;
            var isRedOrBlue = ((y + bayerOffsetY) & 1) == ((x + bayerOffsetX) & 1);
            if (isRedOrBlue && k % 5 < 2)
            {
                return float.PositiveInfinity;
            }
            if (!isRedOrBlue && k % 7 == 0)
            {
                return float.NegativeInfinity;
            }
            if (!isRedOrBlue && k % 11 == 0)
            {
                return float.NaN;
            }
            return 10f + k * 1.25f;
        });
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    public void EachColoursMedianIsItsSplitPlanesMedianOverTheSameSamples(int bayerOffsetX, int bayerOffsetY)
    {
        var mosaic = MosaicWithNonFiniteSamples(bayerOffsetX, bayerOffsetY);

        var split = mosaic.SplitBayerChannels(); // [R, G1, G2, B]
        var perChannel = Normalizer.ComputeStats(split);

        // Green is ONE statistic over both green phases, so its reference is both split planes pooled.
        var g1 = split.GetChannelArray(1);
        var g2 = split.GetChannelArray(2);
        var pooledGreen = new float[1, g1.Length + g2.Length];
        var i = 0;
        foreach (var v in g1)
        {
            pooledGreen[0, i++] = v;
        }
        foreach (var v in g2)
        {
            pooledGreen[0, i++] = v;
        }
        var greenReference = Normalizer.ComputeStats(Image.FromChannel(pooledGreen, maxValue: 4096f, minValue: 0f));

        var cfa = Normalizer.ComputeCfaStats(mosaic);

        cfa.Red.PerChannelMedian[0].ShouldBe(perChannel.PerChannelMedian[0], "red, against the split R plane");
        cfa.Green.PerChannelMedian[0].ShouldBe(greenReference.PerChannelMedian[0], "green, against G1 and G2 pooled");
        cfa.Blue.PerChannelMedian[0].ShouldBe(perChannel.PerChannelMedian[3], "blue, against the split B plane");
        cfa.Red.PerChannelFloor[0].ShouldBe(3f, "the floor is the pedestal, never a pixel");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void InPlaceWritesExactlyWhatTheCopyWritesIntoTheFramesOwnArrays(int bayerOffsetX, int bayerOffsetY)
    {
        static float Value(int y, int x) => y == 3 && x == 4 ? float.NaN : 100f + (y * 31 + x * 17) % 23 * 2.5f;

        var reference = Mosaic(bayerOffsetX, bayerOffsetY, pedestal: 20f, Value);
        var owned = Mosaic(bayerOffsetX, bayerOffsetY, pedestal: 20f, Value);
        var stats = Normalizer.ComputeCfaStats(reference);

        var copied = Normalizer.ApplyCfa(reference, stats, Target);
        var ownedPlane = owned.GetChannelArray(0);
        var inPlace = Normalizer.ApplyCfaInPlace(owned, stats, Target);

        ReferenceEquals(inPlace.GetChannelArray(0), ownedPlane).ShouldBeTrue("the result is the consumed frame's own plane");
        inPlace.Pedestal.ShouldBe(0f, "the pedestal is mapped to zero, same as the copy");
        var expected = copied.GetChannelArray(0);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                BitConverter.SingleToInt32Bits(ownedPlane[y, x]).ShouldBe(BitConverter.SingleToInt32Bits(expected[y, x]),
                    $"photosite ({y}, {x}) differs between the in-place and copying normalise");
            }
        }
    }

    [Fact]
    public void TheInPlaceNormaliseAllocatesNoPlane()
    {
        const int size = 512;
        static Image Frame(int size)
        {
            var plane = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    plane[y, x] = 500f + (x ^ y) % 97;
                }
            }
            var meta = new ImageMeta { Instrument = "synth-cfa", SensorType = SensorType.RGGB };
            return new Image([plane], BitDepth.Float32, maxValue: 4096f, minValue: 0f, pedestal: 0f, imageMeta: meta);
        }

        // Warm the path so JIT work is not counted against the call being measured.
        var warm = Frame(4);
        Normalizer.ApplyCfaInPlace(warm, Normalizer.ComputeCfaStats(warm), Target);

        var frame = Frame(size);
        var stats = Normalizer.ComputeCfaStats(frame);
        var planeBytes = (long)size * size * sizeof(float);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Normalizer.ApplyCfaInPlace(frame, stats, Target);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBeLessThan(planeBytes / 16,
            $"allocated {allocated} bytes normalising a {planeBytes}-byte plane: a plane copy is back");
    }
}
