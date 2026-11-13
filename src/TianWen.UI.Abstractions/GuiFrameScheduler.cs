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
/// <param name="PointerMoveChanged">What those moves changed, when the widget that took them can say; null when it
/// cannot, or when something else took a move too.</param>
public readonly record struct GuiFrameInputs(
    GuiTab Tab,
    bool RunGoingOn,
    bool PreviewCapturing,
    bool SlewInFlight,
    bool Changed,
    bool RedrawPending,
    bool PendingForPointerMoveAlone,
    RectF32? PointerMoveChanged);

/// <summary>
/// WHETHER the GUI owes its window a frame, and WHAT that frame repaints (#2130): the whole of the decision that used to
/// sit in <c>Program.cs</c> between the event loop's hooks, where no test could reach it. The host reads its state into
/// <see cref="GuiFrameInputs"/>, asks <see cref="Check"/> on every loop iteration, asks <see cref="BeginFrame"/> as a frame
/// is committed to, and tells <see cref="EndFrame"/> nothing: the scheduler already knows what the frame was.
/// <para>
/// Four causes, each on its own:
/// </para>
/// <list type="bullet">
/// <item><b>the clock</b>, once a second, which repaints the clock's rectangle and nothing else;</item>
/// <item><b>time-driven content</b>, at the interval <see cref="GuiTimeCadence"/> states for what is on show, a full frame;</item>
/// <item><b>a change</b>, flagged by whoever made it, a full frame at once, followed by ONE settling frame a second later
/// (the flags are cleared after the frame that drew them, so a change flagged while that frame was being drawn is cleared
/// unseen; the settling frame draws it, and asks for no other);</item>
/// <item><b>a pointer move whose widget says what it changed</b>, which repaints that.</item>
/// </list>
/// <para>
/// Nothing may lean on another's timer. A repaint is narrowed only when every cause since the last frame said what it
/// changed (<see cref="GuiFrameReasons"/>); everything else, a cause the loop raised itself included, is a full frame.
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

    /// <summary>How often each cause asked, since the host last read them: for a log line, never for a decision.</summary>
    public FrameCauseCounts Causes;

    /// <summary>Asked on every loop iteration, after the iteration's events were dispatched: whether a frame is owed.</summary>
    public bool Check(long now, in GuiFrameInputs inputs)
    {
        var cadence = GuiTimeCadence.Interval(inputs.Tab, inputs.RunGoingOn, inputs.PreviewCapturing, inputs.SlewInFlight);
        var timeDriven = Elapsed(_lastTimeDrivenFrame, now) >= cadence;

        var clockTicked = Elapsed(_lastClockTick, now) >= GuiTimeCadence.Second;
        if (clockTicked)
        {
            _lastClockTick = now;
        }

        // A frame the loop owes for pointer moves alone, whose widget said what they changed, is the one loop-raised cause
        // that can be narrowed. Every other one is a cause this check cannot name.
        var narrowedMove = inputs is { RedrawPending: true, PendingForPointerMoveAlone: true, PointerMoveChanged: not null };
        var loopsOwn = inputs.RedrawPending && !narrowedMove;

        var caused = timeDriven || inputs.Changed || loopsOwn;
        var settling = !caused && _settlingFrameDue != 0 && now >= _settlingFrameDue;
        if (caused)
        {
            // Armed by a frame with a cause, and by nothing else: the settling frame does not ask for another.
            _settlingFrameDue = now + _ticksPerSecond;
        }
        else if (settling)
        {
            _settlingFrameDue = 0;
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
        if (narrowedMove && inputs.PointerMoveChanged is { } moved)
        {
            _reasons.Narrow(moved);
        }
        if (clockTicked)
        {
            _reasons.ClockTick();
        }

        if (timeDriven) { Causes.Time++; }
        if (inputs.Changed) { Causes.Changed++; }
        if (loopsOwn) { Causes.Loop++; }
        if (narrowedMove) { Causes.PointerMove++; }
        if (settling) { Causes.Settling++; }
        if (clockTicked) { Causes.Clock++; }

        return caused || settling || narrowedMove || clockTicked;
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
        var narrow = _reasons.TryTakeNarrow(_narrowed, out var clock)
            && !flagged && !shuttingDown && GuiTimeCadence.MayNarrow(tab);
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
            return _narrowed;
        }

        FrameIsNarrow = true;
        return _narrowed;
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

    /// <summary>The one frame after a caused full frame.</summary>
    public int Settling;

    /// <summary>The clock's second.</summary>
    public int Clock;
}
