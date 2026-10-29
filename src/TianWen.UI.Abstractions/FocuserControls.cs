using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Which focuser a <see cref="JogFocuserSignal"/> or <see cref="GotoFocuserSignal"/> means, beside an OTA's (its index in the
/// profile): the profile's guide focuser (#1915), which the Guider tab's looped guide camera is focused with.
/// </summary>
public static class FocuserTargets
{
    /// <summary>The profile's <c>GuiderFocuser</c>, in the place of an OTA index.</summary>
    public const int Guider = -1;
}

/// <summary>
/// The focuser controls ONE panel offers for a connected focuser: its position (with temperature and a moving mark), a jog row
/// (fine +-10, coarse +-100) and a goto field with its Go button. The live Preview builds one per OTA and the Guider tab one
/// for the guide focuser, so the two cannot drift; each passes what a press does (the signals, which ask the node and its
/// <c>DeviceOwnershipGate</c>), so this builds nothing but the tree.
/// </summary>
public static class FocuserControls
{
    /// <summary>The step of a fine jog button.</summary>
    public const int FineStep = 10;

    /// <summary>The step of a coarse jog button.</summary>
    public const int CoarseStep = 100;

    /// <summary>
    /// The rows for <paramref name="reading"/>: the readout, the jog buttons and, with a <paramref name="gotoInput"/>, the goto
    /// row. Empty for a focuser that is not connected (<paramref name="reading"/> null): what a panel says then is its own.
    /// </summary>
    /// <param name="idSuffix">Ends each button's hit id (<c>FocFineIn{idSuffix}</c>), the OTA index or <c>Guide</c>.</param>
    /// <param name="reading">What the focuser reads; null when it is not connected.</param>
    /// <param name="gotoInput">The goto field's state, kept by the caller across frames; null leaves the goto row out.</param>
    /// <param name="jog">What a jog button does, given the signed step.</param>
    /// <param name="goTo">What Go (or Enter in the field) does, given the position typed.</param>
    /// <param name="rowHeight">The height of each row, in design units.</param>
    public static List<Layout.Node> Rows(string idSuffix, FocuserReading? reading, TextInputState? gotoInput,
        Action<int> jog, Action<int> goTo, float rowHeight)
    {
        var rows = new List<Layout.Node>();
        if (reading is not { } focuser)
        {
            return rows;
        }

        var baseFontSize = GuiTheme.Metrics.BaseFontSize;
        var body = GuiTheme.Palette.BodyText;
        var dim = GuiTheme.Palette.DimText;

        var label = $"Foc: {LiveSessionActions.FocusPositionLabel(focuser.Position)}";
        if (!double.IsNaN(focuser.TemperatureC))
        {
            label += $"  {focuser.TemperatureC:F1}°C";
        }
        if (focuser.IsMoving)
        {
            label += "  ⇄";
        }
        rows.Add(Layout.Builder.Text(label, baseFontSize, focuser.IsMoving ? GuiTheme.Palette.Warn : body).RowH(rowHeight));

        var buttonBg = GuiTheme.NeutralButtonBg;

        // Jog buttons row: [<<] [<] "10 | 100" [>] [>>] as one HStack.
        Layout.Node JogButton(string glyph, string action, int delta) =>
            FormRowLayout.StepMark(glyph, baseFontSize * 0.85f, body)
                .WFixed(32f).HStar().Bg(buttonBg).BgHover(GuiTheme.Hover(buttonBg))
                .Clickable(new HitResult.ButtonHit(action), _ => jog(delta));

        rows.Add(Layout.Builder.HStack(
                JogButton("«", $"FocCoarseIn{idSuffix}", -CoarseStep),
                JogButton("‹", $"FocFineIn{idSuffix}", -FineStep),
                Layout.Builder.Text($"{FineStep} | {CoarseStep}", baseFontSize * 0.85f * 0.85f, dim, TextAlign.Center, TextAlign.Center).WStar().HStar(),
                JogButton("›", $"FocFineOut{idSuffix}", FineStep),
                JogButton("»", $"FocCoarseOut{idSuffix}", CoarseStep))
            .WithGap(2f).RowH(rowHeight));

        // Goto-position row: numeric input pre-filled with the current focuser step + a "Go" button.
        if (gotoInput is not null)
        {
            // Pre-filled only with a position the focuser gave: a failed read leaves the field empty.
            if (!gotoInput.IsActive && string.IsNullOrEmpty(gotoInput.Text) && FocuserPositions.IsKnown(focuser.Position))
            {
                gotoInput.Text = focuser.Position.ToString();
                gotoInput.CursorPos = gotoInput.Text.Length;
            }
            gotoInput.OnCommit = text =>
            {
                if (int.TryParse(text, out var pos))
                {
                    goTo(pos);
                }
                return Task.CompletedTask;
            };

            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.TextInput(gotoInput, baseFontSize * 0.85f).Stretch(),
                    Layout.Builder.Text("Go", baseFontSize * 0.85f, body, TextAlign.Center, TextAlign.Center)
                        .WFixed(32f).HStar().Bg(buttonBg).BgHover(GuiTheme.Hover(buttonBg))
                        .Clickable(new HitResult.ButtonHit($"FocGoto{idSuffix}"), _ =>
                        {
                            if (int.TryParse(gotoInput.Text, out var pos))
                            {
                                goTo(pos);
                            }
                        }))
                .WithGap(4f).RowH(rowHeight));
        }

        return rows;
    }
}
