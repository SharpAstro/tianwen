using System;
using System.Collections.Generic;
using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>What the GUI's redraw check knows when the loop asks it whether a frame is owed.</summary>
/// <param name="Tab">The tab on show.</param>
/// <param name="RunGoingOn">A session or a polar alignment routine runs, in the local or the viewed context.</param>
/// <param name="PreviewCapturing">A preview exposure is in flight on the viewed context.</param>
/// <param name="SlewInFlight">The mount is on its way to a target whose ETA the top bar counts down.</param>
/// <param name="Changed">A redraw flag is set, or one of the host's standing reasons holds (a caret's flip, a tooltip
/// falling due, a chart's pending draw, an animation that plays).</param>
/// <param name="RedrawPending">The loop already owes the window a frame for a cause of its own
/// (<c>SdlWindowView.RedrawPending</c>): input a handler took, an expose, a resize, a frame held back.</param>
/// <param name="PendingForPointerMoveAlone">That frame is owed to pointer moves alone
/// (<c>SdlWindowView.RedrawPendingForPointerMoveAlone</c>).</param>
/// <param name="PointerMoveChanged">What the pointer moves taken since the last check changed, when the widget that took
/// them said, and what the tab's settle changed at this check (<see cref="IPointerMoveChange.SettlePointer"/>); null when
/// nothing said. Given whether or not the loop armed a frame for it: a change the settle states has no move to arm one,
/// and a loop before SdlVulkan.Renderer 7.109 armed moves at most thirty times a second and dropped the ones between.</param>
/// <param name="PointerMoveUnstated">A pointer move was taken since the last check by something that did not say what it
/// changed (a tab that only answers "handled", a region's hover): the next frame is a full one.</param>
/// <param name="PendingForCheckAndMovesAlone">The frame the loop owes was armed by nothing but this check's own earlier
/// answers and pointer moves a handler took (<c>SdlWindowView.RedrawPendingForCheckAndPointerMovesAlone</c>): everything
/// it is owed for was said here.</param>
/// <param name="RaiseDropped">The host's clear after the last full frame dropped a redraw flag that frame's own draw had
/// raised (<see cref="RedrawFlag.ClearAfterFrame"/>): nothing will draw what it asked for unless a settling frame does.</param>
/// <param name="SkyDrift">The atlas's interval for the view it shows (<see cref="GuiTimeCadence.SkyDrift"/>); null when
/// the host cannot say, which is a second.</param>
/// <param name="ProcessCpu">The processor time this process has used so far, on every core
/// (<c>Environment.CpuUsage</c>), from which the scheduler reads what a full frame costs; zero when the host does not
/// say, and then no interval is stretched for cost.</param>
public readonly record struct GuiFrameInputs(
    GuiTab Tab,
    bool RunGoingOn,
    bool PreviewCapturing,
    bool SlewInFlight,
    bool Changed,
    bool RedrawPending,
    bool PendingForPointerMoveAlone,
    RectF32? PointerMoveChanged,
    bool PointerMoveUnstated = false,
    TimeSpan? SkyDrift = null,
    TimeSpan ProcessCpu = default,
    bool PendingForCheckAndMovesAlone = false,
    bool RaiseDropped = false);

/// <summary>
/// WHETHER the GUI owes its window a frame, and WHAT that frame repaints (#2130): the whole of the decision that used to
/// sit in <c>Program.cs</c> between the event loop's hooks, where no test could reach it. The host reads its state into
/// <see cref="GuiFrameInputs"/>, asks <see cref="Check"/> whenever the loop asks its redraw check (right before every frame,
/// and between frames about once a refresh), asks <see cref="BeginFrame"/> as a frame
/// is committed to, and tells <see cref="EndFrame"/> nothing: the scheduler already knows what the frame was.
/// <para>
/// Four causes, each on its own:
/// </para>
/// <list type="bullet">
/// <item><b>the clock</b>, once a second, which repaints the clock's rectangle and nothing else;</item>
/// <item><b>time-driven content</b>, at the interval <see cref="GuiTimeCadence"/> states for what is on show, a full frame;</item>
/// <item><b>a change</b>, flagged by whoever made it, a full frame at once;</item>
/// <item><b>a pointer move whose widget says what it changed</b>, which repaints that.</item>
/// </list>
/// <para>
/// And one that is owed only when something was lost: <b>a settling frame</b>, a second after a full frame whose own draw
/// raised a redraw flag that the host's clear then dropped (<see cref="GuiFrameInputs.RaiseDropped"/>). It draws what that
/// raise asked for, and asks for no other. It used to follow EVERY caused frame, because a plain flag's clear also took
/// what another thread had raised while the frame was drawn, and nothing could say whether that had happened: on an idle
/// atlas that was every second full frame (2026-11-25). A <see cref="RedrawFlag"/> keeps such a raise.
/// </para>
/// <para>
/// Nothing may lean on another's timer. A repaint is narrowed only when every cause since the last frame said what it
/// changed (<see cref="GuiFrameReasons"/>); everything else, a cause the loop raised itself included, is a full frame.
/// </para>
/// <para>
/// <b>Time-driven frames are paced by what they cost.</b> A full frame is a few milliseconds of processor on a GPU and
/// a tenth of a second or more on a software renderer (llvmpipe in a VM, a Raspberry Pi), where a frame a second is
/// the whole of the app's idle load. So the scheduler reads what its full frames cost (the process's own processor
/// time, on every core, over the quarter second after one starts) and stretches a time-driven interval until those
/// frames (and the settling frame each is followed by, where one is) take no more than
/// <see cref="MaxTimeDrivenShare"/> of one core, up to <see cref="SlowestForCost"/>. It asks
/// nothing about which renderer runs: a fast one never reaches the bound. A change is never held back, and neither is
/// the clock. A time-driven frame of a second or slower is also drawn ON the clock's tick, so the two are one frame
/// where they used to be two.
/// </para>
/// </summary>
public sealed class GuiFrameScheduler
{
    private readonly long _ticksPerSecond;
    private readonly GuiFrameReasons _reasons = new GuiFrameReasons();
    private readonly List<RectF32> _narrowed = [];
    private long _lastTimeDrivenFrame;
    private long _lastClockTick;
    // When the one settling frame is due; 0 for none.
    private long _settlingFrameDue;
    // A settling frame was asked for and has not begun; and the last frame begun was one, so what its clear dropped is its
    // own and buys no other.
    private bool _settlingAsked;
    private bool _lastFrameWasSettling;
    // A settling frame fired since the last time-driven frame, and whether one followed the time-driven frame before it:
    // then each interval is two frames of cost.
    private bool _settledSinceTimeDriven;
    private bool _settlingFollowsTimeDriven;
    // Since the last frame: a move said what it changed, and a move was taken that did not.
    private bool _statedMove;
    private bool _unstatedMove;
    // The last check's instant and the process's processor time at it.
    private long _lastCheck;
    private TimeSpan _lastCheckCpu;
    // The full frame whose cost is being read: when it began (0 for none) and the processor time then.
    private long _costFrameAt;
    private TimeSpan _costFrameCpu;
    // What a full frame costs, in seconds of processor; 0 until one was read.
    private double _fullFrameCost;

    /// <summary>The share of one core the time-driven frames may take before their interval is stretched.</summary>
    public const double MaxTimeDrivenShare = 0.05;

    /// <summary>The slowest a time-driven interval is made for cost alone; one already slower is left as it is.</summary>
    public static readonly TimeSpan SlowestForCost = TimeSpan.FromSeconds(10);

    // How long after a full frame begins its cost is read: long enough for a software renderer's worker threads to
    // have finished it, short enough that the next frame has not begun.
    private static readonly TimeSpan CostWindow = TimeSpan.FromMilliseconds(250);

    /// <param name="now">The clock's timestamp at start (<c>ITimeProvider.GetTimestamp</c>).</param>
    /// <param name="ticksPerSecond">Its frequency.</param>
    public GuiFrameScheduler(long now, long ticksPerSecond)
    {
        _ticksPerSecond = ticksPerSecond;
        _lastTimeDrivenFrame = now;
        _lastClockTick = now;
    }

    /// <summary>Whether the frame in flight, or the last one drawn, repainted less than the whole window.</summary>
    public bool FrameIsNarrow { get; private set; }

    /// <summary>
    /// Whether the frame in flight, or the last one drawn, was owed to the clock's tick and nothing else: the one frame
    /// whose whole damage is the clock's rectangle, so a host may paint the clock alone rather than the window
    /// (<c>GuiWindowChrome.TryRepaintClockAlone</c>). A narrowed frame with a pointer move's rectangle in it is not one.
    /// </summary>
    public bool FrameIsClockAlone { get; private set; }

    /// <summary>How often each cause asked, since the host last read them: for a log line, never for a decision.</summary>
    public FrameCauseCounts Causes;

    /// <summary>What a full frame was last read to cost in processor time; zero until one was read.</summary>
    public TimeSpan FullFrameCost => TimeSpan.FromSeconds(_fullFrameCost);

    /// <summary>
    /// The interval a time-driven frame is drawn at: <paramref name="cadence"/>, stretched until the frames it asks for
    /// take no more than <see cref="MaxTimeDrivenShare"/> of a core, and by no more than up to
    /// <see cref="SlowestForCost"/>. Two frames are counted to an interval where the last time-driven frame was followed
    /// by a settling frame, one where it was not.
    /// </summary>
    public TimeSpan PacedForCost(TimeSpan cadence)
    {
        var framesAnInterval = _settlingFollowsTimeDriven ? 2.0 : 1.0;
        var forCost = TimeSpan.FromSeconds(Math.Min(framesAnInterval * _fullFrameCost / MaxTimeDrivenShare, SlowestForCost.TotalSeconds));
        return forCost > cadence ? forCost : cadence;
    }

    /// <summary>
    /// Asked whenever the loop asks the host's redraw check, after the pass's events were dispatched: right before every
    /// frame, and between frames about once a refresh. Whether a frame is owed.
    /// </summary>
    public bool Check(long now, in GuiFrameInputs inputs)
    {
        ReadFrameCost(now, inputs.ProcessCpu);

        var clockTicked = Elapsed(_lastClockTick, now) >= GuiTimeCadence.Second;
        if (clockTicked)
        {
            _lastClockTick = now;
        }

        var cadence = PacedForCost(GuiTimeCadence.Interval(
            inputs.Tab, inputs.RunGoingOn, inputs.PreviewCapturing, inputs.SlewInFlight, inputs.SkyDrift));
        // An interval of a second or more is drawn on the clock's tick, the nearest one to when it falls due: the tick
        // is a frame anyway, and a full frame beside it rather than in it is a frame for nothing.
        var timeDriven = cadence >= GuiTimeCadence.Second
            ? clockTicked && Elapsed(_lastTimeDrivenFrame, now) + GuiTimeCadence.Second / 2 >= cadence
            : Elapsed(_lastTimeDrivenFrame, now) >= cadence;

        // What the moves changed is kept until a frame draws it, armed or not.
        if (inputs.PointerMoveChanged is { } moved)
        {
            _reasons.Narrow(moved);
            _statedMove = true;
        }
        if (inputs.PointerMoveUnstated)
        {
            _reasons.Full();
            _unstatedMove = true;
        }

        // The frame the loop owes, when it owes one, is this check's OWN when nothing armed it but the check's earlier
        // answers and pointer moves a handler took: everything such a frame is owed for was said here, and is in the
        // reasons, so it may be narrowed to them. A frame is often held for pacing past the next check, and a frame the
        // check had armed then read as a cause of the loop's, by nothing but the check's own yes: under a moving pointer
        // nearly every clock tick was drawn in full for it. Any other arming is a cause this check cannot name, and so is
        // a frame owed to a move that nothing here has an account of.
        var movesAlone = inputs is { RedrawPending: true, PendingForPointerMoveAlone: true };
        var ownFrame = inputs.RedrawPending && (movesAlone || inputs.PendingForCheckAndMovesAlone)
            && (!movesAlone || _statedMove || _unstatedMove);
        var loopsOwn = inputs.RedrawPending && !ownFrame;

        // A stated change no frame is armed for (the tab's settle with no move behind it, the pointer come to rest; a
        // move a loop before SdlVulkan.Renderer 7.109 dropped): asked for at once, so the follower never rests where the
        // pointer was. It used to wait a thirtieth of a second, as a rate limit beside the loop's own for moves, and the
        // two clocks beat against each other (the atlas's hover, 2026-11-25).
        var statedUnarmed = _statedMove && !inputs.RedrawPending;

        var caused = timeDriven || inputs.Changed || loopsOwn;

        // The settling frame: armed by a dropped raise and by nothing else, never by the settling frame's own.
        if (inputs.RaiseDropped && !_lastFrameWasSettling)
        {
            _settlingFrameDue = now + _ticksPerSecond;
        }
        if (caused)
        {
            // A full frame is on its way for a cause of its own: it draws what the dropped raise asked for.
            _settlingFrameDue = 0;
            _settlingAsked = false;
        }
        var settling = !caused && _settlingFrameDue != 0 && now >= _settlingFrameDue;
        if (settling)
        {
            _settlingFrameDue = 0;
            _settlingAsked = true;
            _settledSinceTimeDriven = true;
        }

        if (caused || settling)
        {
            _reasons.Full();
        }
        if (timeDriven || inputs.Changed)
        {
            // A full frame draws everything time-driven, whatever asked for it.
            _lastTimeDrivenFrame = now;
        }
        if (timeDriven)
        {
            _settlingFollowsTimeDriven = _settledSinceTimeDriven;
            _settledSinceTimeDriven = false;
        }
        if (clockTicked)
        {
            _reasons.ClockTick();
        }

        var narrowedMove = (ownFrame || statedUnarmed) && _statedMove && !_unstatedMove;
        if (timeDriven) { Causes.Time++; }
        if (inputs.Changed) { Causes.Changed++; }
        if (loopsOwn) { Causes.Loop++; }
        if (narrowedMove) { Causes.PointerMove++; }
        if (settling) { Causes.Settling++; }
        if (clockTicked) { Causes.Clock++; }

        return caused || settling || ownFrame || statedUnarmed || clockTicked;
    }

    /// <summary>
    /// How long the window can be left alone after a <see cref="Check"/> that found nothing owed: the time until the
    /// next thing this scheduler could owe a frame FOR by the clock alone (the clock's next tick, a time-driven frame
    /// faster than a second, a settling frame, a full frame's cost falling due to be read). What changes by anything
    /// but the clock (input, a redraw flag) reaches the host another way and is not in it.
    /// </summary>
    /// <remarks>
    /// The host tells its loop, which then does not ask again until that time, an event or a wake. The check used to be
    /// asked every 16 ms whatever was due: sixty wakes a second of a window nothing was happening to, each with the
    /// loop's per-pass work behind it, which took more of an idle window's render thread than its frames did (2026-11-25).
    /// </remarks>
    public TimeSpan NextTimedNeed(long now, in GuiFrameInputs inputs)
    {
        var next = _lastClockTick + _ticksPerSecond;

        // A time-driven frame of a second or slower is drawn on the clock's tick, so the tick covers it.
        var cadence = PacedForCost(GuiTimeCadence.Interval(
            inputs.Tab, inputs.RunGoingOn, inputs.PreviewCapturing, inputs.SlewInFlight, inputs.SkyDrift));
        if (cadence < GuiTimeCadence.Second)
        {
            next = Math.Min(next, _lastTimeDrivenFrame + (long)(cadence.TotalSeconds * _ticksPerSecond));
        }
        if (_settlingFrameDue != 0)
        {
            next = Math.Min(next, _settlingFrameDue);
        }
        if (_costFrameAt != 0)
        {
            next = Math.Min(next, _costFrameAt + (long)(CostWindow.TotalSeconds * _ticksPerSecond));
        }

        return TimeSpan.FromSeconds(Math.Max(0L, next - now) / (double)_ticksPerSecond);
    }

    /// <summary>
    /// Asked as a frame is committed to: what it repaints. An empty answer is the whole window; otherwise the rectangles
    /// to declare as the frame's damage.
    /// </summary>
    /// <param name="flagged">Whether a redraw flag is set NOW. Peeked, never consumed: a writer may have set one since
    /// the check, and a narrowed frame takes no part in the flags' life, so one that is set makes this frame a full one.</param>
    /// <param name="shuttingDown">The window is closing: its frames are full.</param>
    /// <param name="tab">The tab on show.</param>
    /// <param name="clockRect">Where the last frame painted the clock, or null when it painted none.</param>
    public IReadOnlyList<RectF32> BeginFrame(bool flagged, bool shuttingDown, GuiTab tab, RectF32? clockRect)
    {
        _narrowed.Clear();
        _statedMove = false;
        _unstatedMove = false;
        _lastFrameWasSettling = _settlingAsked;
        _settlingAsked = false;
        var narrow = _reasons.TryTakeNarrow(_narrowed, out var clock)
            && !shuttingDown && GuiTimeCadence.MayNarrow(tab);
        // Before the clock's own rectangle joins them: any rectangle here is a move's.
        var movesStated = _narrowed.Count > 0;
        FrameIsClockAlone = false;
        if (narrow && flagged)
        {
            // Every cause said what it changed, and a flag was set since the check: the frame is full after all.
            Causes.Flagged++;
            narrow = false;
        }
        if (narrow && clock)
        {
            if (clockRect is { Width: > 0f, Height: > 0f } rect)
            {
                _narrowed.Add(rect);
            }
            else
            {
                // The clock ticked and nobody knows where it is drawn.
                narrow = false;
            }
        }

        if (!narrow || _narrowed.Count == 0)
        {
            _narrowed.Clear();
            FrameIsNarrow = false;
            // This frame's cost is read from the check that asked for it; from nothing, with a host that does not say
            // what the process has used, and then no reading is waited for either (NextTimedNeed).
            _costFrameAt = _lastCheckCpu > TimeSpan.Zero ? _lastCheck : 0;
            _costFrameCpu = _lastCheckCpu;
            return _narrowed;
        }

        FrameIsNarrow = true;
        FrameIsClockAlone = clock && !movesStated;
        // A narrowed frame inside a full one's window would be counted into it: that reading is dropped.
        _costFrameAt = 0;
        return _narrowed;
    }

    // Reads what the last full frame cost once its window has run out with no other frame in it. A reading below the
    // running figure replaces it and one above moves it a quarter of the way: work on another thread (a catalogue
    // loading as the window opens) lands in a window too, and may slow the pace only if it keeps happening.
    private void ReadFrameCost(long now, TimeSpan processCpu)
    {
        if (_costFrameAt != 0 && processCpu > TimeSpan.Zero && Elapsed(_costFrameAt, now) >= CostWindow)
        {
            var cost = Math.Max(0.0, (processCpu - _costFrameCpu).TotalSeconds);
            _fullFrameCost = _fullFrameCost == 0.0 || cost < _fullFrameCost
                ? cost
                : _fullFrameCost + (cost - _fullFrameCost) / 4.0;
            _costFrameAt = 0;
        }

        _lastCheck = now;
        _lastCheckCpu = processCpu;
    }

    private TimeSpan Elapsed(long from, long to) => TimeSpan.FromSeconds((to - from) / (double)_ticksPerSecond);
}

/// <summary>
/// A tab that can say what a pointer move it took changed on screen: a chart whose crosshair follows the pointer. The frame
/// the move asks for then repaints that rectangle alone, where a tab that only answers "handled" gets the whole window.
/// </summary>
public interface IPointerMoveChange
{
    /// <summary>
    /// The extent, in the surface's pixels, of everything the moves taken since the last call changed, or null when the
    /// tab cannot say (it must then be repainted in full). The WHOLE extent: what the pointer left as well as what it
    /// reached, since the frame keeps every pixel outside it.
    /// </summary>
    RectF32? TakePointerMoveChanged();

    /// <summary>
    /// Brings what the tab shows for the pointer up to date with where the pointer is NOW, before a frame is decided:
    /// asked by the host from its redraw check, ahead of <see cref="TakePointerMoveChanged"/>. For a tab that does not
    /// answer every move as it arrives (the atlas resolves the object under the pointer at a bounded rate) this is where
    /// the moves it passed over are caught up, so a frame shows the pointer as of that frame and the last move of a
    /// gesture is never left unanswered. Nothing to do for a tab that answers each move in full.
    /// </summary>
    void SettlePointer()
    {
    }
}

/// <summary>How often each cause of a GUI frame asked.</summary>
public struct FrameCauseCounts
{
    /// <summary>The tab's time-driven interval ran out.</summary>
    public int Time;

    /// <summary>A redraw flag or one of the host's standing reasons.</summary>
    public int Changed;

    /// <summary>The loop owed a frame for a cause of its own.</summary>
    public int Loop;

    /// <summary>A pointer move whose widget said what it changed.</summary>
    public int PointerMove;

    /// <summary>The one frame a second after a full frame whose own draw raised a redraw flag that the clear dropped.</summary>
    public int Settling;

    /// <summary>The clock's second.</summary>
    public int Clock;

    /// <summary>Frames that could have been narrowed and were drawn in full for a redraw flag set since the check.</summary>
    public int Flagged;
}
