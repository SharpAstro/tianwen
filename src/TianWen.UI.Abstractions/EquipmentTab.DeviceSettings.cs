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
    /// Generic expandable device-settings pane: DeviceSettingDescriptor rows (toggle / cycle /
    /// stepper / string editor) with the collapsed-by-default Advanced sub-section.
    /// </summary>
    partial class EquipmentTab<TSurface>
    {
        // -----------------------------------------------------------------------
        // Generic device settings
        // -----------------------------------------------------------------------

        /// <summary>
        /// Renders device settings for a device URI if the device declares any <see cref="DeviceSettingDescriptor"/>s.
        /// Returns the updated cursor Y. No-ops if the device has no settings.
        /// </summary>
        private float RenderDeviceSettingsIfAny(
            GuiAppState appState,
            ProfileData pd,
            Uri? deviceUri,
            string sectionLabel,
            float x, float cursor, float w, float itemH,
            float dpiScale, string fontPath, float fontSize, float padding)
        {
            if (deviceUri is null || deviceUri == NoneDevice.Instance.DeviceUri)
            {
                return cursor;
            }

            var device = EquipmentActions.TryDeviceFromUri(deviceUri);
            if (device is null || device.Settings.IsDefaultOrEmpty)
            {
                return cursor;
            }

            return RenderDeviceSettings(appState, pd, deviceUri, device.Settings, sectionLabel,
                x, cursor, w, itemH, dpiScale, fontPath, fontSize, padding);
        }

        /// <summary>
        /// Renders the expandable settings pane for a device. Iterates <see cref="DeviceSettingDescriptor"/>s
        /// and dispatches on <see cref="DeviceSettingKind"/> to render the appropriate control.
        /// </summary>
        private float RenderDeviceSettings(
            GuiAppState appState,
            ProfileData pd,
            Uri savedDeviceUri,
            ImmutableArray<DeviceSettingDescriptor> settings,
            string sectionLabel,
            float x, float cursor, float w, float itemH,
            float dpiScale, string fontPath, float fontSize, float padding)
        {
            var deviceKey = savedDeviceUri.GetLeftPart(UriPartial.Path);
            var isExpanded = State.ExpandedDeviceSettingsUri == deviceKey;
            var rowH = itemH * 0.9f;

            // Toggle header
            var headerLabel = isExpanded ? $"    {sectionLabel} [-]" : $"    {sectionLabel} [+]";
            var devToggle = FormRowLayout.ToggleHeaderRow(
                headerLabel, rowH, FilterTableBg, HeaderText, BaseFontSize * 0.85f,
                new HitResult.ButtonHit($"Toggle_{deviceKey}"),
                _ =>
                {
                    if (isExpanded)
                    {
                        State.StopEditingDeviceSettings();
                    }
                    else
                    {
                        State.BeginEditingDeviceSettings(savedDeviceUri);
                    }
                });
            RenderLayout(devToggle, new RectF32(x + padding, cursor, w - padding * 2f, rowH), fontPath, dpiScale);
            cursor += rowH;

            if (!isExpanded || State.EditingDeviceUri is not { } editingUri)
            {
                return cursor;
            }

            var labelW = w * 0.45f;
            var controlX = x + padding * 2f + labelW;
            var controlW = w - padding * 4f - labelW;
            var labelX = x + padding * 2f;
            var stepBtnW = 24f * dpiScale;

            // Local row renderer shared by the basic pass and the advanced sub-section.
            var rowIndex = 0;
            float RenderSettingRow(DeviceSettingDescriptor desc, float rowCursor)
            {
                var rowBg = rowIndex++ % 2 == 0 ? FilterTableBg : FilterRowAlt;
                FillRect(x + padding, rowCursor, w - padding * 2f, rowH, rowBg);

                // StringEditor uses a narrower label to give more space to the text input
                var rowLabelW = desc.Kind == DeviceSettingKind.StringEditor ? w * 0.25f : labelW;
                var rowControlX = x + padding * 2f + rowLabelW;
                var rowControlW = w - padding * 4f - rowLabelW;
                DrawText($"{desc.Label}:".AsSpan(), fontPath, labelX, rowCursor, rowLabelW, rowH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);

                var capturedDesc = desc;
                switch (desc.Kind)
                {
                    case DeviceSettingKind.BoolToggle:
                    case DeviceSettingKind.EnumCycle:
                    {
                        var valueLabel = desc.FormatValue(editingUri);
                        RenderButton(valueLabel, controlX, rowCursor, controlW, rowH, fontPath, fontSize * 0.85f, EditButtonBg, BodyText, $"Cycle_{desc.Key}",
                            _ =>
                            {
                                State.EditingDeviceUri = capturedDesc.Increment(editingUri);
                                State.DeviceSettingsDirty = true;
                            });
                        break;
                    }

                    case DeviceSettingKind.IntStepper:
                    case DeviceSettingKind.FloatStepper:
                    case DeviceSettingKind.PercentStepper:
                    {
                        // [-] button
                        if (desc.Decrement is { } decrement)
                        {
                            RenderButton("-", controlX, rowCursor, stepBtnW, rowH, fontPath, fontSize * 0.85f, EditButtonBg, BodyText, $"Dec_{desc.Key}",
                                _ =>
                                {
                                    State.EditingDeviceUri = decrement(editingUri);
                                    State.DeviceSettingsDirty = true;
                                });
                        }

                        // Value label
                        var valueText = desc.FormatValue(editingUri);
                        var valueW = controlW - stepBtnW * 2f;
                        DrawText(valueText.AsSpan(), fontPath, controlX + stepBtnW, rowCursor, valueW, rowH, fontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center);

                        // [+] button
                        RenderButton("+", controlX + controlW - stepBtnW, rowCursor, stepBtnW, rowH, fontPath, fontSize * 0.85f, EditButtonBg, BodyText, $"Inc_{desc.Key}",
                            _ =>
                            {
                                State.EditingDeviceUri = capturedDesc.Increment(editingUri);
                                State.DeviceSettingsDirty = true;
                            });
                        break;
                    }

                    case DeviceSettingKind.StringEditor:
                    {
                        var isEditing = State.EditingStringSettingKey == desc.Key;
                        if (isEditing)
                        {
                            // Active text input
                            if (desc.Placeholder is { } placeholder)
                            {
                                State.StringSettingInput.Placeholder = placeholder;
                            }
                            RenderTextInput(State.StringSettingInput, (int)rowControlX, (int)rowCursor, (int)rowControlW, (int)rowH, fontPath, fontSize * 0.85f);
                        }
                        else
                        {
                            // Display current value (masked if configured) with click-to-edit
                            var rawValue = desc.FormatValue(editingUri);
                            var displayValue = desc.Mask && rawValue.Length > 0
                                ? new string('*', Math.Min(rawValue.Length, 8)) + rawValue[Math.Max(0, rawValue.Length - 4)..]
                                : rawValue;
                            if (displayValue.Length == 0)
                            {
                                displayValue = desc.Placeholder ?? "(empty)";
                            }
                            RenderButton(displayValue, rowControlX, rowCursor, rowControlW, rowH, fontPath, fontSize * 0.85f, EditButtonBg, BodyText, $"Edit_{desc.Key}",
                                _ =>
                                {
                                    State.EditingStringSettingKey = capturedDesc.Key;
                                    State.StringSettingInput.Activate(capturedDesc.FormatValue(editingUri));
                                });
                        }
                        break;
                    }
                }

                return rowCursor + rowH;
            }

            // Basic rows first; advanced rows are deferred to a collapsed-by-default sub-section.
            var visibleAdvancedCount = 0;
            for (var i = 0; i < settings.Length; i++)
            {
                var desc = settings[i];

                // Conditional visibility
                if (desc.IsVisible is { } isVisible && !isVisible(editingUri))
                {
                    continue;
                }
                if (desc.IsAdvanced)
                {
                    visibleAdvancedCount++;
                    continue;
                }

                cursor = RenderSettingRow(desc, cursor);
            }

            // Advanced sub-section: expert knobs with sensible defaults, collapsed by default.
            if (visibleAdvancedCount > 0)
            {
                var advancedExpanded = State.AdvancedDeviceSettingsExpanded;
                var advancedLabel = advancedExpanded ? "      Advanced [-]" : "      Advanced [+]";
                var advancedToggle = FormRowLayout.ToggleHeaderRow(
                    advancedLabel, rowH, FilterTableBg, DimText, BaseFontSize * 0.85f,
                    new HitResult.ButtonHit($"ToggleAdvanced_{deviceKey}"),
                    _ => State.AdvancedDeviceSettingsExpanded = !advancedExpanded);
                RenderLayout(advancedToggle, new RectF32(x + padding, cursor, w - padding * 2f, rowH), fontPath, dpiScale);
                cursor += rowH;

                if (advancedExpanded)
                {
                    for (var i = 0; i < settings.Length; i++)
                    {
                        var desc = settings[i];
                        if (!desc.IsAdvanced || (desc.IsVisible is { } isVisible && !isVisible(editingUri)))
                        {
                            continue;
                        }

                        cursor = RenderSettingRow(desc, cursor);
                    }
                }
            }

            // Commit any active string editor when focus moves away
            if (State.EditingStringSettingKey is { } activeStringKey
                && State.StringSettingInput is { IsActive: false } stringInput)
            {
                State.EditingDeviceUri = DeviceSettingHelper.WithQueryParam(editingUri, activeStringKey, stringInput.Text);
                State.DeviceSettingsDirty = true;
                State.EditingStringSettingKey = null;
            }

            // Save / Cancel buttons (only when dirty)
            if (State.DeviceSettingsDirty)
            {
                var btnW = 60f * dpiScale;
                var gap = padding;
                var saveBtnX = x + w - padding - btnW * 2f - gap;
                var cancelBtnX = x + w - padding - btnW;

                RenderButton("Save", saveBtnX, cursor, btnW, rowH, fontPath, fontSize * 0.85f, CreateButton, BodyText, $"SaveSettings_{deviceKey}",
                    _ =>
                    {
                        if (appState.ActiveProfile is { Data: { } data } && State.EditingDeviceUri is { } newUri)
                        {
                            var newData = EquipmentActions.UpdateDeviceUri(data, savedDeviceUri, newUri);
                            PostSignal(new UpdateProfileSignal(newData));
                            State.BeginEditingDeviceSettings(newUri);
                        }
                    });

                RenderButton("Cancel", cancelBtnX, cursor, btnW, rowH, fontPath, fontSize * 0.85f, EditButtonBg, DimText, $"CancelSettings_{deviceKey}",
                    _ =>
                    {
                        State.BeginEditingDeviceSettings(savedDeviceUri);
                    });

                cursor += rowH;
            }

            return cursor;
        }

    }
}
