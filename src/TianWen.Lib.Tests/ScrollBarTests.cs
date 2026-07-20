using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for <see cref="ScrollBar.HandleWheel"/> -- the shared wheel-delta -> row-offset math used
/// by the planner target list and the equipment device list. Pins the trackpad regression: a precision
/// wheel / two-finger swipe (and the browser bridge's <c>deltaY / 100</c>) emits sub-1 deltas, which the
/// old <c>(int)scrollY * WheelStep</c> truncated to zero so the list never scrolled.
/// </summary>
public sealed class ScrollBarTests
{
    private const int Total = 100;
    private const int Visible = 10; // maxOffset = 90

    [Fact]
    public void FullNotch_ScrollsWholeWheelStep()
    {
        float accum = 0f;
        // Positive scrollY scrolls toward the start (lower offset); one notch down = -1.
        var down = ScrollBar.HandleWheel(-1f, offset: 10, Total, Visible, ref accum);
        down.ShouldBe(10 + ScrollBar.WheelStep);
        accum.ShouldBe(0f); // whole notch leaves no remainder

        var up = ScrollBar.HandleWheel(+1f, offset: 10, Total, Visible, ref accum);
        up.ShouldBe(10 - ScrollBar.WheelStep);
    }

    [Fact]
    public void SmallTrackpadDeltas_AccumulateIntoAScroll()
    {
        // The bug: each -0.2 delta * WheelStep(3) = -0.6 -> truncated to 0 under the old code, so the
        // offset never moved. With the accumulator, repeated small deltas must eventually advance.
        float accum = 0f;
        var offset = 0;
        for (var i = 0; i < 10; i++)
        {
            offset = ScrollBar.HandleWheel(-0.2f, offset, Total, Visible, ref accum);
        }
        offset.ShouldBeGreaterThan(0, "ten small trackpad deltas must accumulate into at least one scrolled row");
    }

    [Fact]
    public void Clamps_AtZeroAndMax()
    {
        float accum = 0f;
        // Cannot scroll above the start.
        ScrollBar.HandleWheel(+5f, offset: 0, Total, Visible, ref accum).ShouldBe(0);
        accum = 0f;
        // Cannot scroll past the end (maxOffset = Total - Visible = 90).
        ScrollBar.HandleWheel(-100f, offset: 90, Total, Visible, ref accum).ShouldBe(90);
    }
}
