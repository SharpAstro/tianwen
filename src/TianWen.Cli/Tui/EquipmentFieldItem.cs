using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui
{
    /// <summary>
    /// A row in the equipment settings scrollable list.
    /// Supports section headers, device slot rows, OTA headers, property steppers,
    /// filter rows, device settings, and action rows.
    /// </summary>
    internal sealed class EquipmentFieldItem : IRowLayout
    {
        // --- Common ---

        /// <summary>Section header (non-editable separator row).</summary>
        public string? SectionName { get; init; }

        /// <summary>Flat index among editable fields (for selection tracking). -1 for headers.</summary>
        public int FieldIndex { get; init; } = -1;

        /// <summary>Increment callback.</summary>
        public Action? Increment { get; init; }

        /// <summary>Decrement callback.</summary>
        public Action? Decrement { get; init; }

        // --- Device setting rows (original) ---

        /// <summary>Device setting descriptor (null for non-setting rows).</summary>
        public DeviceSettingDescriptor? Setting { get; init; }

        /// <summary>URI being edited — used to format current value.</summary>
        public Uri? DeviceUri { get; init; }

        // --- Device slot rows ---

        /// <summary>Assignment target for device slot rows.</summary>
        public AssignTarget? Slot { get; init; }

        /// <summary>Label for the slot (e.g. "Mount", "Camera").</summary>
        public string? SlotLabel { get; init; }

        /// <summary>Display name of the currently assigned device.</summary>
        public string? SlotDeviceName { get; init; }

        /// <summary>Whether this slot has a device assigned (not NoneDevice).</summary>
        public bool IsSlotActive { get; init; }

        /// <summary>URI of the currently-assigned device (null for unassigned slots).
        /// Used by the connect/disconnect toggle — distinct from <see cref="DeviceUri"/>
        /// which drives device-setting rows.</summary>
        public Uri? SlotDeviceUri { get; init; }

        /// <summary>Whether the assigned device is currently connected via the hub.
        /// Meaningless when <see cref="SlotDeviceUri"/> is null.</summary>
        public bool IsConnected { get; init; }

        /// <summary>Whether a connect/disconnect transition is in flight — shown as
        /// "..." on the target segment so the user gets visible feedback.</summary>
        public bool IsPending { get; init; }

        // --- OTA header rows ---

        /// <summary>OTA index for OTA headers (-1 for non-OTA rows).</summary>
        public int OtaIndex { get; init; } = -1;

        /// <summary>Whether this row is an OTA header (with a delete action).</summary>
        public bool IsOtaHeader { get; init; }

        /// <summary>
        /// Invoked when the OTA header's delete action is clicked. Null leaves the glyph unbound, which
        /// is what it used to be unconditionally: it was drawn as an affordance and was not one, so the
        /// only way to remove an OTA was an undocumented key.
        /// </summary>
        public Action<InputModifier>? OnRemoveOta { get; init; }

        // --- Property stepper rows ---

        /// <summary>Label for property rows (FL, Aperture, Design).</summary>
        public string? PropertyLabel { get; init; }

        /// <summary>Formatted value for property/stepper rows.</summary>
        public string? PropertyValue { get; init; }

        /// <summary>Whether this is a toggle/cycle field (no ←/→ arrows).</summary>
        public bool IsCycleField { get; init; }

        // --- Filter rows ---

        /// <summary>Filter slot index (1-based), or -1 for non-filter rows.</summary>
        public int FilterIndex { get; init; } = -1;

        /// <summary>Display name of the filter.</summary>
        public string? FilterName { get; init; }

        /// <summary>Focus offset value.</summary>
        public int FilterOffset { get; init; }

        // --- Action rows ---

        /// <summary>Action label (e.g. "+ Add OTA").</summary>
        public string? ActionLabel { get; init; }

        /// <summary>The delete affordance an OTA header carries.</summary>
        public const string DeleteActionLabel = "[X]";

        public Layout.Node BuildRow(in RowContext context)
        {
            if (SectionName is not null)
            {
                return BuildSectionHeader(SectionName);
            }

            if (ActionLabel is not null)
            {
                // Bright when the cursor is on it, dim otherwise -- an action row is always green, so the
                // usual white-on-blue selection bar would read as a different KIND of row.
                var actionPen = new RowPen(context.Selected ? SgrColor.BrightGreen : SgrColor.Green, SgrColor.Black);
                return actionPen.Text($"  {ActionLabel}");
            }

            if (SlotLabel is not null && Slot is not null)
            {
                return BuildSlotRow(context.Selected);
            }

            if (FilterIndex > 0 && FilterName is not null)
            {
                return BuildFilterRow(context.Selected);
            }

            if (PropertyLabel is not null)
            {
                return BuildLabelledControl(PropertyLabel, PropertyControl(), context.Selected);
            }

            if (Setting is { } setting && DeviceUri is not null)
            {
                return BuildLabelledControl(setting.Label, SettingControl(setting), context.Selected);
            }

            return TuiRowPalette.Body.Rest();
        }

        /// <summary>
        /// A section separator, with the OTA sections carrying a delete action at the row's right edge.
        /// <para>
        /// The action is <b>right-anchored</b>, which a formatted string could not safely be: the row's
        /// usable width is not the viewport width (<see cref="ScrollableList{T}"/> yields a column to the
        /// scrollbar once the list overflows), so a right-aligned span had to re-derive that and drifted by
        /// a column exactly when the list scrolled. It was therefore pinned one space after the title, and
        /// the columns it occupied had to be published as a static so the click region could be registered
        /// over the same arithmetic that drew them. The node is arranged into the content width and carries
        /// its own hit, so both problems are gone -- and it now sits where the GUI's [Remove] does.
        /// </para>
        /// <para>
        /// Only [X] appears: a global add is already surfaced by the "+ Add OTA" action row and the
        /// <c>A</c> hint in the status bar, so repeating it per OTA is clutter.
        /// </para>
        /// </summary>
        private Layout.Node BuildSectionHeader(string sectionName)
        {
            var pen = TuiRowPalette.SectionHeader;
            var title = pen.Text(TuiRowPalette.SectionHeaderText(sectionName));

            if (!IsOtaHeader)
            {
                return title;
            }

            var action = pen.Cell(DeleteActionLabel, DeleteActionLabel.Length, TextAlign.Center);
            if (OnRemoveOta is { } onRemove)
            {
                action = action.Clickable(new HitResult.ButtonHit($"RemoveOta{OtaIndex}"), onRemove);
            }

            return Layout.Builder.HStack(title, action).RowH(1).Bg(pen.Background);
        }

        /// <summary>
        /// <c>"  Label   DeviceName   [On|Off]  [>]"</c>.
        /// <para>
        /// The right-hand strip is a fixed width whether or not the slot has a device, so <c>[>]</c> lands
        /// in the same column on every row; and each of On/Off gets a fixed cell, so the strip no longer
        /// grows by a column while a connect is in flight (<c>"..."</c> is one character wider than
        /// <c>"On"</c>, which used to shift <c>[>]</c> for as long as the transition ran).
        /// </para>
        /// <para>
        /// The label used to take a third of the row because <c>Math.Max(14, width / 3)</c> was easy
        /// arithmetic, not because a slot label needs 26 columns -- the longest is "CoverCalibrator". It is
        /// a clamped Star now, so the surplus goes to the device name, which is the field that actually
        /// runs long.
        /// </para>
        /// </summary>
        private Layout.Node BuildSlotRow(bool isSelected)
        {
            var pen = TuiRowPalette.ForRow(isSelected);

            return Layout.Builder.HStack(
                pen.Gap(2),
                pen.Text(SlotLabel!).WStar(1f, SlotLabelMinColumns, SlotLabelMaxColumns),
                pen.Gap(1),
                pen.Text(SlotDeviceName ?? "(none)").WStar(2f, 4f),
                BuildToggleStrip(pen),
                pen.Cell(" [>]", 4))
                .RowH(1).Bg(pen.Background);
        }

        /// <summary>
        /// The <c>[On|Off]</c> strip. Both segments are always drawn; colour says which side is live and
        /// which is merely available, and a pending transition marks the segment being moved TO.
        /// <para>
        /// Each segment states its foreground against the ROW's background, which is the whole reason the
        /// string version needed a helper that re-applied the enclosing style on exit plus a scan over the
        /// emitted escape bytes to know how far to pad the line. A nested run's reset used to wipe the
        /// selection background for the rest of the row.
        /// </para>
        /// </summary>
        private Layout.Node BuildToggleStrip(RowPen pen)
        {
            if (!IsSlotActive)
            {
                return pen.Gap(ToggleColumns);
            }

            var (on, off) = (IsPending, IsConnected) switch
            {
                (true, true) => (SgrColor.BrightGreen, SgrColor.Yellow),   // disconnecting: Off is the target
                (true, false) => (SgrColor.Yellow, SgrColor.BrightRed),    // connecting: On is the target
                (false, true) => (SgrColor.BrightGreen, SgrColor.White),
                (false, false) => (SgrColor.White, SgrColor.BrightRed),
            };

            var onText = IsPending && !IsConnected ? "..." : "On";
            var offText = IsPending && IsConnected ? "..." : "Off";

            return Layout.Builder.HStack(
                pen.Gap(1),
                pen.Cell("[", 1),
                pen.WithForeground(on).Cell(onText, 3, TextAlign.Center),
                pen.Cell("|", 1),
                pen.WithForeground(off).Cell(offText, 3, TextAlign.Center),
                pen.Cell("]", 1),
                pen.Gap(1))
                .WFixed(ToggleColumns).HStar().Bg(pen.Background);
        }

        private Layout.Node BuildFilterRow(bool isSelected)
        {
            var pen = TuiRowPalette.ForRow(isSelected);
            var offset = FilterOffset >= 0 ? $"+{FilterOffset}" : $"{FilterOffset}";

            return Layout.Builder.HStack(
                pen.Gap(4),
                pen.Cell($"{FilterIndex}", 2, TextAlign.Far),
                pen.Gap(2),
                pen.Text(FilterName!).WStar(1f, 16f),
                pen.Cell(" [←] ", 5),
                pen.Cell(offset, 5, TextAlign.Far),
                pen.Cell(" [→]", 4),
                pen.Rest())
                .RowH(1).Bg(pen.Background);
        }

        /// <summary>
        /// <c>"  Label   [control]"</c> -- the shape shared by property steppers and device settings, and
        /// by <see cref="SessionFieldItem"/>. The label column is half the row with a floor, expressed as
        /// a min-clamped Star rather than the <c>Math.Max(18, width / 2)</c> each of the three copies
        /// computed for itself.
        /// </summary>
        private static Layout.Node BuildLabelledControl(string label, string control, bool isSelected)
        {
            var pen = TuiRowPalette.ForRow(isSelected);

            return Layout.Builder.HStack(
                pen.Gap(2),
                pen.Text(label).WStar(1f, TuiRowPalette.LabelMinColumns),
                pen.Text(control).WStar())
                .RowH(1).Bg(pen.Background);
        }

        private string PropertyControl() => IsCycleField
            ? $"  [{PropertyValue}]"
            : $"  [←] {PropertyValue} [→]";

        private string SettingControl(DeviceSettingDescriptor setting)
        {
            var value = setting.FormatValue(DeviceUri!);
            return setting.Kind switch
            {
                DeviceSettingKind.BoolToggle => $"  [{value}]",
                DeviceSettingKind.EnumCycle => $"  [{value}]",
                DeviceSettingKind.StringEditor => setting.Mask && value.Length > 0
                    ? $"  [{new string('*', Math.Min(value.Length, 8))}{value[Math.Max(0, value.Length - 4)..]}]"
                    : $"  [{(value.Length > 0 ? value : setting.Placeholder ?? "(empty)")}]",
                _ => $"  [←] {value} [→]",
            };
        }

        /// <summary>Floor for the slot-label column, matching the width the old <c>Math.Max</c> guaranteed.</summary>
        private const float SlotLabelMinColumns = 14f;

        /// <summary>Ceiling for the slot-label column: enough for "CoverCalibrator", the longest label.</summary>
        private const float SlotLabelMaxColumns = 18f;

        /// <summary>Columns the <c>[On|Off]</c> strip reserves, occupied or not.</summary>
        private const int ToggleColumns = 11;
    }
}
