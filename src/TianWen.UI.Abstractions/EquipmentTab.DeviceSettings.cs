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

            // Local row renderer shared by the basic pass and the advanced sub-section. Each row is one
            // tree: [pad | label | control | pad] with an alternating background. The control is a cycle
            // button, a [- value +] stepper, or a string editor (inline text-input Fill / click-to-edit
            // button) -- was ~90 lines of FillRect + per-control DrawText/RenderButton at computed x.
            var rowIndex = 0;
            float RenderSettingRow(DeviceSettingDescriptor desc, float rowCursor)
            {
                var rowBg = rowIndex++ % 2 == 0 ? FilterTableBg : FilterRowAlt;
                var capturedDesc = desc;
                // StringEditor uses a narrower label to give the text input more room.
                var labelWeight = desc.Kind == DeviceSettingKind.StringEditor ? 0.25f : 0.45f;

                Layout.Node Btn(string label, string action, RGBAColor32 bg, Action<InputModifier> onClick) =>
                    Layout.Builder.Text(label, BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center)
                        .Bg(bg).Clickable(new HitResult.ButtonHit(action), onClick);

                Layout.Node control;
                switch (desc.Kind)
                {
                    case DeviceSettingKind.BoolToggle:
                    case DeviceSettingKind.EnumCycle:
                        control = Btn(desc.FormatValue(editingUri), $"Cycle_{desc.Key}", EditButtonBg,
                            _ => { State.EditingDeviceUri = capturedDesc.Increment(editingUri); State.DeviceSettingsDirty = true; });
                        break;

                    case DeviceSettingKind.IntStepper:
                    case DeviceSettingKind.FloatStepper:
                    case DeviceSettingKind.PercentStepper:
                        control = Layout.Builder.HStack(
                            desc.Decrement is { } decrement
                                ? Btn("-", $"Dec_{desc.Key}", EditButtonBg, _ => { State.EditingDeviceUri = decrement(editingUri); State.DeviceSettingsDirty = true; }).WFixed(24f).HStar()
                                : Layout.Builder.Spacer().WFixed(24f).HStar(),
                            Layout.Builder.Text(desc.FormatValue(editingUri), BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center).Stretch(),
                            Btn("+", $"Inc_{desc.Key}", EditButtonBg, _ => { State.EditingDeviceUri = capturedDesc.Increment(editingUri); State.DeviceSettingsDirty = true; }).WFixed(24f).HStar());
                        break;

                    case DeviceSettingKind.StringEditor when State.EditingStringSettingKey == desc.Key:
                        if (desc.Placeholder is { } placeholder) State.StringSettingInput.Placeholder = placeholder;
                        control = Layout.Builder.Fill(key: $"setting:{desc.Key}");
                        break;

                    case DeviceSettingKind.StringEditor:
                    {
                        var rawValue = desc.FormatValue(editingUri);
                        var displayValue = desc.Mask && rawValue.Length > 0
                            ? new string('*', Math.Min(rawValue.Length, 8)) + rawValue[Math.Max(0, rawValue.Length - 4)..]
                            : rawValue;
                        if (displayValue.Length == 0) displayValue = desc.Placeholder ?? "(empty)";
                        control = Btn(displayValue, $"Edit_{desc.Key}", EditButtonBg,
                            _ => { State.EditingStringSettingKey = capturedDesc.Key; State.StringSettingInput.Activate(capturedDesc.FormatValue(editingUri)); });
                        break;
                    }

                    default:
                        control = Layout.Builder.Spacer();
                        break;
                }

                var row = Layout.Builder.HStack(
                        Layout.Builder.Spacer().WFixed(BasePadding).HStar(),
                        Layout.Builder.Text($"{desc.Label}:", BaseFontSize * 0.85f, DimText).WStar(labelWeight).HStar(),
                        control.WStar(1f - labelWeight).HStar(),
                        Layout.Builder.Spacer().WFixed(BasePadding).HStar())
                    .RowH(BaseItemHeight * 0.9f).Bg(rowBg);
                RenderLayout(row, new RectF32(x + padding, rowCursor, w - padding * 2f, rowH), fontPath, dpiScale,
                    drawFill: (fill, r) =>
                    {
                        if (fill.Key == $"setting:{capturedDesc.Key}")
                            RenderTextInput(State.StringSettingInput, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, fontSize * 0.85f);
                    });
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
