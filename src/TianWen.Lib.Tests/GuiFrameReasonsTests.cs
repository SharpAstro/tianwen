using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a GUI frame repaints follows WHY it is owed (#2130): the clock's rectangle alone when the clock's tick is its only
/// cause, everything otherwise, an unknown cause included.
/// </summary>
public class GuiFrameReasonsTests
{
    [Fact]
    public void TheFirstFrameIsFull()
    {
        var reasons = new GuiFrameReasons();
        reasons.ClockTick();
        reasons.TakeIsClockOnly().ShouldBeFalse("a new window has no previous frame to keep");
    }

    [Fact]
    public void ATickAloneRepaintsTheClockAlone()
    {
        var reasons = Drawn();
        reasons.ClockTick();
        reasons.TakeIsClockOnly().ShouldBeTrue();
    }

    // The order must not matter: a change may be noticed before the tick or after it, in one check or across several
    // while a frame is held back (paced, minimised).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnyOtherCauseMakesTheFrameFull(bool tickFirst)
    {
        var reasons = Drawn();
        if (tickFirst)
        {
            reasons.ClockTick();
            reasons.Full();
        }
        else
        {
            reasons.Full();
            reasons.ClockTick();
        }

        reasons.TakeIsClockOnly().ShouldBeFalse();
    }

    [Fact]
    public void AFrameWithNoStatedCauseIsFull()
        => Drawn().TakeIsClockOnly().ShouldBeFalse("a frame nobody described is not one to take chances with");

    [Fact]
    public void TheVerdictIsTakenOnce()
    {
        var reasons = Drawn();
        reasons.Full();
        reasons.TakeIsClockOnly().ShouldBeFalse();

        // The next frame starts over: the full repaint it was owed has been drawn.
        reasons.ClockTick();
        reasons.TakeIsClockOnly().ShouldBeTrue();
        reasons.TakeIsClockOnly().ShouldBeFalse("the tick was this frame's, not the next one's");
    }

    // The intervals are the rule: nothing time-driven leans on the clock's tick, so each tab states its own.
    [Theory]
    [InlineData(GuiTab.Home, 60.0)]
    [InlineData(GuiTab.Equipment, 60.0)]
    [InlineData(GuiTab.Planner, 60.0)]
    [InlineData(GuiTab.Session, 60.0)]
    [InlineData(GuiTab.Guider, 60.0)]
    [InlineData(GuiTab.Notifications, 60.0)]
    [InlineData(GuiTab.LiveSession, 30.0)]
    [InlineData(GuiTab.SkyMap, 1.0)]
    public void AnIdleTabAsksForAFrameAtItsOwnInterval(GuiTab tab, double seconds)
        => GuiTimeCadence.Interval(tab, runGoingOn: false, previewCapturing: false, slewInFlight: false).TotalSeconds.ShouldBe(seconds);

    [Theory]
    [InlineData(GuiTab.Home)]
    [InlineData(GuiTab.Planner)]
    [InlineData(GuiTab.SkyMap)]
    [InlineData(GuiTab.LiveSession)]
    public void ARunsProgressSurfacesAtHalfASecondOnEveryTab(GuiTab tab)
        => GuiTimeCadence.Interval(tab, runGoingOn: true, previewCapturing: false, slewInFlight: false).ShouldBe(GuiTimeCadence.Progress);

    [Fact]
    public void APreviewCaptureIsProgressOnTheTabThatShowsIt()
    {
        GuiTimeCadence.Interval(GuiTab.LiveSession, runGoingOn: false, previewCapturing: true, slewInFlight: false).ShouldBe(GuiTimeCadence.Progress);
        // Nowhere else is its bar on show.
        GuiTimeCadence.Interval(GuiTab.Planner, runGoingOn: false, previewCapturing: true, slewInFlight: false).ShouldBe(GuiTimeCadence.Minute);
    }

    [Fact]
    public void ASlewsEtaCountsDownByTheSecondAndNeverSlowsAFasterTab()
    {
        GuiTimeCadence.Interval(GuiTab.Planner, runGoingOn: false, previewCapturing: false, slewInFlight: true).ShouldBe(GuiTimeCadence.Second);
        GuiTimeCadence.Interval(GuiTab.LiveSession, runGoingOn: false, previewCapturing: true, slewInFlight: true).ShouldBe(GuiTimeCadence.Progress);
    }

    // Where the sky map's own pipeline can draw, a narrowed frame is not sound yet: it sets its own scissor.
    [Theory]
    [InlineData(GuiTab.Home, true)]
    [InlineData(GuiTab.Equipment, true)]
    [InlineData(GuiTab.Planner, true)]
    [InlineData(GuiTab.Session, true)]
    [InlineData(GuiTab.Notifications, true)]
    [InlineData(GuiTab.SkyMap, false)]
    [InlineData(GuiTab.LiveSession, false)]
    [InlineData(GuiTab.Guider, false)]
    public void TheClockRepaintsAloneOnlyWhereNothingDrawsWithItsOwnScissor(GuiTab tab, bool alone)
        => GuiTimeCadence.ClockMayRepaintAlone(tab).ShouldBe(alone);

    /// <summary>A window that has drawn its first frame.</summary>
    private static GuiFrameReasons Drawn()
    {
        var reasons = new GuiFrameReasons();
        reasons.TakeIsClockOnly();
        return reasons;
    }
}
