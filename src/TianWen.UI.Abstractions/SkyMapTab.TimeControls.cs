using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The time controls in the atlas's info strip: step buttons for what the arrow keys do, the time itself
/// opening a drop-up of the night's landmarks, and Now while the view is scrubbed off live.
/// </summary>
/// <remarks>
/// The keys (Up/Down an hour, Shift for ten minutes, Left/Right a day, N midnight, 0 back to live) were the
/// only way to move the atlas in time, and the strip showed the time with nothing to press
/// (reported 2026-09-28). A touch screen has no arrow keys, so on a phone the atlas had no time control at
/// all. The buttons and the keys go through <see cref="StepTime"/>, one path, so they cannot drift apart.
/// </remarks>
public partial class SkyMapTab<TSurface>
{
    private const string TimeMenuAction = "SkyTimeMenu";

    /// <summary>The strip's steps, in the order they are drawn around the time: back, then forward.</summary>
    private static readonly ImmutableArray<(string Label, TimeSpan Delta)> BackSteps =
    [
        ("-1d", TimeSpan.FromDays(-1)),
        ("-1h", TimeSpan.FromHours(-1)),
        ("-10m", TimeSpan.FromMinutes(-10)),
    ];

    private static readonly ImmutableArray<(string Label, TimeSpan Delta)> ForwardSteps =
    [
        ("+10m", TimeSpan.FromMinutes(10)),
        ("+1h", TimeSpan.FromHours(1)),
        ("+1d", TimeSpan.FromDays(1)),
    ];

    /// <summary>The night's landmarks, opened by a press on the strip's time; its value is the scrub offset.</summary>
    internal DropdownMenuState<TimeSpan> TimeMenu { get; } = new();

    /// <summary>The orrery's play speeds, opened by a press on the strip's speed; its value is days a second.</summary>
    internal DropdownMenuState<double> SpeedMenu { get; } = new();

    internal const string OrrerySpeedAction = "OrrerySpeed";

    /// <summary>Moves the atlas's viewing time by <paramref name="delta"/>: the arrow keys and the strip's buttons.</summary>
    internal void StepTime(TimeSpan delta)
    {
        State.TimeOffset += delta;
        State.NeedsRedraw = true;
    }

    /// <summary>Back to the live clock (or the planner's date), keeping that date: the 0 key and Now.</summary>
    internal void ResetTimeOffset()
    {
        State.TimeOffset = TimeSpan.Zero;
        State.NeedsRedraw = true;
    }

    /// <summary>
    /// The scrub offset that puts the viewing time at <paramref name="instant"/>, from the base the frame
    /// adds it to: the planner's date when one is set, else now.
    /// </summary>
    private TimeSpan OffsetTo(DateTimeOffset instant, PlannerState plannerState, ITimeProvider timeProvider)
        => instant - (plannerState.PlanningDate?.ToUniversalTime() ?? timeProvider.GetUtcNow());

    /// <summary>
    /// Opens the landmarks above <paramref name="anchor"/> (the time's arranged rect, device pixels): dusk
    /// and dawn of the planned night, its midnight, and now. A landmark the planner has not computed yet
    /// (no site, or no night) is listed disabled, so the menu's shape does not change under the pointer.
    /// </summary>
    private void OpenTimeMenu(RectF32 anchor, float fontSize)
    {
        if (_plannerState is not { } plannerState || _timeProvider is not { } timeProvider)
        {
            return;
        }

        var tz = plannerState.SiteTimeZone;
        var baseUtc = plannerState.PlanningDate?.ToUniversalTime() ?? timeProvider.GetUtcNow();
        var hasNight = plannerState.AstroDark != default && plannerState.AstroTwilight != default;
        var items = ImmutableArray.Create(
            hasNight
                ? new DropdownItem<TimeSpan>($"Dusk  {plannerState.AstroDark.ToOffset(tz):HH:mm}", OffsetTo(plannerState.AstroDark, plannerState, timeProvider))
                : DropdownItem<TimeSpan>.Disabled("Dusk", TimeSpan.Zero, "The night is not computed yet"),
            new DropdownItem<TimeSpan>("Midnight", SkyMapState.ComputeMidnightOffset(baseUtc.ToOffset(tz))),
            hasNight
                ? new DropdownItem<TimeSpan>($"Dawn  {plannerState.AstroTwilight.ToOffset(tz):HH:mm}", OffsetTo(plannerState.AstroTwilight, plannerState, timeProvider))
                : DropdownItem<TimeSpan>.Disabled("Dawn", TimeSpan.Zero, "The night is not computed yet"),
            new DropdownItem<TimeSpan>("Now", TimeSpan.Zero));

        // Wide enough for "Dusk  19:32" at this size, and never narrower than the time it opened from.
        var width = MathF.Max(anchor.Width, fontSize * 8f);
        TimeMenu.Open(anchor.X + anchor.Width - width, anchor.Y, width, items, item =>
        {
            State.TimeOffset = item.Value;
            State.NeedsRedraw = true;
        });
    }

    /// <summary>
    /// The widest time the strip shows in ordinary use: a scrubbed date and an offset of its two largest units, each of two digits
    /// in the widest unit letter (m), so hours and minutes, days and hours, weeks and days and years and weeks all fit (years past
    /// 99 aside, which only the orrery's Play reaches, and trim at the offset's end).
    /// </summary>
    internal const string TimeTextWidthSample = "0000-00-00 00:00  (+00m 00m)";

    /// <summary>
    /// The strip's right-hand controls: Now while the view is off live, the back steps, the time (a press opens the landmarks),
    /// and the forward steps. Device-pixel sizes; the caller arranges them, in a group of their own (<see cref="TimeControlsGroup"/>)
    /// or as direct children of its strip, which a label that gives way under pressure needs (the time is a content-capped Star:
    /// it reads in full while the strip has room and trims once the buttons have taken it, #1584).
    /// </summary>
    /// <remarks>The fill and ink are the sky strip's own unless the caller states its own: the orrery's come from the palette, so
    /// Night spends no blue there.</remarks>
    private ImmutableArray<Layout.Node> BuildTimeControls(string timeText, RGBAColor32 timeColor, float fontSize, float dpiScale,
        RGBAColor32? fillColour = null, RGBAColor32? inkColour = null)
    {
        var fill = fillColour ?? InfoPanelBg;
        var ink = inkColour ?? InfoText;
        var padX = 6f * dpiScale;

        Layout.Node Step((string Label, TimeSpan Delta) step)
            => Layout.Builder.Text(step.Label, fontSize, ink, TextAlign.Center, TextAlign.Center)
                .PadX(padX)
                .HStar()
                .Bg(fill).BgHover(GuiTheme.Hover(fill))
                .Clickable(new HitResult.ButtonHit("SkyTimeStep:" + step.Label), _ => StepTime(step.Delta));

        var children = ImmutableArray.CreateBuilder<Layout.Node>(BackSteps.Length + ForwardSteps.Length + 2);
        // Now on the LEFT, while the view is off live: the group is right-aligned, so a button appearing at its far end moved
        // every step the next press was aimed at.
        if (State.TimeOffset != TimeSpan.Zero)
        {
            children.Add(Layout.Builder.Text("Now", fontSize, ink, TextAlign.Center, TextAlign.Center)
                .PadX(padX)
                .HStar()
                .Bg(fill).BgHover(GuiTheme.Hover(fill))
                .Clickable(new HitResult.ButtonHit("SkyTimeNow"), _ => ResetTimeOffset()));
        }

        foreach (var step in BackSteps)
        {
            children.Add(Step(step));
        }

        // Sized for a scrubbed date and an offset of its two largest units, so the live clock turning into a date and an offset
        // does not move the back steps either; only an offset past a year of weeks is wider.
        // A content-capped Star twice over: the button sits at its content while its strip has room and gives way under pressure,
        // and inside it the time takes what the mark leaves, trimming its end (the offset) before the date.
        children.Add(Layout.Builder.HStack(
                Layout.Builder.Text(timeText, fontSize, timeColor, TextAlign.Center, TextAlign.Center, TextTrim.End, widthSample: TimeTextWidthSample)
                    .WStarUpToContent(),
                FormRowLayout.StepMark("▲", fontSize, timeColor))
            .WithGap(4f * dpiScale)
            .CrossCenter()
            .PadX(padX)
            .WStarUpToContent(TimeLabelWeight, min: TimeLabelMinEm * fontSize)
            .HStar()
            .Bg(fill).BgHover(GuiTheme.Hover(fill))
            .Clickable(new HitResult.ButtonHit(TimeMenuAction), _ => OpenTimeMenu(_timeButtonRect, fontSize)));

        foreach (var step in ForwardSteps)
        {
            children.Add(Step(step));
        }

        return children.ToImmutable();
    }

    /// <summary>The narrowest the time button gives way to under pressure, in ems of its font: the date's year and month.</summary>
    internal const float TimeLabelMinEm = 4f;

    /// <summary>
    /// The time button's weight against a strip's other content-capped labels (each 1): under pressure it gives way a quarter as
    /// fast, so at 1.5x on a 900 px orrery strip it keeps its whole date and time with about three and a half ems to spare and only
    /// the offset trims (at weight 1 it trimmed into the date; at 2 it kept two ems on Windows and lost a digit on Linux, whose
    /// glyphs measure a few pixels wider, #1584). It was 3 until the orrery's speed button came in beside Play (#1573), a fourth
    /// label sharing the squeeze: at 3 the time kept only 26 px (1.7 ems) past its date and time; at 4, 53 px (3.5 ems), the
    /// labels 54 px each.
    /// </summary>
    internal const float TimeLabelWeight = 4f;

    /// <summary>The time controls (<see cref="BuildTimeControls"/>) as one group, for a strip that arranges them as a unit.</summary>
    private static Layout.Node TimeControlsGroup(ImmutableArray<Layout.Node> controls, float dpiScale)
        // The strip's full height, so each button's text centres in the strip as the info text beside it
        // does, and its hover fill is the strip's height. Auto, the group measured to one line of text and
        // sat at the strip's top (a stack places a child at the cross-axis START).
        => Layout.Builder.HStack(controls.AsSpan()).WithGap(2f * dpiScale).HStar();

    // Where the strip last laid the time (and the orrery's speed) out, device pixels: the drop-up opens above it. Remembered as
    // the strip paints, every frame, because a press handler is not handed its node's arranged rect.
    private RectF32 _timeButtonRect;
    private RectF32 _speedButtonRect;

    /// <summary>Remembers where the strip laid the time and the orrery's speed out, for the drop-up a press on either opens.</summary>
    private void RememberTimeButton(ImmutableArray<Layout.ArrangedNode<float>> arranged)
    {
        foreach (var node in arranged)
        {
            switch (node.Node.Hit)
            {
                case HitResult.ButtonHit { Action: TimeMenuAction }:
                    _timeButtonRect = new RectF32(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height);
                    break;
                case HitResult.ButtonHit { Action: OrrerySpeedAction }:
                    _speedButtonRect = new RectF32(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height);
                    break;
            }
        }
    }

    /// <summary>Paints the landmarks drop-up when it is open (<see cref="RenderDropUp{T}"/>).</summary>
    private void RenderTimeMenu(RectF32 viewport, float fontSize, RGBAColor32? fillColour = null, RGBAColor32? inkColour = null)
        => RenderDropUp(TimeMenu, viewport, fontSize, fillColour, inkColour);

    /// <summary>
    /// Paints a strip's drop-up when it is open (the landmarks, the orrery's speeds), LAST of the pass so it lies over the strip and
    /// the map; its backdrop dismisses, and Escape comes from being painted.
    /// </summary>
    private void RenderDropUp<T>(DropdownMenuState<T> menu, RectF32 viewport, float fontSize, RGBAColor32? fillColour = null, RGBAColor32? inkColour = null)
    {
        var fill = fillColour ?? InfoPanelBg;
        if (!menu.IsOpen)
        {
            return;
        }

        var anchor = new RectF32(menu.AnchorX, menu.AnchorY, menu.AnchorWidth, 0f);
        RenderLayout(
            Layout.Builder.Dropdown(anchor, menu,
                fontSize: fontSize,
                textColor: inkColour ?? InfoText,
                background: fill with { Alpha = 0xFF },
                highlight: GuiTheme.Hover(fill),
                maxHeight: MathF.Max(fontSize, menu.AnchorY - viewport.Y),
                side: Layout.DockSide.Top),
            viewport,
            // Device pixels, as the strip is: the default context would scale the tree as design units.
            scale: DesignScale.One);
    }
}
