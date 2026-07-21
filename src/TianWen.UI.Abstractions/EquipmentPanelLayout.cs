using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Equipment-panel chrome colours not covered by the shared <see cref="GuiTheme"/> palette: the
    /// device-slot row states + the OTA-header / filter-table backgrounds. Bundled with a
    /// <see cref="UiTheme"/> so the builder reads palette + metrics from one place.
    /// </summary>
    public readonly record struct EquipmentPanelStyle(
        UiTheme Theme,
        RGBAColor32 SlotNormal,
        RGBAColor32 SlotActive,
        RGBAColor32 OtaHeaderBg,
        RGBAColor32 FilterTableBg)
    {
        /// <summary>The default dark equipment palette (matches the values previously inlined in EquipmentTab).</summary>
        public static EquipmentPanelStyle Default { get; } = new(
            GuiTheme.Theme,
            SlotNormal:    new RGBAColor32(0x2a, 0x2a, 0x35, 0xff),
            SlotActive:    new RGBAColor32(0x2a, 0x6b, 0xb8, 0xff),
            OtaHeaderBg:   new RGBAColor32(0x24, 0x24, 0x32, 0xff),
            FilterTableBg: new RGBAColor32(0x1a, 0x1a, 0x26, 0xff));
    }

    /// <summary>
    /// Builds the surface-agnostic <see cref="Layout.Node"/> tree for the profile/equipment panel from the
    /// data-driven content models (<see cref="DeviceSlotRow"/> / <see cref="OtaSummaryRow"/> emitted by
    /// <see cref="EquipmentContent"/>). One tree, arranged + painted by BOTH the GPU pixel painter and the
    /// TUI cell painter -- so the per-OTA panel is genuinely data-driven (loop over the OTA set, no hardcoded
    /// slot sequence) and the two surfaces can no longer drift. Stateful/interactive sub-widgets (site editor,
    /// camera telemetry graph, filter-offset editors, device dropdowns) stay as imperative helpers; this
    /// builder owns the vertical flow + the repeating header/slot/OTA/filter structure.
    /// <para>
    /// Built with the <c>Layout.Builder</c> DSL -- the static row builders compose <c>Text</c>/<c>HStack</c>/
    /// <c>Fill</c> with fluent <c>.WStar()</c>/<c>.RowH()</c>/<c>.Bg()</c>/<c>.Clickable()</c> modifiers; the
    /// dynamic vertical flow aggregates the rows into <c>Builder.VStack(...)</c>.
    /// </para>
    /// </summary>
    public static class EquipmentPanelLayout
    {
        // Design-unit width of the right-edge slot indicator column (matches EquipmentTab's BaseArrowWidth).
        private const float ArrowWidth = 22f;

        // Label column share of a slot row (matches EquipmentTab's labelW = 0.35 * width).
        // Internal so the live EquipmentTab can size its name-column truncation to the same split.
        internal const float LabelShare = 0.35f;

        /// <summary>
        /// Builds the panel tree. <paramref name="activeSlot"/> highlights the row currently in assignment
        /// mode; <paramref name="onSlotClick"/> supplies the per-slot click handler (the host wires it to
        /// toggle assignment + request a redraw). Both are optional so the builder stays unit-testable.
        /// </summary>
        public static Layout.Node Build(
            string profileName,
            IReadOnlyList<DeviceSlotRow> profileSlots,
            IReadOnlyList<OtaSummaryRow> otas,
            EquipmentPanelStyle style,
            AssignTarget? activeSlot = null,
            Func<AssignTarget, Action<InputModifier>?>? onSlotClick = null)
        {
            var palette = style.Theme.Palette;
            var metrics = style.Theme.Metrics;
            var children = ImmutableArray.CreateBuilder<Layout.Node>();

            // Profile name header.
            children.Add(Header($"Profile: {profileName}", metrics.BaseFontSize * 1.1f, metrics.HeaderHeight, palette.HeaderText));
            children.Add(SeparatorRow(palette));

            // Profile-level device slots.
            foreach (var slot in profileSlots)
            {
                children.Add(SlotRow(slot, style, activeSlot, onSlotClick));
            }

            children.Add(SeparatorRow(palette));

            // Per-OTA sections -- the data-driven loop: one section per telescope, no hardcoded count.
            foreach (var ota in otas)
            {
                children.Add(Header($"Telescope #{ota.Index}: {ota.Name}", metrics.BaseFontSize, metrics.ItemHeight, palette.HeaderText, style.OtaHeaderBg));
                children.Add(Properties(ota.Properties, metrics, palette));

                foreach (var slot in ota.DeviceSlots)
                {
                    children.Add(SlotRow(slot, style, activeSlot, onSlotClick));
                }

                if (ota.Filters is { Count: > 0 } filters)
                {
                    children.Add(FilterTable(filters, style));
                }
            }

            return Layout.Builder.VStack(children.ToImmutable().AsSpan())
                .WithGap(metrics.Padding * 0.25f)
                .Pad(metrics.Padding)
                .Stretch()
                .Bg(palette.PanelBg);
        }

        private static Layout.Node Header(string text, float fontSize, float height, RGBAColor32 color, RGBAColor32? background = null)
        {
            var header = Layout.Builder.Text(text, fontSize, color).RowH(height);
            return background is { } bg ? header.Bg(bg) : header;
        }

        private static Layout.Node Properties(string properties, UiMetrics metrics, UiPalette palette) =>
            Layout.Builder.Text(properties, metrics.BaseFontSize * 0.85f, palette.DimText).RowH(metrics.ItemHeight * 0.8f);

        private static Layout.Node SeparatorRow(UiPalette palette) =>
            Layout.Builder.Box(0f, 1f, palette.Separator).RowH(1f);

        /// <summary>
        /// Builds one device-slot row: <c>[pad | label .35 | name * | indicator]</c> as a horizontal Stack
        /// whose whole rect carries the click <see cref="HitResult.SlotHit{T}"/> + handler + background
        /// (draw-rect == hit-rect by construction). The indicator is a surface-neutral <see cref="Layout.Content.Fill"/>
        /// slot painted by the caller's <c>drawFill</c> (the GPU panel draws a reachability dot or the
        /// <c>[&gt;]</c> arrow; a terminal panel draws its own glyph) -- which is what lets the live equipment
        /// panel and the (eventual) TUI panel share this exact structure. Public so the live
        /// <c>EquipmentTab</c> consumes it directly instead of re-deriving the row inline.
        /// </summary>
        public static Layout.Node SlotRow(
            DeviceSlotRow slot, EquipmentPanelStyle style, AssignTarget? activeSlot,
            Func<AssignTarget, Action<InputModifier>?>? onSlotClick,
            string? indicatorFillKey = null)
        {
            var palette = style.Theme.Palette;
            var metrics = style.Theme.Metrics;
            var isActive = activeSlot is not null && activeSlot == slot.Slot;

            // Lead pad gives the label its left inset; every column's Height is Star so it stretches to
            // the full row height for vertical centring (Auto would collapse a Text leaf to glyph height).
            var pad = Layout.Builder.Spacer().ColW(metrics.Padding);
            var label = Layout.Builder.Text(slot.Label, metrics.BaseFontSize * 0.9f, palette.DimText).WStar(LabelShare).HStar();
            var name = Layout.Builder.Text(slot.DeviceName, metrics.BaseFontSize, palette.BodyText).WStar(1f - LabelShare).HStar();
            var indicator = Layout.Builder.Fill(key: indicatorFillKey).ColW(ArrowWidth);

            // The whole row is clickable (Hit on the Stack), so a click anywhere toggles assignment.
            return Layout.Builder.HStack(pad, label, name, indicator)
                .RowH(metrics.ItemHeight)
                .Bg(isActive ? style.SlotActive : style.SlotNormal)
                .Clickable(new HitResult.SlotHit<AssignTarget>(slot.Slot), onSlotClick?.Invoke(slot.Slot));
        }

        private static Layout.Node FilterTable(IReadOnlyList<FilterSlotRow> filters, EquipmentPanelStyle style)
        {
            var palette = style.Theme.Palette;
            var metrics = style.Theme.Metrics;
            var fontSize = metrics.BaseFontSize * 0.85f;
            var rows = ImmutableArray.CreateBuilder<Layout.Node>();
            foreach (var f in filters)
            {
                var offset = f.FocusOffset >= 0 ? $"+{f.FocusOffset}" : f.FocusOffset.ToString();
                rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text($"{f.Position}", fontSize, palette.DimText, TextAlign.Far).WFixed(24f),
                    Layout.Builder.Text(f.Name, fontSize, palette.BodyText).WStar(),
                    Layout.Builder.Text(offset, fontSize, palette.DimText, TextAlign.Far).WFixed(48f))
                    .RowH(metrics.ItemHeight * 0.85f));
            }

            return Layout.Builder.VStack(rows.ToImmutable().AsSpan())
                .WStar()
                .Pad(metrics.Padding * 0.5f)
                .Bg(style.FilterTableBg);
        }
    }
}
