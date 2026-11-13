using System;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Why the GUI's next frame is owed, kept between the redraw check that asks for it and the frame that draws it, so the
/// frame can say what it must repaint (#2130).
/// <para>
/// The GUI used to repaint its whole window once a second so the top bar's clock ticks, on every tab. On a software
/// renderer that one frame a second was the app's whole idle cost (llvmpipe: about 100 ms of CPU a frame at 5 megapixels;
/// a Raspberry Pi 3B sat at 15 to 20 % of its four cores with no profile loaded). The same tick was also, unsaid, what made
/// every other time-driven thing on screen move, and what repainted a change the redraw flags had lost. Those are three
/// separate things now: the clock (<see cref="ClockTick"/>, which repaints the clock alone), the time-driven content of the
/// tab on show (<see cref="GuiTimeCadence"/>, a full frame at the tab's own interval), and a change (a redraw flag, a full
/// frame at once).
/// </para>
/// <para>
/// <b>Safe by construction, as the viewer's damage is</b> (<c>ImageRendererBase.Damage.cs</c>): a frame is repainted in
/// FULL unless the clock is positively its only cause. Every other cause, an unknown one included, calls
/// <see cref="Full"/>; narrowing wrongly is the one failure that shows (stale pixels), and failing to narrow only costs
/// what it always cost.
/// </para>
/// <para>
/// <b>A clock frame PEEKS at the redraw flags and consumes none.</b> The flags keep the life they always had: set by
/// whoever changed something, cleared by the host after the full frame that drew it (which is why a tab may set its own
/// flag while it paints without asking for a frame per frame, and why <c>SkyMapState.NeedsContinuousFrames</c> exists).
/// Clearing them BEFORE the frame instead was tried and made the atlas draw at the display's rate while idle.
/// </para>
/// </summary>
public sealed class GuiFrameReasons
{
    // A window's first frame has no previous frame to keep.
    private bool _full = true;
    private bool _clock;

    /// <summary>Something other than the clock asked for the pending frame, or nobody can say what did.</summary>
    public void Full() => _full = true;

    /// <summary>The clock's second turned over.</summary>
    public void ClockTick() => _clock = true;

    /// <summary>
    /// Takes the verdict for the frame about to be drawn and starts over for the next: true when the clock's tick is the
    /// frame's only cause, so its rectangle alone may be repainted.
    /// </summary>
    public bool TakeIsClockOnly()
    {
        var clockOnly = _clock && !_full;
        _full = false;
        _clock = false;
        return clockOnly;
    }
}

/// <summary>
/// How often the GUI needs a frame for no reason but the passage of time, by what is on show (#2130). A change (input, a
/// poll that read a new value, a notification) asks for its own frame through a redraw flag and is not this; the clock is
/// not this either (<see cref="GuiFrameReasons.ClockTick"/>). What is left is content that is a function of "now":
/// <list type="bullet">
/// <item>a run's progress bars and countdowns, and a preview capture's: half a second;</item>
/// <item>the sky as it drifts, and the ETA of a slew in flight: a second;</item>
/// <item>the Live Session's timeline marker: half a minute;</item>
/// <item>everything read to the minute (the top bar's night label and verdict, the planner's "now" line, the session's
/// remaining windows, the Home board's "last seen", a quiet rig's "not answering" age): a minute.</item>
/// </list>
/// A new piece of time-driven content states its interval HERE, never by leaning on the clock's tick.
/// </summary>
public static class GuiTimeCadence
{
    /// <summary>A run's progress, and a preview capture's.</summary>
    public static readonly TimeSpan Progress = TimeSpan.FromMilliseconds(500);

    /// <summary>The live sky, and a slew's ETA.</summary>
    public static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    /// <summary>The Live Session's timeline marker.</summary>
    public static readonly TimeSpan Timeline = TimeSpan.FromSeconds(30);

    /// <summary>Everything read to the minute.</summary>
    public static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>The interval at which the window needs a full frame for time's passing alone.</summary>
    /// <param name="tab">The tab on show.</param>
    /// <param name="runGoingOn">A session runs, or a polar alignment routine is past idle, in the local or the viewed context:
    /// its progress surfaces at half a second whatever tab is on show, as it always did.</param>
    /// <param name="previewCapturing">A preview exposure is in flight on the viewed context.</param>
    /// <param name="slewInFlight">The mount is on its way to a target whose ETA the top bar counts down.</param>
    public static TimeSpan Interval(GuiTab tab, bool runGoingOn, bool previewCapturing, bool slewInFlight)
    {
        if (runGoingOn)
        {
            return Progress;
        }

        var tabs = tab switch
        {
            GuiTab.SkyMap => Second,
            GuiTab.LiveSession => previewCapturing ? Progress : Timeline,
            _ => Minute,
        };
        return slewInFlight && tabs > Second ? Second : tabs;
    }

    /// <summary>
    /// Whether a frame owed to the clock alone may repaint the clock's rectangle alone on this tab. Not where the sky map's
    /// own pipeline draws (the Sky Map; the Live Session and the Guider, whose viewer can lay the sky behind a frame): it
    /// sets its own scissor and viewport, so it would draw outside a narrowed render area. Those tabs keep a full frame for
    /// the tick until the pipeline is bounded by the frame's region.
    /// </summary>
    public static bool ClockMayRepaintAlone(GuiTab tab)
        => tab is GuiTab.Home or GuiTab.Equipment or GuiTab.Planner or GuiTab.Session or GuiTab.Notifications;
}
