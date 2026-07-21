using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Per-OTA installed-filter table: collapsible editor with name dropdown / custom-name
    /// input and focus-offset steppers, plus the dirty-gated Save / Cancel row.
    /// </summary>
    partial class EquipmentTab<TSurface>
    {
        // -----------------------------------------------------------------------
        // Filter table
        // -----------------------------------------------------------------------

        /// <summary>
        /// Builds the per-OTA installed-filter table as one profile-panel section node: collapsible
        /// header -> column header -> per-filter rows (name dropdown / custom-name input + offset
        /// steppers) -> dirty-gated Save/Cancel. The custom-name input and the clickable name cell are
        /// keyed <c>Fill</c> leaves painted through <see cref="_profilePanelFills"/>; the name cell's
        /// painter stashes its arranged rect so the name dropdown anchors from the real layout position.
        /// </summary>
        private Layout.Node BuildFilterTable(
            GuiAppState appState, int otaIndex, ProfileData pd, float dpiScale, string fontPath)
        {
            var savedFilters = EquipmentActions.GetFilterConfig(pd, otaIndex);
            var isExpanded = State.ExpandedFilterOtaIndex == otaIndex;
            var fontSize = BaseFontSize * dpiScale;
            float rowH = BaseItemHeight * 0.9f;   // design units
            var capturedOtaIdx = otaIndex;

            // Toggle header -- clicking expands/collapses and loads/discards editing state
            var headerLabel = isExpanded
                ? $"    Filters ({savedFilters.Count}) [-]"
                : $"    Filters ({savedFilters.Count}) [+]";
            var toggle = FormRowLayout.ToggleHeaderRow(
                headerLabel, rowH, FilterTableBg, HeaderText, BaseFontSize * 0.85f,
                new HitResult.ButtonHit($"ToggleFilters{otaIndex}"),
                _ =>
                {
                    if (isExpanded)
                    {
                        State.ExpandedFilterOtaIndex = -1;
                        State.StopEditingFilters();
                    }
                    else
                    {
                        State.ExpandedFilterOtaIndex = capturedOtaIdx;
                        State.BeginEditingFilters(savedFilters);
                    }
                });

            if (!isExpanded || State.EditingFilters is not { } filters)
            {
                return toggle;
            }

            var rows = new List<Layout.Node> { toggle };

            // Column headers as one HStack (lead/trail pad keeps the columns aligned with the rows below).
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Spacer().WFixed(BasePadding).HStar(),
                    Layout.Builder.Text("#", BaseFontSize * 0.75f, DimText, TextAlign.Center, TextAlign.Center).WFixed(20f).HStar(),
                    Layout.Builder.Text("Name", BaseFontSize * 0.75f, DimText).WStar().HStar(),
                    Layout.Builder.Text("Offset", BaseFontSize * 0.75f, DimText, TextAlign.Center, TextAlign.Center).WFixed(100f).HStar(),
                    Layout.Builder.Spacer().WFixed(BasePadding).HStar())
                .RowH(rowH).Bg(FilterTableBg));

            // Filter rows: [pad | # | name | [- offset +] | pad], one tree each.
            for (var f = 0; f < filters.Count; f++)
            {
                var filter = filters[f];
                var rowBg = f % 2 == 0 ? FilterTableBg : FilterRowAlt;
                var capturedF = f;

                Layout.Node nameCell;
                if (State.CustomFilterSlotIndex == f)
                {
                    var inputKey = $"filterNameInput:{otaIndex}:{f}";
                    _profilePanelFills[inputKey] =
                        r => RenderTextInput(State.CustomFilterNameInput, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, fontSize * 0.8f);
                    nameCell = Layout.Builder.Fill(key: inputKey);
                }
                else
                {
                    // Clickable name cell -- the painter draws the label AND stashes the cell's arranged
                    // rect so the dropdown (opened on the click that fires a later frame) anchors from the
                    // real layout position rather than a hand-computed x/y.
                    var anchor = new RectF32[1];
                    var textKey = $"filterNameText:{otaIndex}:{f}";
                    var display = EquipmentActions.FilterDisplayName(filter);
                    _profilePanelFills[textKey] = r =>
                    {
                        anchor[0] = r;
                        DrawText(display.AsSpan(), fontPath, r.X, r.Y, r.Width, r.Height, fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Center);
                    };
                    nameCell = Layout.Builder.Fill(key: textKey)
                        .Clickable(new HitResult.ButtonHit($"FilterName{otaIndex}_{f}"), _ =>
                        {
                            var existingCustom = capturedF < filters.Count ? filters[capturedF].CustomName : null;
                            var a = anchor[0];
                            State.FilterNameDropdown.Open(
                                a.X, a.Y + a.Height, a.Width,
                                EquipmentActions.CommonFilterNames,
                                (idx, name) =>
                                {
                                    if (capturedF < filters.Count)
                                    {
                                        filters[capturedF] = new InstalledFilter(name, filters[capturedF].Position);
                                        State.FiltersDirty = true;
                                    }
                                },
                                hasCustomEntry: true,
                                onCustom: () =>
                                {
                                    State.CustomFilterSlotIndex = capturedF;
                                    var preservedName = capturedF < filters.Count && filters[capturedF].CustomName is { } cn ? cn : "";
                                    State.CustomFilterNameInput.Text = preservedName;
                                    State.CustomFilterNameInput.CursorPos = preservedName.Length;
                                    PostSignal(new ActivateTextInputSignal(State.CustomFilterNameInput));
                                },
                                customEntryLabel: existingCustom is { Length: > 0 } ? $"Custom: {existingCustom}" : null);
                        });
                }

                Layout.Node OffBtn(string glyph, string action, int delta) =>
                    Layout.Builder.Text(glyph, BaseFontSize * 0.8f, BodyText, TextAlign.Center, TextAlign.Center)
                        .WFixed(24f).HStar().Bg(EditButtonBg)
                        .Clickable(new HitResult.ButtonHit(action), _ =>
                        {
                            if (capturedF < filters.Count)
                            {
                                var cur = filters[capturedF];
                                filters[capturedF] = new InstalledFilter(cur.Filter.Name, cur.Position + delta);
                                State.FiltersDirty = true;
                            }
                        });

                var offsetStr = filter.Position >= 0 ? $"+{filter.Position}" : filter.Position.ToString();
                var offsetGroup = Layout.Builder.HStack(
                        OffBtn("-", $"FilterOffDec{otaIndex}_{f}", -1),
                        Layout.Builder.Text(offsetStr, BaseFontSize * 0.8f, BodyText, TextAlign.Center, TextAlign.Center).Stretch(),
                        OffBtn("+", $"FilterOffInc{otaIndex}_{f}", +1))
                    .WFixed(100f).HStar();

                rows.Add(Layout.Builder.HStack(
                        Layout.Builder.Spacer().WFixed(BasePadding).HStar(),
                        Layout.Builder.Text((f + 1).ToString(), BaseFontSize * 0.8f, DimText, TextAlign.Center, TextAlign.Center).WFixed(20f).HStar(),
                        nameCell.WStar().HStar(),
                        offsetGroup,
                        Layout.Builder.Spacer().WFixed(BasePadding).HStar())
                    .RowH(rowH).Bg(rowBg));
            }

            // Save / Cancel buttons (only shown when dirty), left-aligned under the rows.
            if (State.FiltersDirty)
            {
                Layout.Node ActionBtn(string label, RGBAColor32 bg, string action, Action<InputModifier> onClick) =>
                    Layout.Builder.Text(label, BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center)
                        .WFixed(60f).HStar().Bg(bg).Clickable(new HitResult.ButtonHit(action), onClick);

                rows.Add(Layout.Builder.HStack(
                        Layout.Builder.Spacer().WFixed(BasePadding * 2f).HStar(),
                        ActionBtn("Save", CreateButton, $"SaveFilters{otaIndex}",
                            _ =>
                            {
                                if (appState.ActiveProfile is { } prof && prof.Data is { } data && State.EditingFilters is { } editFilters)
                                {
                                    var newData = EquipmentActions.SetFilterConfig(data, capturedOtaIdx, editFilters);
                                    PostSignal(new UpdateProfileSignal(newData));
                                    State.FiltersDirty = false;
                                }
                            }),
                        Layout.Builder.Spacer().WFixed(BasePadding).HStar(),
                        ActionBtn("Cancel", EditButtonBg, $"CancelFilters{otaIndex}",
                            _ => State.BeginEditingFilters(savedFilters)),
                        Layout.Builder.Spacer().Stretch())
                    .RowH(BaseButtonHeight * 0.85f));
                rows.Add(Layout.Builder.Spacer().RowH(BasePadding * 0.5f));
            }

            // Wire commit for custom filter name -- on Enter, apply the name.
            // Always re-wire to avoid stale captures after list replacement.
            State.CustomFilterNameInput.OnCommit = (text) =>
            {
                if (text is { Length: > 0 } && State.EditingFilters is { } ef
                    && State.CustomFilterSlotIndex >= 0 && State.CustomFilterSlotIndex < ef.Count)
                {
                    ef[State.CustomFilterSlotIndex] = new InstalledFilter(text, ef[State.CustomFilterSlotIndex].Position);
                    State.FiltersDirty = true;
                }
                State.CustomFilterSlotIndex = -1;
                State.CustomFilterNameInput.Deactivate();
                return Task.CompletedTask;
            };
            State.CustomFilterNameInput.OnCancel = () =>
            {
                State.CustomFilterSlotIndex = -1;
            };

            return Layout.Builder.VStack([.. rows]).WStar();
        }

    }
}
