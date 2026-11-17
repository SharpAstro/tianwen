using System;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The GUI's frame decision on a clock the test turns (#2130): whether a frame is owed and what it repaints. These are the
/// guard against the idle cost coming back: an idle window is the clock's rectangle once a second and nothing else.
/// </summary>
public class GuiFrameSchedulerTests
{
    private const long Hz = 1000;
    private static readonly RectF32 Clock = new RectF32(900f, 0f, 180f, 30f);
    private static readonly RectF32 Crosshair = new RectF32(400f, 100f, 60f, 500f);

    private static GuiFrameInputs Idle(GuiTab tab = GuiTab.Home) => new GuiFrameInputs(
        tab, RunGoingOn: false, PreviewCapturing: false, SlewInFlight: false,
        Changed: false, RedrawPending: false, PendingForPointerMoveAlone: false, PointerMoveChanged: null);

    /// <summary>A scheduler whose window has drawn its first, full, frame.</summary>
    private static GuiFrameScheduler Drawn(GuiTab tab = GuiTab.Home)
    {
        var frames = new GuiFrameScheduler(now: 0, Hz);
        frames.Check(0, Idle(tab) with { RedrawPending = true }).ShouldBeTrue("a new window is owed its first frame");
        frames.BeginFrame(flagged: false, shuttingDown: false, tab, Clock).ShouldBeEmpty("which is a full one");
        return frames;
    }

    /// <summary>Runs the idle loop from <paramref name="from"/> to <paramref name="to"/> ms and counts the frames by kind.</summary>
    private static (int Full, int Narrow) Run(GuiFrameScheduler frames, long from, long to, GuiTab tab, Func<long, GuiFrameInputs>? at = null)
    {
        var full = 0;
        var narrow = 0;
        for (var now = from; now <= to; now += 10)
        {
            if (!frames.Check(now, at?.Invoke(now) ?? Idle(tab)))
            {
                continue;
            }

            if (frames.BeginFrame(flagged: false, shuttingDown: false, tab, Clock).Count == 0)
            {
                full++;
            }
            else
            {
                narrow++;
            }
        }

        return (full, narrow);
    }

    // THE guard. Ten seconds with nothing happening: one settling frame after the first, then the clock alone.
    [Theory]
    [InlineData(GuiTab.Home)]
    [InlineData(GuiTab.Equipment)]
    [InlineData(GuiTab.Planner)]
    [InlineData(GuiTab.Session)]
    [InlineData(GuiTab.Notifications)]
    public void AnIdleWindowRepaintsItsClockOnceASecondAndNothingElse(GuiTab tab)
    {
        var frames = Drawn(tab);

        var (full, narrow) = Run(frames, 10, 10_000, tab);

        full.ShouldBe(1, "the one settling frame after the window's first");
        narrow.ShouldBe(9, "nine clock ticks, the tenth second's coinciding with the settling frame");
    }

    [Fact]
    public void AClockFrameRepaintsTheClocksRectangle()
    {
        var frames = Drawn();
        Run(frames, 10, 1_500, GuiTab.Home);   // past the settling frame

        frames.Check(2_100, Idle()).ShouldBeTrue();
        var damage = frames.BeginFrame(flagged: false, shuttingDown: false, GuiTab.Home, Clock);

        damage.ShouldBe([Clock]);
        frames.FrameIsNarrow.ShouldBeTrue("so the host clears no redraw flag after it");
    }

    // A change is drawn at once, in full, and once more a second later; the second asks for no third.
    [Fact]
    public void AChangeIsOneFullFrameAndOneSettlingFrame()
    {
        var frames = Drawn();
        Run(frames, 10, 3_000, GuiTab.Home);

        var (full, _) = Run(frames, 3_010, 9_000, GuiTab.Home, now => Idle() with { Changed = now == 3_500 });

        full.ShouldBe(2);
    }

    // A flag a background writer set between the check and the frame: the frame is about to be followed by the flags'
    // clearing only if it is full, so it must be.
    [Fact]
    public void AFlagSetSinceTheCheckMakesAClockFrameFull()
    {
        var frames = Drawn();
        Run(frames, 10, 1_500, GuiTab.Home);

        frames.Check(2_100, Idle()).ShouldBeTrue();
        frames.BeginFrame(flagged: true, shuttingDown: false, GuiTab.Home, Clock).ShouldBeEmpty();
        frames.FrameIsNarrow.ShouldBeFalse();
    }

    [Fact]
    public void ACauseTheLoopRaisedItselfIsAFullFrame()
    {
        var frames = Drawn();
        Run(frames, 10, 1_500, GuiTab.Home);

        // The clock ticks in the same iteration an expose arrives.
        frames.Check(2_100, Idle() with { RedrawPending = true }).ShouldBeTrue();
        frames.BeginFrame(flagged: false, shuttingDown: false, GuiTab.Home, Clock).ShouldBeEmpty();
    }

    [Fact]
    public void AClockThatWasNotDrawnCannotBeRepaintedAlone()
    {
        var frames = Drawn();
        Run(frames, 10, 1_500, GuiTab.Home);

        frames.Check(2_100, Idle()).ShouldBeTrue();
        frames.BeginFrame(flagged: false, shuttingDown: false, GuiTab.Home, clockRect: null).ShouldBeEmpty();
    }

    // Where the viewer can lay the sky behind a frame, a narrowed frame has not been looked at, so the tick is a full
    // frame there. (The Sky Map itself may be narrowed: AWideAtlasIsDrawnWhenItsSkyHasMoved.)
    [Theory]
    [InlineData(GuiTab.LiveSession)]
    [InlineData(GuiTab.Guider)]
    public void NoFrameIsNarrowedWhereAPipelineSetsItsOwnScissor(GuiTab tab)
    {
        var frames = Drawn(tab);

        var (_, narrow) = Run(frames, 10, 5_000, tab);

        narrow.ShouldBe(0);
    }

    [Fact]
    public void TheSkyMapIsDrawnOnceASecondForItsDrift()
    {
        var frames = Drawn(GuiTab.SkyMap);

        var (full, narrow) = Run(frames, 10, 10_000, GuiTab.SkyMap);

        // Its own interval and the clock's coincide, and each is a full frame here.
        full.ShouldBeInRange(10, 11);
        narrow.ShouldBe(0);
    }

    // THE guard for the atlas (llvmpipe at 1200x800 sat at 15 % of a core, a Raspberry Pi 3B the same): a view whose
    // sky takes 18 s to move half a pixel is drawn every 18 s (and once more a second later, the settling frame every
    // caused frame is owed), and the clock ticks in its own rectangle between.
    [Fact]
    public void AWideAtlasIsDrawnWhenItsSkyHasMoved()
    {
        var frames = Drawn(GuiTab.SkyMap);
        var idle = Idle(GuiTab.SkyMap) with { SkyDrift = TimeSpan.FromSeconds(18) };

        var (full, narrow) = Run(frames, 10, 60_000, GuiTab.SkyMap, _ => idle);

        full.ShouldBeInRange(6, 8, "the first frame's settling frame, then a frame and its settling frame each 18 s");
        narrow.ShouldBeInRange(52, 54, "every other second is the clock alone");
    }

    // The sky's frame is drawn ON the clock's tick: a change in the middle of a second used to leave the two half a
    // second apart from then on, two frames a second where one does.
    [Fact]
    public void ATimeDrivenFrameOfASecondIsTheClocksFrame()
    {
        var frames = Drawn(GuiTab.SkyMap);
        Run(frames, 10, 2_000, GuiTab.SkyMap);
        frames.Check(2_500, Idle(GuiTab.SkyMap) with { Changed = true }).ShouldBeTrue();
        frames.BeginFrame(flagged: false, shuttingDown: false, GuiTab.SkyMap, Clock).ShouldBeEmpty();

        var (full, narrow) = Run(frames, 2_510, 12_000, GuiTab.SkyMap);

        (full + narrow).ShouldBeInRange(9, 11, "one frame a second, the clock in it");
    }

    /// <summary>Runs the idle atlas for a minute on a renderer whose full frame costs <paramref name="costMs"/> of processor.</summary>
    private static int FullFramesInAMinute(int costMs, TimeSpan? skyDrift = null, bool runGoingOn = false)
    {
        var frames = Drawn(GuiTab.SkyMap);
        var cpu = TimeSpan.Zero;
        var full = 0;
        for (long now = 10; now <= 60_000; now += 10)
        {
            var inputs = Idle(GuiTab.SkyMap) with { SkyDrift = skyDrift, RunGoingOn = runGoingOn, ProcessCpu = cpu + TimeSpan.FromMilliseconds(1) };
            if (frames.Check(now, inputs) && frames.BeginFrame(flagged: false, shuttingDown: false, GuiTab.SkyMap, Clock).Count == 0)
            {
                full++;
                cpu += TimeSpan.FromMilliseconds(costMs);
            }
        }

        return full;
    }

    // A frame that costs 2 ms of processor is drawn as often as the content asks.
    [Fact]
    public void AFastRendererKeepsTheContentsInterval()
        => FullFramesInAMinute(costMs: 2).ShouldBeInRange(58, 61);

    // One that costs 150 ms (llvmpipe, the atlas at 1200x800) is held to a twentieth of a core: a frame and its
    // settling frame each 6 s.
    [Fact]
    public void ASlowRendererIsHeldToItsShareOfACore()
        => FullFramesInAMinute(costMs: 150).ShouldBeInRange(19, 23);

    // However slow, an interval is not stretched past ten seconds for cost alone.
    [Fact]
    public void CostNeverSlowsAnIntervalPastItsBound()
        => FullFramesInAMinute(costMs: 2_000).ShouldBeInRange(11, 13);

    // A run's progress is time-driven too, and paced the same way.
    [Fact]
    public void ARunsProgressIsPacedByCostAsWell()
    {
        FullFramesInAMinute(costMs: 2, runGoingOn: true).ShouldBeInRange(115, 121);
        FullFramesInAMinute(costMs: 150, runGoingOn: true).ShouldBeInRange(19, 23);
    }

    // An interval already slower than what cost asks for is left alone.
    [Fact]
    public void CostDoesNotShortenASlowInterval()
        => FullFramesInAMinute(costMs: 150, skyDrift: TimeSpan.FromSeconds(18)).ShouldBeInRange(6, 8);

    [Fact]
    public void ARunsProgressIsDrawnTwiceASecond()
    {
        var frames = Drawn();

        var (full, _) = Run(frames, 10, 10_000, GuiTab.Home, _ => Idle() with { RunGoingOn = true });

        full.ShouldBeInRange(19, 21);
    }

    // A pointer move whose widget says what it changed repaints that, and asks for no settling frame.
    [Fact]
    public void APointerMoveThatSaysWhatItChangedRepaintsThatAlone()
    {
        var frames = Drawn(GuiTab.Planner);
        Run(frames, 10, 1_500, GuiTab.Planner);

        var move = Idle(GuiTab.Planner) with { RedrawPending = true, PendingForPointerMoveAlone = true, PointerMoveChanged = Crosshair };
        frames.Check(1_600, move).ShouldBeTrue();
        frames.BeginFrame(flagged: false, shuttingDown: false, GuiTab.Planner, Clock).ShouldBe([Crosshair]);

        // Nothing follows it but the clock.
        var (full, narrow) = Run(frames, 1_610, 4_000, GuiTab.Planner);
        full.ShouldBe(0, "a narrowed frame is followed by no settling frame");
        narrow.ShouldBe(3, "the clock at two, three and four seconds");
    }

    [Fact]
    public void APointerMoveAndTheClockTogetherRepaintBoth()
    {
        var frames = Drawn(GuiTab.Planner);
        Run(frames, 10, 1_500, GuiTab.Planner);

        var move = Idle(GuiTab.Planner) with { RedrawPending = true, PendingForPointerMoveAlone = true, PointerMoveChanged = Crosshair };
        frames.Check(2_100, move).ShouldBeTrue();
        frames.BeginFrame(flagged: false, shuttingDown: false, GuiTab.Planner, Clock).ShouldBe([Crosshair, Clock]);
    }

    // The three ways a move's frame is not the move's alone: the widget did not say, the loop owes more than moves, or
    // something else changed too.
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void APointerMoveThatCannotBeNarrowedIsAFullFrame(bool stated, bool moveAlone, bool changed)
    {
        var frames = Drawn(GuiTab.Planner);
        Run(frames, 10, 1_500, GuiTab.Planner);

        var move = Idle(GuiTab.Planner) with
        {
            RedrawPending = true,
            PendingForPointerMoveAlone = moveAlone,
            PointerMoveChanged = stated ? Crosshair : null,
            PointerMoveUnstated = !stated,
            Changed = changed,
        };
        frames.Check(1_600, move).ShouldBeTrue();
        frames.BeginFrame(flagged: false, shuttingDown: false, GuiTab.Planner, Clock).ShouldBeEmpty();
    }

    // The loop draws moves at most thirty times a second and drops the ones between. What a dropped move changed is
    // drawn anyway, a thirtieth of a second on, or the follower would rest where the pointer was.
    [Fact]
    public void AMoveTheLoopDidNotArmIsDrawnAnyway()
    {
        var frames = Drawn(GuiTab.Planner);
        Run(frames, 10, 1_500, GuiTab.Planner);

        frames.Check(1_600, Idle(GuiTab.Planner) with { PointerMoveChanged = Crosshair }).ShouldBeFalse("the loop armed nothing");
        frames.Check(1_620, Idle(GuiTab.Planner)).ShouldBeFalse();
        frames.Check(1_640, Idle(GuiTab.Planner)).ShouldBeTrue("a thirtieth of a second after the move");
        frames.BeginFrame(flagged: false, shuttingDown: false, GuiTab.Planner, Clock).ShouldBe([Crosshair]);

        frames.Check(1_700, Idle(GuiTab.Planner)).ShouldBeFalse("drawn once");
    }

    [Fact]
    public void WhatAnUnarmedMoveChangedIsKeptForTheNextFrame()
    {
        var frames = Drawn(GuiTab.Planner);
        Run(frames, 10, 1_500, GuiTab.Planner);
        var second = new RectF32(470f, 100f, 60f, 500f);

        frames.Check(1_600, Idle(GuiTab.Planner) with { PointerMoveChanged = Crosshair }).ShouldBeFalse();
        var armed = Idle(GuiTab.Planner) with { RedrawPending = true, PendingForPointerMoveAlone = true, PointerMoveChanged = second };
        frames.Check(1_610, armed).ShouldBeTrue();

        frames.BeginFrame(flagged: false, shuttingDown: false, GuiTab.Planner, Clock).ShouldBe([Crosshair, second]);
        frames.Check(1_650, Idle(GuiTab.Planner)).ShouldBeFalse("nothing is left to trail");
    }

    // A move something took without saying what it changed (a hover highlight) beside one that said: the frame is full,
    // whichever came first.
    [Fact]
    public void AnUnstatedMoveBesideAStatedOneIsAFullFrame()
    {
        var frames = Drawn(GuiTab.Planner);
        Run(frames, 10, 1_500, GuiTab.Planner);

        frames.Check(1_600, Idle(GuiTab.Planner) with { PointerMoveUnstated = true }).ShouldBeFalse();
        var armed = Idle(GuiTab.Planner) with { RedrawPending = true, PendingForPointerMoveAlone = true, PointerMoveChanged = Crosshair };
        frames.Check(1_610, armed).ShouldBeTrue();

        frames.BeginFrame(flagged: false, shuttingDown: false, GuiTab.Planner, Clock).ShouldBeEmpty();
    }

    [Fact]
    public void AClosingWindowDrawsInFull()
    {
        var frames = Drawn();
        Run(frames, 10, 1_500, GuiTab.Home);

        frames.Check(2_100, Idle()).ShouldBeTrue();
        frames.BeginFrame(flagged: false, shuttingDown: true, GuiTab.Home, Clock).ShouldBeEmpty();
    }
}
