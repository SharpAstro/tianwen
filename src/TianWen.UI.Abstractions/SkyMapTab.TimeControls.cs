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
    /// The strip's right-hand controls: the back steps, the time (a press opens the landmarks), the forward
    /// steps, and Now while the view is off live. Device-pixel sizes; the caller arranges them.
    /// </summary>
    private Layout.Node BuildTimeControls(string timeText, RGBAColor32 timeColor, float fontSize, float dpiScale)
    {
        var fill = InfoPanelBg;
        var padX = 6f * dpiScale;

        Layout.Node Step((string Label, TimeSpan Delta) step)
            => Layout.Builder.Text(step.Label, fontSize, InfoText, TextAlign.Center, TextAlign.Center)
                .PadX(padX)
                .HStar()
                .Bg(fill).BgHover(GuiTheme.Hover(fill))
                .Clickable(new HitResult.ButtonHit("SkyTimeStep:" + step.Label), _ => StepTime(step.Delta));

        var children = ImmutableArray.CreateBuilder<Layout.Node>(BackSteps.Length + ForwardSteps.Length + 2);
        foreach (var step in BackSteps)
        {
            children.Add(Step(step));
        }

        children.Add(Layout.Builder.HStack(
                Layout.Builder.Text(timeText, fontSize, timeColor, TextAlign.Center, TextAlign.Center),
                FormRowLayout.StepMark("▲", fontSize, timeColor))
            .WithGap(4f * dpiScale)
            .CrossCenter()
            .PadX(padX)
            .HStar()
            .Bg(fill).BgHover(GuiTheme.Hover(fill))
            .Clickable(new HitResult.ButtonHit(TimeMenuAction), _ => OpenTimeMenu(_timeButtonRect, fontSize)));

        foreach (var step in ForwardSteps)
        {
            children.Add(Step(step));
        }

        if (State.TimeOffset != TimeSpan.Zero)
        {
            children.Add(Layout.Builder.Text("Now", fontSize, InfoText, TextAlign.Center, TextAlign.Center)
                .PadX(padX)
                .HStar()
                .Bg(fill).BgHover(GuiTheme.Hover(fill))
                .Clickable(new HitResult.ButtonHit("SkyTimeNow"), _ => ResetTimeOffset()));
        }

        // The strip's full height, so each button's text centres in the strip as the info text beside it
        // does, and its hover fill is the strip's height. Auto, the group measured to one line of text and
        // sat at the strip's top (a stack places a child at the cross-axis START).
        return Layout.Builder.HStack(children.ToImmutable().AsSpan()).WithGap(2f * dpiScale).HStar();
    }

    // Where the strip last laid the time out, device pixels: the drop-up opens above it. Remembered as
    // the strip paints, every frame, because a press handler is not handed its node's arranged rect.
    private RectF32 _timeButtonRect;

    /// <summary>Remembers where the strip laid the time out, for the drop-up a press on it opens.</summary>
    private void RememberTimeButton(ImmutableArray<Layout.ArrangedNode<float>> arranged)
    {
        foreach (var node in arranged)
        {
            if (node.Node.Hit is HitResult.ButtonHit { Action: TimeMenuAction })
            {
                _timeButtonRect = new RectF32(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height);
                return;
            }
        }
    }

    /// <summary>
    /// Paints the landmarks drop-up when it is open, LAST of the sky pass so it lies over the strip and the
    /// map; its backdrop dismisses, and Escape comes from being painted.
    /// </summary>
    private void RenderTimeMenu(RectF32 viewport, float fontSize)
    {
        if (!TimeMenu.IsOpen)
        {
            return;
        }

        var anchor = new RectF32(TimeMenu.AnchorX, TimeMenu.AnchorY, TimeMenu.AnchorWidth, 0f);
        RenderLayout(
            Layout.Builder.Dropdown(anchor, TimeMenu,
                fontSize: fontSize,
                textColor: InfoText,
                background: InfoPanelBg with { Alpha = 0xFF },
                highlight: GuiTheme.Hover(InfoPanelBg),
                maxHeight: MathF.Max(fontSize, TimeMenu.AnchorY - viewport.Y),
                side: Layout.DockSide.Top),
            viewport,
            // Device pixels, as the strip is: the default context would scale the tree as design units.
            scale: DesignScale.One);
    }
}
