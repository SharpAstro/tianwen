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

    // Where the sky map's pipeline draws, a narrowed frame is not sound, so the tick is a full frame there.
    [Theory]
    [InlineData(GuiTab.SkyMap)]
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

        var (full, _) = Run(frames, 10, 10_000, GuiTab.SkyMap);

        // Its own interval and the clock's coincide, and each is a full frame here.
        full.ShouldBeInRange(10, 11);
    }

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
            Changed = changed,
        };
        frames.Check(1_600, move).ShouldBeTrue();
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
