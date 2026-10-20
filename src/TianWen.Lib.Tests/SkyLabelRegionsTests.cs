using System;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>The part of a sky label a host's photograph leaves seen, which is its whole click region (#1513).</summary>
public class SkyLabelRegionsTests
{
    private static RectF32[] Outside(RectF32 label, RectF32? hole)
    {
        Span<RectF32> into = stackalloc RectF32[4];
        var count = SkyLabelRegions.Outside(label, hole, into);
        return into[..count].ToArray();
    }

    private static float Area(RectF32[] rects)
    {
        var area = 0f;
        foreach (var r in rects)
        {
            area += r.Width * r.Height;
        }
        return area;
    }

    [Fact]
    public void WithNothingOverTheMapTheLabelIsItsOwnRegion()
        => Outside(new RectF32(10, 10, 100, 20), null).ShouldBe([new RectF32(10, 10, 100, 20)]);

    [Fact]
    public void ALabelClearOfThePhotographKeepsItsWholeRegion()
        => Outside(new RectF32(10, 10, 100, 20), new RectF32(200, 200, 50, 50)).ShouldBe([new RectF32(10, 10, 100, 20)]);

    [Fact]
    public void ALabelWhollyBehindThePhotographTakesNoPressAtAll()
        => Outside(new RectF32(60, 60, 40, 10), new RectF32(50, 50, 200, 200)).ShouldBeEmpty();

    [Fact]
    public void ALabelRunningOffThePhotographsRightEdgeKeepsOnlyTheSeenPart()
        => Outside(new RectF32(220, 60, 100, 20), new RectF32(50, 50, 200, 200)).ShouldBe([new RectF32(250, 60, 70, 20)]);

    [Fact]
    public void ALabelOverACornerKeepsTheTwoSeenPiecesAndNothingCovered()
    {
        var label = new RectF32(30, 30, 40, 40);
        var hole = new RectF32(50, 50, 100, 100);

        var pieces = Outside(label, hole);

        Area(pieces).ShouldBe(40 * 40 - 20 * 20, "the label less the corner the photograph covers");
        foreach (var p in pieces)
        {
            var overlaps = p.X < hole.X + hole.Width && p.X + p.Width > hole.X && p.Y < hole.Y + hole.Height && p.Y + p.Height > hole.Y;
            overlaps.ShouldBeFalse($"{p} lies under the photograph");
        }
    }

    [Fact]
    public void APhotographInsideALabelLeavesItsFourSides()
    {
        var pieces = Outside(new RectF32(0, 0, 100, 100), new RectF32(40, 40, 20, 20));

        pieces.Length.ShouldBe(4);
        Area(pieces).ShouldBe(100 * 100 - 20 * 20);
    }
}
