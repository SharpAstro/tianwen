using System;
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

    // A pixel of drift at the sidereal rate: 60 degrees down 800 px is 270 arcseconds a pixel, 18 s. Never under a
    // second (a field of a degree) nor over a minute (the whole sky in a small window), and a second with no view yet.
    [Theory]
    [InlineData(60.0, 800f, 17.95)]
    [InlineData(60.0, 1600f, 8.98)]
    [InlineData(90.0, 480f, 44.88)]
    [InlineData(1.0, 800f, 1.0)]
    [InlineData(180.0, 200f, 60.0)]
    [InlineData(60.0, 0f, 1.0)]
    [InlineData(0.0, 800f, 1.0)]
    public void TheAtlasIsDrawnWhenItsSkyHasMovedAPixel(double fieldOfViewDeg, float heightPx, double seconds)
        => GuiTimeCadence.SkyDrift(fieldOfViewDeg, heightPx).TotalSeconds.ShouldBe(seconds, 0.01);

    // A focused field's caret flips every 530 ms on the app clock. A host that leaves its redraw check unasked while
    // nothing is owed must be asked again by the flip, on the arithmetic the blink itself uses.
    [Theory]
    [InlineData(0L, 530)]
    [InlineData(100L, 430)]
    [InlineData(529L, 1)]
    [InlineData(530L, 530)]
    [InlineData(1_000L, 60)]
    public void TheCaretsNextFlipIsATimedNeed(long nowMs, int untilMs)
    {
        const long ticksPerSecond = 10_000_000;
        var now = nowMs * ticksPerSecond / 1000;

        var until = GuiTimeCadence.UntilCaretFlips(now, ticksPerSecond);

        until.ShouldBe(TimeSpan.FromMilliseconds(untilMs));
        // The phase really is another one then, and not a millisecond sooner.
        var then = now + (long)(until.TotalSeconds * ticksPerSecond);
        DIR.Lib.CaretBlink.PhaseAt(then, ticksPerSecond).ShouldBe(DIR.Lib.CaretBlink.PhaseAt(now, ticksPerSecond) + 1);
        DIR.Lib.CaretBlink.PhaseAt(then - (ticksPerSecond / 1000), ticksPerSecond).ShouldBe(DIR.Lib.CaretBlink.PhaseAt(now, ticksPerSecond));
    }

    [Fact]
    public void TheAtlassIntervalIsTheOneItsViewAsksFor()
        => GuiTimeCadence.Interval(GuiTab.SkyMap, runGoingOn: false, previewCapturing: false, slewInFlight: false, TimeSpan.FromSeconds(9))
            .ShouldBe(TimeSpan.FromSeconds(9));

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

    // The Sky Map may since its pipeline draws through the renderer's clip stack (#2130). The viewer tabs, which can lay
    // the sky behind a frame, wait until a narrowed frame has been looked at there.
    [Theory]
    [InlineData(GuiTab.Home, true)]
    [InlineData(GuiTab.Equipment, true)]
    [InlineData(GuiTab.Planner, true)]
    [InlineData(GuiTab.Session, true)]
    [InlineData(GuiTab.Notifications, true)]
    [InlineData(GuiTab.SkyMap, true)]
    [InlineData(GuiTab.LiveSession, false)]
    [InlineData(GuiTab.Guider, false)]
    public void TheClockRepaintsAloneOnlyWhereANarrowedFrameIsKnownSound(GuiTab tab, bool alone)
        => GuiTimeCadence.ClockMayRepaintAlone(tab).ShouldBe(alone);

    /// <summary>A window that has drawn its first frame.</summary>
    private static GuiFrameReasons Drawn()
    {
        var reasons = new GuiFrameReasons();
        reasons.TakeIsClockOnly();
        return reasons;
    }
}
