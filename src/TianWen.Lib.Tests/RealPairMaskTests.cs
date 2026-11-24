using System;
using Shouldly;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Which pixels of a real pair count in the loss (<see cref="RealPairMask"/>): the builder's subtracted footprint, away
/// from the sources it kept and from its saturated stars, and never an absent pixel.
/// </summary>
public class RealPairMaskTests
{
    private const int W = 160;
    private const int H = 120;
    private const double Fwhm = 2.5;

    private static FittedStar Star(float x, float y, StarFitOutcome outcome, bool saturated = false)
        => new FittedStar(x, y, 50f, 0.1f, 1f, 0.01f, 0.001f, outcome, saturated, false, float.NaN, float.NaN, false, float.NaN);

    // A footprint of discs of the radius round each position.
    private static BitMatrix Footprint(double radius, params (double X, double Y)[] at)
    {
        var mask = new BitMatrix(H, W);
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                foreach (var (cx, cy) in at)
                {
                    if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= radius * radius)
                    {
                        mask[y, x] = true;
                    }
                }
            }
        }
        return mask;
    }

    [Fact]
    public void ASubtractedStarsFootprintCountsAndNothingOutsideItDoes()
    {
        var footprint = Footprint(6, (40, 40), (100, 80));
        FittedStar[] catalogue = [Star(40, 40, StarFitOutcome.Subtracted), Star(100, 80, StarFitOutcome.Subtracted)];

        var counted = RealPairMask.Counted(footprint, catalogue, Fwhm);

        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                counted[y, x].ShouldBe(footprint[y, x], $"x{x} y{y}: with nothing kept and nothing saturated the footprint is the mask");
            }
        }
        counted.PopCount().ShouldBeGreaterThan(200, "two discs of 6 px");
    }

    [Theory]
    [InlineData(StarFitOutcome.Knot)]
    [InlineData(StarFitOutcome.TooNarrow)]
    [InlineData(StarFitOutcome.NoFit)]
    [InlineData(StarFitOutcome.Merged)]
    public void TheSkyNearASourceTheBuilderKeptDoesNotCount(StarFitOutcome keptAs)
    {
        // A subtracted star at (40, 40) and a kept source 5 px to its right: the footprint's side toward it is set aside.
        var footprint = Footprint(6, (40, 40));
        FittedStar[] catalogue = [Star(40, 40, StarFitOutcome.Subtracted), Star(45, 40, keptAs)];
        var reach = RealPairMask.KeptSourceFwhm * Fwhm;

        var counted = RealPairMask.Counted(footprint, catalogue, Fwhm);

        var setAside = 0;
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var near = ((x - 45.0) * (x - 45.0)) + ((y - 40.0) * (y - 40.0)) <= reach * reach;
                counted[y, x].ShouldBe(footprint[y, x] && !near, $"x{x} y{y}");
                if (footprint[y, x] && near)
                {
                    setAside++;
                }
            }
        }
        setAside.ShouldBeGreaterThan(20, "the kept source's zone overlaps the footprint");
        counted[40, 36].ShouldBeTrue("the far side of the star, over two PSF widths from the kept source, still counts");
    }

    [Fact]
    public void ASaturatedStarSetsItsReachAsideAndAStarBeyondItStillCounts()
    {
        // A saturated star whose footprint is 40 px wide, and an ordinary star 50 px away.
        var footprint = Footprint(20, (60, 60));
        var ordinary = Footprint(5, (120, 60));
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                footprint[y, x] = footprint[y, x] || ordinary[y, x];
            }
        }
        FittedStar[] catalogue = [Star(60, 60, StarFitOutcome.Subtracted, saturated: true), Star(120, 60, StarFitOutcome.Subtracted)];

        var counted = RealPairMask.Counted(footprint, catalogue, Fwhm);

        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var inReach = ((x - 60.0) * (x - 60.0)) + ((y - 60.0) * (y - 60.0)) <= RealPairMask.SaturatedReachPx * RealPairMask.SaturatedReachPx;
                counted[y, x].ShouldBe(footprint[y, x] && !inReach, $"x{x} y{y}");
            }
        }
        counted[60, 60].ShouldBeFalse("the saturated core");
        counted[60, 120].ShouldBeTrue("the ordinary star's core, 60 px from the saturated one");
        counted[60, 116].ShouldBeTrue("its near side, 56 px away");
    }

    [Fact]
    public void AZoneIsCutAtTheFramesEdgeAndAnAbsentPixelNeverCounts()
    {
        // The whole frame is footprint; a kept source sits on the corner pixel and a saturated star off the frame's right edge.
        var footprint = new BitMatrix(H, W);
        footprint.SetAll();
        footprint.ClearPadding();
        FittedStar[] catalogue = [Star(0, 0, StarFitOutcome.Knot), Star(W + 10, 60, StarFitOutcome.Subtracted, saturated: true)];
        var absent = new BitMatrix(H, W);
        absent[100, 20] = true;

        var counted = RealPairMask.Counted(footprint, catalogue, Fwhm, absent);

        counted[0, 0].ShouldBeFalse("the kept source's own pixel");
        counted[0, 5].ShouldBeFalse("5 px from it, inside two PSF widths");
        counted[0, 6].ShouldBeTrue("6 px from it, outside");
        counted[60, W - 1].ShouldBeFalse("11 px from the saturated star beyond the edge");
        counted[60, W - 20].ShouldBeFalse("30 px from it, on the reach");
        counted[60, W - 21].ShouldBeTrue("31 px from it");
        counted[100, 20].ShouldBeFalse("an absent pixel");
        counted[100, 21].ShouldBeTrue("its neighbour");
    }

    [Fact]
    public void AWidthThatIsNoWidthAndAnAbsentMaskOfAnotherShapeAreRefused()
    {
        var footprint = new BitMatrix(H, W);
        Should.Throw<ArgumentOutOfRangeException>(() => RealPairMask.Counted(footprint, [], 0.0));
        Should.Throw<ArgumentOutOfRangeException>(() => RealPairMask.Counted(footprint, [], double.NaN));
        Should.Throw<ArgumentException>(() => RealPairMask.Counted(footprint, [], Fwhm, new BitMatrix(H, W + 1)));
    }
}
