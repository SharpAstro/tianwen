using System;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The stretch's black point set by sky noise (#2309). A channel's minimum is one pixel: across the store's masters it
/// lay 8 to 228 sky sigma under the sky, and three pixels of a frame decided how hard a model fed that stretch saw
/// every star in it. These pin the noise the depth is counted in, the black point it gives, and the property the whole
/// change is for: one dark pixel moves the minimum's stretch and leaves this one where it was.
/// </summary>
public class SkyNoiseBlackPointTests
{
    private const int Size = 512;
    private const float Sky = 0.10f;
    private const float Sigma = 0.002f;
    private const double Depth = 40;

    /// <summary>A flat sky at <paramref name="level"/> with Gaussian noise of <paramref name="sigma"/>, the same for a seed.</summary>
    private static float[,] NoisySky(int seed, float sigma = Sigma, float level = Sky, int size = Size)
    {
        var random = new Random(seed);
        var plane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                plane[y, x] = level + sigma * Gaussian(random);
            }
        }
        return plane;
    }

    private static float Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    private static Image ToImage(params float[][,] planes) => new(planes, BitDepth.Float32, 1f, 0f, 0f, default);

    private static float MedianOf(float[,] plane)
    {
        var values = new float[plane.Length];
        Buffer.BlockCopy(plane, 0, values, 0, values.Length * sizeof(float));
        Array.Sort(values);
        return (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2f;
    }

    [Fact]
    public void TheSkyNoiseOfAPlainSkyIsItsSigma()
    {
        var image = ToImage(NoisySky(seed: 1));

        image.TryEstimateSkyNoise(0, exclude: null, out var sigma).ShouldBeTrue();

        // The quietest fifth of the blocks reads a little under the mean of them all; 5 % covers that and the draw.
        (sigma / Sigma).ShouldBeInRange(0.95f, 1.05f);
    }

    [Fact]
    public void ANoisierThreeQuartersOfTheFrameDoesNotRaiseIt()
    {
        // The target fills most of a frame: here the upper three quarters carry four times the noise and a glow, as a
        // nebula's own photon noise does, and the sky is the strip that is left.
        var plane = NoisySky(seed: 2);
        var loud = NoisySky(seed: 3, sigma: 4 * Sigma, level: 0.35f);
        for (var y = 0; y < Size * 3 / 4; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[y, x] = loud[y, x];
            }
        }

        ToImage(plane).TryEstimateSkyNoise(0, exclude: null, out var sigma).ShouldBeTrue();

        (sigma / Sigma).ShouldBeInRange(0.94f, 1.06f);
    }

    [Fact]
    public void ABlockThatTouchesTheCanvasRingIsLeftOutWhole()
    {
        // A NaN ring and a zero ring named as absent are the same frame to the estimate, and neither reads the ring.
        var nanRing = NoisySky(seed: 4);
        var zeroRing = NoisySky(seed: 4);
        const int ring = 10;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (y < ring || x < ring || y >= Size - ring || x >= Size - ring)
                {
                    nanRing[y, x] = float.NaN;
                    zeroRing[y, x] = 0f;
                }
            }
        }
        var zeroed = ToImage(zeroRing);
        var absent = zeroed.AbsentPixels().ShouldNotBeNull();

        ToImage(nanRing).TryEstimateSkyNoise(0, exclude: null, out var fromNan).ShouldBeTrue();
        zeroed.TryEstimateSkyNoise(0, absent, out var fromZero).ShouldBeTrue();

        (fromNan / Sigma).ShouldBeInRange(0.95f, 1.05f);
        fromZero.ShouldBe(fromNan);
    }

    [Fact]
    public void AFrameWithNoWholeBlockHasNoSkyNoiseToGive()
    {
        // Smaller than a block, and a frame where an absent pixel reaches every block.
        ToImage(NoisySky(seed: 5, size: Image.SkyNoiseBlock - 4)).TryEstimateSkyNoise(0, exclude: null, out var tiny).ShouldBeFalse();
        float.IsNaN(tiny).ShouldBeTrue();

        var holed = NoisySky(seed: 6, size: 256);
        for (var y = 16; y < 256; y += 32)
        {
            for (var x = 16; x < 256; x += 32)
            {
                holed[y, x] = float.NaN;
            }
        }
        ToImage(holed).TryEstimateSkyNoise(0, exclude: null, out var none).ShouldBeFalse();
        float.IsNaN(none).ShouldBeTrue();
    }

    [Fact]
    public void AMaskOfAnotherShapeIsRefused()
    {
        var image = ToImage(NoisySky(seed: 7, size: 128));

        Should.Throw<ArgumentException>(() => image.TryEstimateSkyNoise(0, new BitMatrix(64, 128), out _));
    }

    [Fact]
    public void TheBlackPointLiesTheAskedDepthUnderTheMedianAndTheMedianLandsOnTheTarget()
    {
        var planes = new[] { NoisySky(seed: 10), NoisySky(seed: 11, sigma: 1.5f * Sigma, level: 0.16f), NoisySky(seed: 12, sigma: 0.7f * Sigma, level: 0.07f) };
        var image = ToImage(planes);

        var (origMin, balances) = image.MtfStretchParametersFromSkyNoise(AiNafnetInputs.TargetMedian, Depth);
        var stretched = image.MtfStretchWith(origMin, balances);

        for (var c = 0; c < 3; c++)
        {
            image.TryEstimateSkyNoise(c, exclude: null, out var sigma).ShouldBeTrue();
            var median = MedianOf(planes[c]);
            ((double)origMin[c]).ShouldBe(median - Depth * sigma, 1e-6);
            // Every channel's sky lands on the target whatever its own level and noise.
            var (min, shifted) = stretched.MinAndShiftedMedian(c);
            ((double)(min + shifted)).ShouldBe(AiNafnetInputs.TargetMedian, 1e-3);
        }
    }

    [Fact]
    public void OneDarkPixelMovesTheMinimumsStretchAndNotThisOne()
    {
        // #2309 in one frame: the same sky twice, one of them with a single pixel far under it.
        var clean = NoisySky(seed: 20);
        var holed = NoisySky(seed: 20);
        holed[300, 211] = Sky - 0.09f;
        var a = ToImage(clean);
        var b = ToImage(holed);

        var (minA, balanceA) = a.MtfStretchParameters(AiNafnetInputs.TargetMedian);
        var (minB, balanceB) = b.MtfStretchParameters(AiNafnetInputs.TargetMedian);
        var (noiseMinA, noiseBalanceA) = a.MtfStretchParametersFromSkyNoise(AiNafnetInputs.TargetMedian, Depth);
        var (noiseMinB, noiseBalanceB) = b.MtfStretchParametersFromSkyNoise(AiNafnetInputs.TargetMedian, Depth);

        // From the minimum the one pixel is the black point, and the whole frame's stretch goes with it.
        (minA[0] - minB[0]).ShouldBeGreaterThan(0.05f);
        (balanceB[0] / balanceA[0]).ShouldBeGreaterThan(3.0);
        // From the sky noise the frame is stretched as it was.
        noiseMinB[0].ShouldBe(noiseMinA[0], 1e-6f);
        noiseBalanceB[0].ShouldBe(noiseBalanceA[0], 1e-6);
    }

    [Fact]
    public void APixelUnderTheBlackPointIsClippedToIt()
    {
        var plane = NoisySky(seed: 21);
        plane[40, 40] = 0.001f;
        var image = ToImage(plane);

        var (origMin, balances) = image.MtfStretchParametersFromSkyNoise(AiNafnetInputs.TargetMedian, Depth);
        var stretched = image.MtfStretchWith(origMin, balances);
        var back = stretched.MtfUnstretch(origMin, balances);

        origMin[0].ShouldBeGreaterThan(0.001f);
        stretched.GetChannelSpan(0)[40 * Size + 40].ShouldBe(0f);
        back.GetChannelSpan(0)[40 * Size + 40].ShouldBe(origMin[0], 1e-6f);
        // A pixel of the sky comes back as it went in.
        back.GetChannelSpan(0)[100 * Size + 100].ShouldBe(plane[100, 100], 1e-5f);
    }

    [Fact]
    public void WithNoSkyNoiseItRefusesRatherThanFallBackToTheMinimum()
    {
        var image = ToImage(NoisySky(seed: 22, size: Image.SkyNoiseBlock - 4));

        var refusal = Should.Throw<InvalidOperationException>(() => image.MtfStretchParametersFromSkyNoise(AiNafnetInputs.TargetMedian, Depth));

        refusal.Message.ShouldContain("no sky noise to set a black point by");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    public void ADepthOfZeroOrLessIsRefused(double depth)
    {
        var image = ToImage(NoisySky(seed: 23, size: 128));

        Should.Throw<ArgumentOutOfRangeException>(() => image.MtfStretchParametersFromSkyNoise(AiNafnetInputs.TargetMedian, depth));
    }

    [Fact]
    public void TheRunnersStretchIsTheMinimumsWithoutADepthAndTheSkyNoisesWithOne()
    {
        // The exporter and the remover both ask the runner, so this is the one place the rule is chosen.
        var image = ToImage(NoisySky(seed: 30), NoisySky(seed: 31), NoisySky(seed: 32));

        var (appliedOld, minOld, balanceOld) = ChunkedNafnetRunner.InputStretchParameters(image, absent: null);
        var (appliedNone, minNone, balanceNone) = ChunkedNafnetRunner.InputStretchParameters(image, absent: null, blackPointSigma: null);
        var (appliedDepth, minDepth, balanceDepth) = ChunkedNafnetRunner.InputStretchParameters(image, absent: null, blackPointSigma: Depth);
        var (expectedMin, expectedBalance) = image.MtfStretchParametersFromSkyNoise(AiNafnetInputs.TargetMedian, Depth);
        var (minimumMin, minimumBalance) = image.MtfStretchParameters(AiNafnetInputs.TargetMedian);

        appliedOld.ShouldBeTrue();
        appliedNone.ShouldBeTrue();
        appliedDepth.ShouldBeTrue();
        minOld.ShouldNotBeNull().ShouldBe(minimumMin);
        balanceOld.ShouldNotBeNull().ShouldBe(minimumBalance);
        minNone.ShouldNotBeNull().ShouldBe(minimumMin);
        balanceNone.ShouldNotBeNull().ShouldBe(minimumBalance);
        minDepth.ShouldNotBeNull().ShouldBe(expectedMin);
        balanceDepth.ShouldNotBeNull().ShouldBe(expectedBalance);
        // And the two rules are not the same stretch on this frame: its darkest pixel is about 5 sigma under the sky.
        (minimumMin[0] - expectedMin[0]).ShouldBeGreaterThan(20 * Sigma);
    }

    [Fact]
    public void UnderADepthTheLinearityTestReadsTheSkyNoiseAndNotTheDeepestPixel()
    {
        // One pixel 0.29 under the sky puts the median past the threshold above the MINIMUM, so the minimum's rule calls
        // the frame already stretched and feeds it as it is. Under a depth the same frame is still stretched: its median
        // stands 40 x 0.002 = 0.08 above its black point, whatever its deepest pixel is.
        var planes = new[] { NoisySky(seed: 40, level: 0.30f), NoisySky(seed: 41, level: 0.30f), NoisySky(seed: 42, level: 0.30f) };
        foreach (var plane in planes)
        {
            plane[100, 100] = 0.01f;
        }
        var image = ToImage(planes);

        ChunkedNafnetRunner.InputStretchParameters(image, absent: null).Applied.ShouldBeFalse();
        ChunkedNafnetRunner.InputStretchParameters(image, absent: null, blackPointSigma: Depth).Applied.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0.0025f, true)]   // 40 x 0.0025 = 0.10, under the threshold of 0.125: stretched
    [InlineData(0.0040f, false)]  // 40 x 0.0040 = 0.16, over it: fed as it is
    public void UnderADepthTheMediansHeightAboveTheBlackPointIsWhatTheThresholdReads(float sigma, bool stretched)
    {
        var image = ToImage(NoisySky(seed: 50, sigma, level: 0.5f), NoisySky(seed: 51, sigma, level: 0.5f), NoisySky(seed: 52, sigma, level: 0.5f));

        ChunkedNafnetRunner.InputStretchParameters(image, absent: null, blackPointSigma: Depth).Applied.ShouldBe(stretched);
    }

    [Fact]
    public void FlatBlocksInsideTheFrameAreLeftOutOfTheSkyNoise()
    {
        // A zero-filled patch inside the frame, which no canvas ring flags: 24 of the 56 blocks, more than the fifth
        // the quantile sits at. Counted as blocks of zero noise they would leave this frame of noisy sky with no answer.
        var plane = NoisySky(seed: 60);
        for (var y = 64; y < 320; y++)
        {
            for (var x = 64; x < 448; x++)
            {
                plane[y, x] = 0f;
            }
        }

        ToImage(plane).TryEstimateSkyNoise(0, exclude: null, out var sigma).ShouldBeTrue();

        sigma.ShouldBe(Sigma, Sigma * 0.06f);
    }
}
