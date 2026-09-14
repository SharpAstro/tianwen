using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A Bayer mosaic's statistics are taken per photosite colour ON THE MOSAIC, by walking that colour's
/// phases -- no sub-plane is materialised. These pin that the walk lands on the right photosites for
/// every pattern, that green is both phases in one histogram, and that the stretch solver therefore
/// sees three colours rather than one blend broadcast three ways.
/// </summary>
[Collection("Imaging")]
public class CfaChannelStatsTests
{
    private const float RedValue = 10f, BlueValue = 40f;

    public static TheoryData<string, int, int> Patterns => BayerSplitPatternTests.Patterns;

    /// <summary>A mosaic constant per photosite colour, in the parity the pattern's offsets state.</summary>
    private static Image Mosaic(int offsetX, int offsetY, float green1, float green2, int w = 16, int h = 12)
    {
        var plane = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var onRedRow = (y & 1) == (offsetY & 1);
                var onRedCol = (x & 1) == (offsetX & 1);
                plane[y, x] = (onRedRow, onRedCol) switch
                {
                    (true, true) => RedValue,
                    (true, false) => green1,
                    (false, true) => green2,
                    (false, false) => BlueValue,
                };
            }
        }

        var meta = new ImageMeta() with { SensorType = SensorType.RGGB, BayerOffsetX = offsetX, BayerOffsetY = offsetY };
        return new Image([plane], BitDepth.Float32, BlueValue, RedValue, 0f, meta);
    }

    /// <summary>
    /// The walk agrees with the split, pattern by pattern: red over the mosaic IS the split's red plane,
    /// and so on. Read as plain RGGB, a GRBG frame would put green where red is -- which is the cheap
    /// test the split's own doc comment describes, applied to the walk.
    /// </summary>
    [Theory]
    [MemberData(nameof(Patterns))]
    public void EachColourWalksTheSamePhotositesTheSplitCopies(string pattern, int offsetX, int offsetY)
    {
        var mosaic = Mosaic(offsetX, offsetY, green1: 20f, green2: 20f);
        var split = mosaic.SplitBayerChannels(); // [R, G1, G2, B]

        mosaic.GetPedestralMedianAndMADScaledToUnit(0, cfa: CfaChannel.Red)
            .ShouldBe(split.GetPedestralMedianAndMADScaledToUnit(0), $"{pattern}: red");
        mosaic.GetPedestralMedianAndMADScaledToUnit(0, cfa: CfaChannel.Green)
            .ShouldBe(split.GetPedestralMedianAndMADScaledToUnit(1), $"{pattern}: green");
        mosaic.GetPedestralMedianAndMADScaledToUnit(0, cfa: CfaChannel.Blue)
            .ShouldBe(split.GetPedestralMedianAndMADScaledToUnit(3), $"{pattern}: blue");
    }

    /// <summary>
    /// Green is ONE histogram over both diagonal phases. With the two greens at different levels the
    /// median lands between them -- where neither half-plane's median is -- and red and blue are untouched.
    /// </summary>
    [Fact]
    public void GreenIsBothPhasesInOneHistogram()
    {
        var mosaic = Mosaic(offsetX: 1, offsetY: 0, green1: 20f, green2: 30f);
        var split = mosaic.SplitBayerChannels();

        var g = mosaic.GetPedestralMedianAndMADScaledToUnit(0, cfa: CfaChannel.Green).Median;
        var g1 = split.GetPedestralMedianAndMADScaledToUnit(1).Median;
        var g2 = split.GetPedestralMedianAndMADScaledToUnit(2).Median;

        g.ShouldBeGreaterThan(g1);
        g.ShouldBeLessThan(g2);
        mosaic.GetPedestralMedianAndMADScaledToUnit(0, cfa: CfaChannel.Red).Median
            .ShouldBe(split.GetPedestralMedianAndMADScaledToUnit(0).Median);
    }

    /// <summary>An odd stride would step off the phase; it is doubled, so the colour stays pure.</summary>
    [Fact]
    public void AnOddStrideStaysOnThePhase()
    {
        var mosaic = Mosaic(offsetX: 0, offsetY: 1, green1: 20f, green2: 20f, w: 32, h: 32);

        var strided = mosaic.GetPedestralMedianAndMADScaledToUnit(0, pixelStride: 3, cfa: CfaChannel.Red);
        var plain = mosaic.GetPedestralMedianAndMADScaledToUnit(0, cfa: CfaChannel.Red);

        strided.Median.ShouldBe(plain.Median);
        strided.MAD.ShouldBe(plain.MAD);
    }

    [Fact]
    public void AColourStatisticRefusesAFrameThatIsNotAMosaic()
    {
        var mono = new Image([new float[4, 4]], BitDepth.Float32, 1f, 0f, 0f,
            new ImageMeta() with { SensorType = SensorType.Monochrome });

        Should.Throw<InvalidOperationException>(() => mono.Statistics(0, cfa: CfaChannel.Green));
    }

    /// <summary>
    /// <b>The symptom.</b> Linked and Unlinked rendered identically on every OSC sub because the solver
    /// was handed one statistic broadcast three ways. With three real colours, Unlinked positions each
    /// channel's curve on its own median and Linked holds one curve for all three.
    /// </summary>
    [Fact]
    public void AMosaicGivesTheSolverThreeColoursSoUnlinkedDiffersFromLinked()
    {
        var mosaic = Mosaic(offsetX: 1, offsetY: 0, green1: 25f, green2: 25f, w: 64, h: 64);

        var stats = StretchSolver.CollectPerChannelStats(mosaic, mosaic.ChannelCount);
        stats.Length.ShouldBe(3, "a 1-plane mosaic is three colours");
        stats[0].Median.ShouldBeLessThan(stats[1].Median);
        stats[1].Median.ShouldBeLessThan(stats[2].Median);

        var unlinked = StretchSolver.ComputeStretchUniforms(
            StretchMode.Unlinked, StretchParameters.Default, stats, lumaStats: null, mosaic.MaxValue);
        var linked = StretchSolver.ComputeStretchUniforms(
            StretchMode.Linked, StretchParameters.Default, stats, lumaStats: null, mosaic.MaxValue);

        unlinked.Shadows.R.ShouldNotBe(unlinked.Shadows.B, "Unlinked: each colour's curve sits on its own median");
        linked.Shadows.R.ShouldBe(linked.Shadows.B, "Linked: one curve for all three");
        unlinked.ShouldNotBe(linked);
    }
}
