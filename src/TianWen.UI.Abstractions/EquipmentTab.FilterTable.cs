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
            var stepBtnW = 24f * dpiScale;
            var offsetValueW = offsetGroupW - stepBtnW * 2f;
            var colStartX = x + padding * 2f;

            // Column headers
            FillRect(x + padding, cursor, w - padding * 2f, rowH, FilterTableBg);
            DrawText("#".AsSpan(), fontPath, colStartX, cursor, slotNumW, rowH, fontSize * 0.75f, DimText, TextAlign.Center, TextAlign.Center);
            DrawText("Name".AsSpan(), fontPath, colStartX + slotNumW, cursor, nameColW, rowH, fontSize * 0.75f, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("Offset".AsSpan(), fontPath, colStartX + slotNumW + nameColW, cursor, offsetGroupW, rowH, fontSize * 0.75f, DimText, TextAlign.Center, TextAlign.Center);
            cursor += rowH;

            // Filter rows
            for (var f = 0; f < filters.Count; f++)
            {
                var filter = filters[f];
                var rowBg = f % 2 == 0 ? FilterTableBg : FilterRowAlt;
                FillRect(x + padding, cursor, w - padding * 2f, rowH, rowBg);

                // Slot number
                DrawText(
                    (f + 1).ToString().AsSpan(),
                    fontPath,
                    colStartX, cursor, slotNumW, rowH,
                    fontSize * 0.8f, DimText, TextAlign.Center, TextAlign.Center);

                // Filter name -- inline text input if custom editing, otherwise clickable to open dropdown
                var capturedF = f;
                var nameCellX = colStartX + slotNumW;
                var nameCellY = cursor;

                if (State.CustomFilterSlotIndex == f)
                {
                    RenderTextInput(State.CustomFilterNameInput, (int)nameCellX, (int)cursor, (int)nameColW, (int)rowH, fontPath, fontSize * 0.8f);
                }
                else
                {
                RenderButton(EquipmentActions.FilterDisplayName(filter), nameCellX, cursor, nameColW, rowH, fontPath, fontSize * 0.8f, rowBg, BodyText, $"FilterName{otaIndex}_{f}",
                    _ =>
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
                                // Preserve existing custom name if re-selecting Custom...
                                var preservedName = capturedF < filters.Count && filters[capturedF].CustomName is { } cn ? cn : "";
                                State.CustomFilterNameInput.Text = preservedName;
                                State.CustomFilterNameInput.CursorPos = preservedName.Length;
                                // Signal deferred text input activation (processed in OnPostFrame)
                                PostSignal(new ActivateTextInputSignal(State.CustomFilterNameInput));
                            },
                            customEntryLabel: existingCustom is { Length: > 0 } ? $"Custom: {existingCustom}" : null);
                    });
                }

                // Offset group: [-] value [+]
                var offsetX = colStartX + slotNumW + nameColW;
                var offsetStr = filter.Position >= 0 ? $"+{filter.Position}" : filter.Position.ToString();

                RenderButton("-", offsetX, cursor, stepBtnW, rowH, fontPath, fontSize * 0.8f, EditButtonBg, BodyText, $"FilterOffDec{otaIndex}_{f}",
                    _ =>
                    {
                        if (capturedF < filters.Count)
                        {
                            var cur = filters[capturedF];
                            filters[capturedF] = new InstalledFilter(cur.Filter.Name, cur.Position - 1);
                            State.FiltersDirty = true;
                        }
                    });

                DrawText(
                    offsetStr.AsSpan(),
                    fontPath,
                    offsetX + stepBtnW, cursor, offsetValueW, rowH,
                    fontSize * 0.8f, BodyText, TextAlign.Center, TextAlign.Center);

                RenderButton("+", offsetX + stepBtnW + offsetValueW, cursor, stepBtnW, rowH, fontPath, fontSize * 0.8f, EditButtonBg, BodyText, $"FilterOffInc{otaIndex}_{f}",
                    _ =>
                    {
                        if (capturedF < filters.Count)
                        {
                            var cur = filters[capturedF];
                            filters[capturedF] = new InstalledFilter(cur.Filter.Name, cur.Position + 1);
                            State.FiltersDirty = true;
                        }
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
