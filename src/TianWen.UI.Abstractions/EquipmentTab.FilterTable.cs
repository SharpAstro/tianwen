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

        private float RenderFilterTable(
            GuiAppState appState,
            int otaIndex, ProfileData pd,
            float x, float cursor, float w, float itemH,
            float dpiScale, string fontPath, float fontSize, float padding, float buttonH)
        {
            var savedFilters = EquipmentActions.GetFilterConfig(pd, otaIndex);
            var isExpanded = State.ExpandedFilterOtaIndex == otaIndex;
            var rowH = itemH * 0.9f;
            var capturedOtaIdx = otaIndex;

            // Toggle header -- clicking expands/collapses and loads/discards editing state
            var headerLabel = isExpanded
                ? $"    Filters ({savedFilters.Count}) [-]"
                : $"    Filters ({savedFilters.Count}) [+]";
            var toggleHeader = FormRowLayout.ToggleHeaderRow(
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
            RenderLayout(toggleHeader, new RectF32(x + padding, cursor, w - padding * 2f, rowH), fontPath, dpiScale);
            cursor += rowH;

            if (!isExpanded || State.EditingFilters is not { } filters)
            {
                return cursor;
            }

            // Layout columns: [#][Name                ][- offset +]
            var slotNumW = 20f * dpiScale;
            var contentW = w - padding * 4f;
            var offsetGroupW = 100f * dpiScale;  // [-] value [+] grouped
            var nameColW = contentW - slotNumW - offsetGroupW;
            var colStartX = x + padding * 2f;

            // Column headers as one HStack (lead/trail pad keeps the columns aligned with the rows below).
            var header = Layout.Builder.HStack(
                    Layout.Builder.Spacer().WFixed(BasePadding).HStar(),
                    Layout.Builder.Text("#", BaseFontSize * 0.75f, DimText, TextAlign.Center, TextAlign.Center).WFixed(20f).HStar(),
                    Layout.Builder.Text("Name", BaseFontSize * 0.75f, DimText).WStar().HStar(),
                    Layout.Builder.Text("Offset", BaseFontSize * 0.75f, DimText, TextAlign.Center, TextAlign.Center).WFixed(100f).HStar(),
                    Layout.Builder.Spacer().WFixed(BasePadding).HStar())
                .RowH(BaseItemHeight * 0.9f).Bg(FilterTableBg);
            RenderLayout(header, new RectF32(x + padding, cursor, w - padding * 2f, rowH), fontPath, dpiScale);
            cursor += rowH;

            // Filter rows: [pad | # | name | [- offset +] | pad], one tree each. Was a FillRect + slot
            // DrawText + name button/input + three offset controls at hand-computed x per row.
            for (var f = 0; f < filters.Count; f++)
            {
                var filter = filters[f];
                var rowBg = f % 2 == 0 ? FilterTableBg : FilterRowAlt;
                var capturedF = f;
                // Dropdown anchors below the name cell -- the lead pad puts the name column at colStartX + slotNumW.
                var nameCellX = colStartX + slotNumW;
                var nameCellY = cursor;

                Layout.Node nameCell = State.CustomFilterSlotIndex == f
                    ? Layout.Builder.Fill(key: $"filterName{f}")
                    : Layout.Builder.Text(EquipmentActions.FilterDisplayName(filter), BaseFontSize * 0.8f, BodyText)
                        .Clickable(new HitResult.ButtonHit($"FilterName{otaIndex}_{f}"), _ =>
                        {
                            var existingCustom = capturedF < filters.Count ? filters[capturedF].CustomName : null;
                            State.FilterNameDropdown.Open(
                                nameCellX, nameCellY + rowH, nameColW,
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

                var row = Layout.Builder.HStack(
                        Layout.Builder.Spacer().WFixed(BasePadding).HStar(),
                        Layout.Builder.Text((f + 1).ToString(), BaseFontSize * 0.8f, DimText, TextAlign.Center, TextAlign.Center).WFixed(20f).HStar(),
                        nameCell.WStar().HStar(),
                        offsetGroup,
                        Layout.Builder.Spacer().WFixed(BasePadding).HStar())
                    .RowH(BaseItemHeight * 0.9f).Bg(rowBg);
                RenderLayout(row, new RectF32(x + padding, cursor, w - padding * 2f, rowH), fontPath, dpiScale,
                    drawFill: (fill, r) =>
                    {
                        if (fill.Key == $"filterName{capturedF}")
                            RenderTextInput(State.CustomFilterNameInput, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, fontSize * 0.8f);
                    });
                cursor += rowH;
            }

            // Save / Cancel buttons (only shown when dirty)
            if (State.FiltersDirty)
            {
                var saveBtnW = Renderer.MeasureText("Save".AsSpan(), fontPath, fontSize * 0.85f).Width + padding * 3f;
                var cancelBtnW = Renderer.MeasureText("Cancel".AsSpan(), fontPath, fontSize * 0.85f).Width + padding * 3f;

                RenderButton("Save", x + padding * 2f, cursor, saveBtnW, buttonH * 0.85f, fontPath, fontSize * 0.85f, CreateButton, BodyText, $"SaveFilters{otaIndex}",
                    _ =>
                    {
                        if (appState.ActiveProfile is { } prof && prof.Data is { } data && State.EditingFilters is { } editFilters)
                        {
                            var newData = EquipmentActions.SetFilterConfig(data, capturedOtaIdx, editFilters);
                            PostSignal(new UpdateProfileSignal(newData));
                            State.FiltersDirty = false;
                        }
                    });

                RenderButton("Cancel", x + padding * 2f + saveBtnW + padding, cursor, cancelBtnW, buttonH * 0.85f, fontPath, fontSize * 0.85f, EditButtonBg, BodyText, $"CancelFilters{otaIndex}",
                    _ =>
                    {
                        // Reload from profile
                        State.BeginEditingFilters(savedFilters);
                    });

                cursor += buttonH * 0.85f + padding / 2f;
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

            return cursor;
        }

    }
}
