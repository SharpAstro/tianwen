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
    /// Builds the surface-agnostic <see cref="LayoutNode"/> tree for the profile/equipment panel from the
    /// data-driven content models (<see cref="DeviceSlotRow"/> / <see cref="OtaSummaryRow"/> emitted by
    /// <see cref="EquipmentContent"/>). One tree, arranged + painted by BOTH the GPU pixel painter and the
    /// TUI cell painter -- so the per-OTA panel is genuinely data-driven (loop over the OTA set, no hardcoded
    /// slot sequence) and the two surfaces can no longer drift. Stateful/interactive sub-widgets (site editor,
    /// camera telemetry graph, filter-offset editors, device dropdowns) stay as imperative helpers; this
    /// builder owns the vertical flow + the repeating header/slot/OTA/filter structure.
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
        public static LayoutNode Build(
            string profileName,
            IReadOnlyList<DeviceSlotRow> profileSlots,
            IReadOnlyList<OtaSummaryRow> otas,
            EquipmentPanelStyle style,
            AssignTarget? activeSlot = null,
            Func<AssignTarget, Action<InputModifier>?>? onSlotClick = null)
        {
            var palette = style.Theme.Palette;
            var metrics = style.Theme.Metrics;
            var children = ImmutableArray.CreateBuilder<LayoutNode>();

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

            return new LayoutNode.Stack(children.ToImmutable(), LayoutAxis.Vertical, Gap: metrics.Padding * 0.25f)
            {
                Padding = metrics.Padding,
                Width = Sizing.Star(),
                Height = Sizing.Star(),
                Background = palette.PanelBg,
            };
        }

        private static LayoutNode Header(string text, float fontSize, float height, RGBAColor32 color, RGBAColor32? background = null) =>
            new LayoutNode.Leaf(new LayoutContent.Text(text, fontSize) { Color = color })
            {
                Height = Sizing.Fixed(height),
                Width = Sizing.Star(),
                Background = background,
            };

        private static LayoutNode Properties(string properties, UiMetrics metrics, UiPalette palette) =>
            new LayoutNode.Leaf(new LayoutContent.Text(properties, metrics.BaseFontSize * 0.85f) { Color = palette.DimText })
            {
                Height = Sizing.Fixed(metrics.ItemHeight * 0.8f),
                Width = Sizing.Star(),
            };

        private static LayoutNode SeparatorRow(UiPalette palette) =>
            new LayoutNode.Leaf(new LayoutContent.Box(0f, 1f) { Color = palette.Separator })
            {
                Height = Sizing.Fixed(1f),
                Width = Sizing.Star(),
            };

        /// <summary>
        /// Builds one device-slot row: <c>[pad | label .35 | name * | indicator]</c> as a horizontal Stack
        /// whose whole rect carries the click <see cref="HitResult.SlotHit{T}"/> + handler + background
        /// (draw-rect == hit-rect by construction). The indicator is a surface-neutral <see cref="LayoutContent.Fill"/>
        /// slot painted by the caller's <c>drawFill</c> (the GPU panel draws a reachability dot or the
        /// <c>[&gt;]</c> arrow; a terminal panel draws its own glyph) -- which is what lets the live equipment
        /// panel and the (eventual) TUI panel share this exact structure. Public so the live
        /// <c>EquipmentTab</c> consumes it directly instead of re-deriving the row inline.
        /// </summary>
        public static LayoutNode SlotRow(
            DeviceSlotRow slot, EquipmentPanelStyle style, AssignTarget? activeSlot,
            Func<AssignTarget, Action<InputModifier>?>? onSlotClick)
        {
            var palette = style.Theme.Palette;
            var metrics = style.Theme.Metrics;
            var isActive = activeSlot is not null && activeSlot == slot.Slot;

            // Lead pad gives the label its left inset; every column's Height is Star so it stretches to
            // the full row height for vertical centring (Auto would collapse a Text leaf to glyph height).
            var pad = new LayoutNode.Leaf(new LayoutContent.Box(0f, 0f)) { Width = Sizing.Fixed(metrics.Padding), Height = Sizing.Star() };
            var label = new LayoutNode.Leaf(new LayoutContent.Text(slot.Label, metrics.BaseFontSize * 0.9f) { Color = palette.DimText })
            {
                Width = Sizing.Star(LabelShare), Height = Sizing.Star(),
            };
            var name = new LayoutNode.Leaf(new LayoutContent.Text(slot.DeviceName, metrics.BaseFontSize) { Color = palette.BodyText })
            {
                Width = Sizing.Star(1f - LabelShare), Height = Sizing.Star(),
            };
            var indicator = new LayoutNode.Leaf(new LayoutContent.Fill()) { Width = Sizing.Fixed(ArrowWidth), Height = Sizing.Star() };

            // The whole row is clickable (Hit on the Stack), so a click anywhere toggles assignment.
            return new LayoutNode.Stack([pad, label, name, indicator], LayoutAxis.Horizontal)
            {
                Height = Sizing.Fixed(metrics.ItemHeight),
                Width = Sizing.Star(),
                Background = isActive ? style.SlotActive : style.SlotNormal,
                Hit = new HitResult.SlotHit<AssignTarget>(slot.Slot),
                OnClick = onSlotClick?.Invoke(slot.Slot),
            };
        }

        private static LayoutNode FilterTable(IReadOnlyList<FilterSlotRow> filters, EquipmentPanelStyle style)
        {
            var palette = style.Theme.Palette;
            var metrics = style.Theme.Metrics;
            var rows = ImmutableArray.CreateBuilder<LayoutNode>();
            foreach (var f in filters)
            {
                var offset = f.FocusOffset >= 0 ? $"+{f.FocusOffset}" : f.FocusOffset.ToString();
                rows.Add(new LayoutNode.Stack(
                [
                    new LayoutNode.Leaf(new LayoutContent.Text($"{f.Position}", metrics.BaseFontSize * 0.85f) { Color = palette.DimText, HAlign = TextAlign.Far }) { Width = Sizing.Fixed(24f) },
                    new LayoutNode.Leaf(new LayoutContent.Text(f.Name, metrics.BaseFontSize * 0.85f) { Color = palette.BodyText }) { Width = Sizing.Star() },
                    new LayoutNode.Leaf(new LayoutContent.Text(offset, metrics.BaseFontSize * 0.85f) { Color = palette.DimText, HAlign = TextAlign.Far }) { Width = Sizing.Fixed(48f) },
                ], LayoutAxis.Horizontal)
                {
                    Height = Sizing.Fixed(metrics.ItemHeight * 0.85f),
                    Width = Sizing.Star(),
                });
            }

            return new LayoutNode.Stack(rows.ToImmutable(), LayoutAxis.Vertical)
            {
                Width = Sizing.Star(),
                Height = Sizing.Auto,
                Padding = metrics.Padding * 0.5f,
                Background = style.FilterTableBg,
            };
        }
    }
}
