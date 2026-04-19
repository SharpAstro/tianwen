using System;
using System.Collections.Generic;
using Console.Lib;
using DIR.Lib;

namespace TianWen.Cli.Tui;

/// <summary>
/// Rows rendered inside <see cref="TuiLiveSessionTab"/>'s info-panel scrollable list.
/// Each row formats itself to a VT string of a given width; interactive rows also
/// expose <see cref="Buttons"/> so the enclosing tab can register hit-test regions
/// at the right column offsets.
/// <para>
/// This exists because <c>MarkdownWidget</c>'s Markdig-based parser corrupts SGR escape
/// sequences and treats leading '&gt;' as blockquote, which made the stepper and
/// [Capture]/[Save]/[Solve] cells unprintable. Pre-formatted VT strings via
/// <see cref="ScrollableList{T}"/> give us full control over colour, background, and
/// whitespace preservation.
/// </para>
/// </summary>
internal abstract record InfoRowItem : IRowFormatter
{
    /// <summary>VT-escaped, padded-to-width row. Called once per visible row per frame.</summary>
    public abstract string FormatRow(int width, ColorMode colorMode);

    /// <summary>
    /// Clickable sub-regions within this row, specified in visible-character
    /// columns (so callers can multiply by cell width to get pixel coords).
    /// Empty for non-interactive rows.
    /// </summary>
    public virtual IReadOnlyList<ButtonRegion> Buttons => [];
}

/// <summary>
/// Column range within a row that is clickable and its associated handler. The tab
/// aggregates these from all rendered rows to populate the <see cref="ClickableRegionTracker"/>.
/// </summary>
internal readonly record struct ButtonRegion(int ColStart, int ColEnd, Action<InputModifier> OnClick);

/// <summary>Pure whitespace separator. Useful for vertical gaps between OTA blocks.</summary>
internal sealed record BlankRow : InfoRowItem
{
    public override string FormatRow(int width, ColorMode colorMode) => new(' ', width);
}

/// <summary>
/// Plain text row, optionally styled (colour / bold / background). Caller passes the
/// exact text to render — leading whitespace is preserved verbatim (no markdown stripping).
/// </summary>
internal sealed record TextRow(string Text, VtStyle? Style = null) : InfoRowItem
{
    public override string FormatRow(int width, ColorMode colorMode)
    {
        var pad = Math.Max(0, width - Text.Length);
        return Style.HasValue
            ? $"{Style.Value.Apply(colorMode)}{Text}{VtStyle.Reset}{new string(' ', pad)}"
            : Text + new string(' ', pad);
    }
}

/// <summary>
/// Heading row (e.g. per-OTA header, "## Focus", mount section title). Selection marker
/// in the first column lets the user see which OTA is active in preview mode.
/// </summary>
internal sealed record HeadingRow(string Text, bool IsSelected = false, Action<InputModifier>? OnClick = null) : InfoRowItem
{
    public override string FormatRow(int width, ColorMode colorMode)
    {
        var marker = IsSelected ? "\u25b8" : " "; // Black right-pointing small triangle
        var line = $"{marker} {Text}";
        var pad = Math.Max(0, width - line.Length);
        var fg = IsSelected ? new VtStyle(SgrColor.BrightWhite, SgrColor.Blue) : new VtStyle(SgrColor.BrightCyan, SgrColor.Black);
        return $"{fg.Apply(colorMode)}{line}{VtStyle.Reset}{new string(' ', pad)}";
    }

    public override IReadOnlyList<ButtonRegion> Buttons => OnClick is null
        ? []
        : [new ButtonRegion(0, int.MaxValue, OnClick)];
}

/// <summary>
/// Stepper row for exposure / gain: label, [-] button, value, [+] button, optional
/// trailing action button (e.g. [Capture] next to the exposure stepper). The buttons
/// use SGR background colours so they stand out as clickable affordances.
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
    // Fixed-width layout so the stepper doesn't jiggle when the value's length changes:
    //   "Label: [-] VALUE_PAD [+]  [ACTION]"
    //          ^DEC         ^INC  ^ACTION
    // VALUE_PAD is always VALUE_WIDTH chars. Trailing action is right-padded to fit.
    private const int ValueWidth = 10;
    private const int ButtonInnerWidth = 3; // "[-]" / "[+]"

    public override string FormatRow(int width, ColorMode colorMode)
    {
        var decStart = Label.Length + 2;                       // after "Label: "
        var valueStart = decStart + ButtonInnerWidth + 1;      // after "[-] "
        var incStart = valueStart + ValueWidth + 1;            // after value and " "
        var actionStart = incStart + ButtonInnerWidth + 2;     // after "[+]  "

        var btnStyle = new VtStyle(SgrColor.White, SgrColor.BrightBlack);

        var paddedValue = Value.Length > ValueWidth ? Value[..ValueWidth] : Value.PadRight(ValueWidth);
        var valueSpan = ValueIsOverride
            ? paddedValue
            : $"{new VtStyle(SgrColor.BrightBlack, SgrColor.Black).Apply(colorMode)}{paddedValue}{VtStyle.Reset}";

        var core =
            $"{Label}: " +
            $"{btnStyle.Apply(colorMode)}[-]{VtStyle.Reset} " +
            $"{valueSpan} " +
            $"{btnStyle.Apply(colorMode)}[+]{VtStyle.Reset}";

        if (ActionLabel is not null && OnAction is not null)
        {
            var style = ActionStyle.HasValue ? ActionStyle.Value : new VtStyle(SgrColor.BrightWhite, SgrColor.Green);
            core += $"  {style.Apply(colorMode)}[{ActionLabel}]{VtStyle.Reset}";
        }

        // Pad right to the list width. The visible length of `core` equals
        // actionStart + ActionLabel.Length + 2 (for the brackets) when an action
        // is present, otherwise incStart + ButtonInnerWidth. We compute padding
        // based on visible chars, not raw string length (which includes SGR bytes).
        var visibleLen = incStart + ButtonInnerWidth;
        if (ActionLabel is not null)
        {
            visibleLen = actionStart + ActionLabel.Length + 2;
        }
        return core + new string(' ', Math.Max(0, width - visibleLen));
    }

    public override IReadOnlyList<ButtonRegion> Buttons
    {
        get
        {
            var decStart = Label.Length + 2;
            var valueStart = decStart + ButtonInnerWidth + 1;
            var incStart = valueStart + ValueWidth + 1;
            var actionStart = incStart + ButtonInnerWidth + 2;

            var regions = new List<ButtonRegion>(3)
            {
                new ButtonRegion(decStart, decStart + ButtonInnerWidth, OnDec),
                new ButtonRegion(incStart, incStart + ButtonInnerWidth, OnInc),
            };
            if (ActionLabel is not null && OnAction is not null)
            {
                regions.Add(new ButtonRegion(actionStart, actionStart + ActionLabel.Length + 2, OnAction));
            }
            return regions;
        }
    }
}

/// <summary>
/// One-line progress row shown during a preview capture. No clickable controls —
/// a filled bar and elapsed/total seconds so the user sees forward motion.
/// </summary>
internal sealed record ProgressRow(string Label, double ElapsedSec, double TotalSec) : InfoRowItem
{
    private const int BarWidth = 16;

    public override string FormatRow(int width, ColorMode colorMode)
    {
        var frac = TotalSec > 0 ? Math.Clamp(ElapsedSec / TotalSec, 0.0, 1.0) : 0.0;
        var filled = (int)(frac * BarWidth);
        var bar = new string('\u2588', filled) + new string('\u2591', BarWidth - filled);
        var text = $"{Label}: {bar} {ElapsedSec:F0}/{TotalSec:F0}s";
        var style = new VtStyle(SgrColor.BrightGreen, SgrColor.Black);
        var pad = Math.Max(0, width - text.Length);
        return $"{style.Apply(colorMode)}{text}{VtStyle.Reset}{new string(' ', pad)}";
    }
}

/// <summary>
/// Row of up to four coloured action buttons (e.g. [J-50] [J+50] [Save] [Solve]).
/// Layout is "  BTN1 BTN2 ... " with a single space between; regions are computed
/// by walking the labels in order.
/// </summary>
internal sealed record ActionRow(IReadOnlyList<ActionRow.Button> Buttons_) : InfoRowItem
{
    public readonly record struct Button(string Label, Action<InputModifier> OnClick, VtStyle Style);

    public override string FormatRow(int width, ColorMode colorMode)
    {
        var sb = new System.Text.StringBuilder("  ");
        var visibleLen = 2;
        for (var i = 0; i < Buttons_.Count; i++)
        {
            if (i > 0) { sb.Append(' '); visibleLen++; }
            var b = Buttons_[i];
            sb.Append($"{b.Style.Apply(colorMode)}[{b.Label}]{VtStyle.Reset}");
            visibleLen += b.Label.Length + 2;
        }
        sb.Append(new string(' ', Math.Max(0, width - visibleLen)));
        return sb.ToString();
    }

    public override IReadOnlyList<ButtonRegion> Buttons
    {
        get
        {
            var regions = new List<ButtonRegion>(Buttons_.Count);
            var col = 2; // leading "  "
            for (var i = 0; i < Buttons_.Count; i++)
            {
                if (i > 0) col++; // space between
                var b = Buttons_[i];
                regions.Add(new ButtonRegion(col, col + b.Label.Length + 2, b.OnClick));
                col += b.Label.Length + 2;
            }
            return regions;
        }
    }
}
