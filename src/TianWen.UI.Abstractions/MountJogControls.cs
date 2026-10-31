using System;
using System.Collections.Generic;
using DIR.Lib;
using TianWen.DAL;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The mount moved while a button is held (the owner, 2026-10-20), ONE pair of rows for every panel that offers it: the
/// speed, in multiples of sidereal (<see cref="MountJog.SiderealMultiples"/>), as a stepper, and N/S/E/W, whose press starts
/// the move and whose release, wherever the pointer is let go, stops it. The planetary panel and the live Preview both show
/// them. Nothing here may ellipsize: every label and the speed are measured from a width sample, so a narrow panel squeezes
/// the direction buttons, never the words (seven speed buttons in a row collapsed to "..." in the Preview's mount block).
/// </summary>
internal static class MountJogControls
{
    /// <summary>The colours and metrics of the panel the rows sit in; <see cref="LabelWidthSample"/> lines its labels up with the panel's own.</summary>
    internal readonly record struct Look(float FontSize, float RowHeight, float Gap, RGBAColor32 ButtonBg,
        RGBAColor32 LabelText, RGBAColor32 ValueText, RGBAColor32 GlyphText, string LabelWidthSample = "Speed");

    /// <summary>The widest speed the stepper shows, so it is measured once and never cut.</summary>
    private const string SpeedWidthSample = "128x";

    /// <summary>A tracking choice in words: Stopped, or the rate's name.</summary>
    public static string TrackingLabel(TrackingSpeed speed) => speed is TrackingSpeed.None ? "Stopped" : speed.ToString();

    /// <summary>
    /// What the Track row shows: Stopped, or the rate the mount itself reports (<see cref="Lib.Sequencing.MountState.TrackingRate"/>),
    /// whoever set it, this app, another client or the handset. Null, shown as plain "Tracking", until a rate has been read.
    /// </summary>
    public static TrackingSpeed? ShownTrackingRate(Lib.Sequencing.MountState mount) => !mount.IsTracking ? TrackingSpeed.None : mount.TrackingRate;

    /// <summary>The Track row's words for <paramref name="mount"/>.</summary>
    public static string TrackingRowLabel(Lib.Sequencing.MountState mount) => ShownTrackingRate(mount) is { } shown ? TrackingLabel(shown) : "Tracking";

    /// <summary>
    /// The speed row, then the move row. <paramref name="idPrefix"/> keeps each panel's hit ids apart: "MoveSpeedDown",
    /// "MoveSpeedUp" and "MountMoveN" with none.
    /// </summary>
    public static IEnumerable<Layout.Node> Rows(int speedIdx, Action<int> chooseSpeed,
        Action<StartMountMoveSignal> start, Action<StopMountMoveSignal> stop, string idPrefix, in Look look)
    {
        var speed = MountJog.SiderealMultiples[speedIdx];
        var (fontSize, rowHeight, buttonBg, glyphText) = (look.FontSize, look.RowHeight, look.ButtonBg, look.GlyphText);

        Layout.Node Move(string glyph, GuideDirection direction)
            => Layout.Builder.Text(glyph, fontSize, glyphText, TextAlign.Center, TextAlign.Center)
                .WStar().HStar().Bg(buttonBg).BgHover(GuiTheme.Hover(buttonBg))
                .Pressable(new HitResult.ButtonHit($"{idPrefix}MountMove{glyph}"), _ =>
                {
                    start(new StartMountMoveSignal(direction, speed));
                    return new DragCapture(static _ => { }, _ => stop(new StopMountMoveSignal()));
                });

        var last = MountJog.SiderealMultiples.Length - 1;
        return
        [
            Stepper("Speed", SpeedLabel(speed), SpeedWidthSample, $"{idPrefix}MoveSpeed",
                () => chooseSpeed(Math.Max(0, speedIdx - 1)), () => chooseSpeed(Math.Min(last, speedIdx + 1)), look),
            Layout.Builder.HStack(
                    Label("Move", look),
                    Move("N", GuideDirection.North),
                    Move("S", GuideDirection.South),
                    Move("E", GuideDirection.East),
                    Move("W", GuideDirection.West))
                .WStar().RowH(rowHeight).WithGap(look.Gap),
        ];
    }

    /// <summary>
    /// "label [−] value [+]" as one row: the value measured from <paramref name="valueWidthSample"/>, so neither it nor the
    /// label is ever cut. Hit ids "{id}Down" and "{id}Up". The speed row and the Preview's Tracking row.
    /// </summary>
    public static Layout.Node Stepper(string label, string value, string valueWidthSample, string id, Action down, Action up, in Look look)
    {
        var (fontSize, rowHeight, buttonBg, glyphText) = (look.FontSize, look.RowHeight, look.ButtonBg, look.GlyphText);

        // A mark is the baked icon FormRowLayout.StepMark resolves, never a symbol in a text run (font coverage).
        Layout.Node Step(string glyph, string hit, Action press)
            => FormRowLayout.StepMark(glyph, fontSize, glyphText)
                .WFixed(rowHeight * 1.4f).HStar().Bg(buttonBg).BgHover(GuiTheme.Hover(buttonBg))
                .Clickable(new HitResult.ButtonHit(hit), _ => press());

        return Layout.Builder.HStack(
                Label(label, look),
                Step("−", $"{id}Down", down),
                Layout.Builder.Text(value, fontSize, look.ValueText, TextAlign.Center, TextAlign.Center, widthSample: valueWidthSample).HStar(),
                Step("+", $"{id}Up", up))
            .WStar().RowH(rowHeight).WithGap(look.Gap);
    }

    /// <summary>
    /// A row of buttons sharing the row's whole width, so the last ends where the Move row's W does: the Preview's
    /// Home / Park / Unpark / Set park.
    /// </summary>
    public static Layout.Node ButtonRow(string idPrefix, in Look look, params ReadOnlySpan<(string Label, string Id, Action Press)> buttons)
    {
        var nodes = new Layout.Node[buttons.Length];
        for (var i = 0; i < buttons.Length; i++)
        {
            var (label, id, press) = buttons[i];
            nodes[i] = Layout.Builder.Text(label, look.FontSize, look.GlyphText, TextAlign.Center, TextAlign.Center)
                .WStar().HStar().Bg(look.ButtonBg).BgHover(GuiTheme.Hover(look.ButtonBg))
                .Clickable(new HitResult.ButtonHit($"{idPrefix}{id}"), _ => press());
        }
        return Layout.Builder.HStack(nodes).WStar().RowH(look.RowHeight).WithGap(look.Gap);
    }

    /// <summary>
    /// "label [value ▾]": a choice that opens a menu, the button as wide as its value (never cut) with a caret. The press opens
    /// it; where it opens is the caller's, from the button's arranged rect (hit <paramref name="hitAction"/>).
    /// </summary>
    public static Layout.Node DropdownRow(string label, string value, string hitAction, Action press, in Look look)
    {
        var caret = look.FontSize * 0.7f;
        return Layout.Builder.HStack(
                Label(label, look),
                Layout.Builder.HStack(
                        Layout.Builder.Text(value, look.FontSize, look.ValueText, TextAlign.Near, TextAlign.Center, widthSample: value).HStar(),
                        Layout.Builder.Icon(Layout.IconKind.CaretDown, caret, look.ValueText).WFixed(caret).HFixed(caret))
                    .WithGap(caret * 0.5f).PadX(look.Gap * 1.5f).CrossCenter().HStar()
                    .Bg(look.ButtonBg).BgHover(GuiTheme.Hover(look.ButtonBg))
                    .Clickable(new HitResult.ButtonHit(hitAction), _ => press()),
                Layout.Builder.Spacer().WStar())
            .WStar().RowH(look.RowHeight).WithGap(look.Gap);
    }

    private static Layout.Node Label(string text, in Look look)
        => Layout.Builder.Text(text, look.FontSize, look.LabelText, TextAlign.Near, TextAlign.Center, widthSample: look.LabelWidthSample).HStar();

    /// <summary>"0.5x", "1x", "128x", "Max": a speed as the stepper shows it.</summary>
    internal static string SpeedLabel(double multiple)
        => multiple >= MountJog.Max ? "Max" : multiple.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x";
}
