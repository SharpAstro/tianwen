using System;
using System.Collections.Generic;
using Console.Lib;
using DIR.Lib;

namespace TianWen.Cli.Tui
{
    /// <summary>
    /// Rows rendered inside <see cref="TuiLiveSessionTab"/>'s info-panel scrollable list. Each row builds
    /// itself as a layout tree; a row's inline buttons are clickable nodes ON that tree, so the enclosing
    /// tab dispatches a click through the list without knowing any of their columns.
    /// <para>
    /// This exists because <c>MarkdownWidget</c>'s Markdig-based parser corrupts SGR escape sequences and
    /// treats leading '&gt;' as blockquote, which made the stepper and [Capture]/[Save]/[Solve] cells
    /// unprintable. A <see cref="ScrollableList{T}"/> of authored rows gives us full control over colour,
    /// background, and whitespace.
    /// </para>
    /// <para>
    /// <b>The <c>ButtonRegion</c> list these rows used to publish is gone.</b> A formatted string has no
    /// rect to bind a hit to, so every interactive row computed its button columns twice -- once while
    /// writing the glyphs and again for the region list -- and the two had to be kept in step by hand.
    /// <see cref="StepperRow"/> was the worst of it: four column offsets derived identically in both
    /// halves, where changing the value width in one place silently moved the buttons away from the
    /// glyphs. Hits ride on <c>.Clickable(...)</c> now and resolve against the rect that was painted.
    /// </para>
    /// </summary>
    internal abstract record InfoRowItem : IRowLayout
    {
        /// <summary>Builds this row. Called once per visible row per frame.</summary>
        public abstract Layout.Node BuildRow(in RowContext context);
    }

    /// <summary>Pure whitespace separator. Useful for vertical gaps between OTA blocks.</summary>
    internal sealed record BlankRow : InfoRowItem
    {
        public override Layout.Node BuildRow(in RowContext context) => TuiRowPalette.Body.Rest();
    }

    /// <summary>
    /// Plain text row, optionally styled (colour / background). Caller passes the exact text to render --
    /// leading whitespace is preserved verbatim (no markdown stripping).
    /// </summary>
    internal sealed record TextRow(string Text, VtStyle? Style = null) : InfoRowItem
    {
        public override Layout.Node BuildRow(in RowContext context)
            => (Style is { } style ? new RowPen(style) : TuiRowPalette.Body).Text(Text);
    }

    /// <summary>
    /// Heading row (e.g. per-OTA header, "## Focus", mount section title). Selection marker
    /// in the first column lets the user see which OTA is active in preview mode.
    /// </summary>
    internal sealed record HeadingRow(string Text, bool IsSelected = false, Action<InputModifier>? OnClick = null) : InfoRowItem
    {
        public override Layout.Node BuildRow(in RowContext context)
        {
            var pen = IsSelected ? TuiRowPalette.Selected : new RowPen(SgrColor.BrightCyan, SgrColor.Black);
            var marker = IsSelected ? "▸" : " "; // Black right-pointing small triangle
            var row = pen.Text($"{marker} {Text}");

            // The whole row is the affordance, which used to be spelled as a span from column 0 to
            // int.MaxValue -- a sentinel the list had to clamp. A Star-width node IS the row.
            return OnClick is null
                ? row
                : row.Clickable(new HitResult.ButtonHit($"InfoHeading:{Text}"), OnClick);
        }
    }

    /// <summary>
    /// Stepper row for exposure / gain: label, [-] button, value, [+] button, optional
    /// trailing action button (e.g. [Capture] next to the exposure stepper). The buttons
    /// carry their own background so they read as clickable affordances.
    /// </summary>
    internal sealed record StepperRow(
        string Label,
        string Value,
        Action<InputModifier> OnDec,
        Action<InputModifier> OnInc,
        string? ActionLabel = null,
        Action<InputModifier>? OnAction = null,
        VtStyle? ActionStyle = null,
        bool ValueIsOverride = true) : InfoRowItem
    {
        /// <summary>
        /// Fixed value width so the stepper does not jiggle as the value's length changes -- the [+] must
        /// not move under the cursor between frames.
        /// </summary>
        private const int ValueColumns = 10;

        public override Layout.Node BuildRow(in RowContext context)
        {
            var body = TuiRowPalette.Body;
            var button = TuiRowPalette.Button;

            // A value the user has not overridden is dimmed -- it is showing the driver's own number.
            var valuePen = ValueIsOverride ? body : TuiRowPalette.Dim;

            var cells = new List<Layout.Node>(7)
            {
                body.Cell($"{Label}: ", Label.Length + 2),
                button.Cell("[-]", 3).Clickable(new HitResult.ButtonHit($"Stepper:{Label}:dec"), OnDec),
                body.Gap(1),
                valuePen.Cell(Value, ValueColumns),
                body.Gap(1),
                button.Cell("[+]", 3).Clickable(new HitResult.ButtonHit($"Stepper:{Label}:inc"), OnInc),
            };

            if (ActionLabel is not null && OnAction is not null)
            {
                var actionPen = ActionStyle is { } style
                    ? new RowPen(style)
                    : new RowPen(SgrColor.BrightWhite, SgrColor.Green);
                cells.Add(body.Gap(2));
                cells.Add(actionPen.Cell($"[{ActionLabel}]", ActionLabel.Length + 2)
                    .Clickable(new HitResult.ButtonHit($"Stepper:{Label}:action"), OnAction));
            }

            cells.Add(body.Rest());
            return Layout.Builder.HStack([.. cells]).RowH(1).Bg(body.Background);
        }
    }

    /// <summary>
    /// One-line progress row shown during a preview capture. No clickable controls --
    /// a filled bar and elapsed/total seconds so the user sees forward motion.
    /// </summary>
    internal sealed record ProgressRow(string Label, double ElapsedSec, double TotalSec) : InfoRowItem
    {
        private const int BarWidth = 16;

        public override Layout.Node BuildRow(in RowContext context)
        {
            var frac = TotalSec > 0 ? Math.Clamp(ElapsedSec / TotalSec, 0.0, 1.0) : 0.0;
            var filled = (int)(frac * BarWidth);
            var bar = new string('█', filled) + new string('░', BarWidth - filled);
            return new RowPen(SgrColor.BrightGreen, SgrColor.Black)
                .Text($"{Label}: {bar} {ElapsedSec:F0}/{TotalSec:F0}s");
        }
    }

    /// <summary>
    /// Row of up to four coloured action buttons (e.g. [J-50] [J+50] [Save] [Solve]), each its own
    /// clickable cell. Walking the labels to compute where each one starts -- once to draw and again to
    /// register -- is what the tree removes.
    /// </summary>
    internal sealed record ActionRow(IReadOnlyList<ActionRow.Button> Buttons) : InfoRowItem
    {
        public readonly record struct Button(string Label, Action<InputModifier> OnClick, VtStyle Style);

        public override Layout.Node BuildRow(in RowContext context)
        {
            var body = TuiRowPalette.Body;
            var cells = new List<Layout.Node>(Buttons.Count * 2 + 2) { body.Gap(2) };

            for (var i = 0; i < Buttons.Count; i++)
            {
                if (i > 0)
                {
                    cells.Add(body.Gap(1));
                }

                var b = Buttons[i];
                cells.Add(new RowPen(b.Style).Cell($"[{b.Label}]", b.Label.Length + 2)
                    .Clickable(new HitResult.ButtonHit($"InfoAction:{b.Label}"), b.OnClick));
            }

            cells.Add(body.Rest());
            return Layout.Builder.HStack([.. cells]).RowH(1).Bg(body.Background);
        }
    }
}
