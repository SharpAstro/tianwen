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
        /// Builds device settings for a device URI as one profile-panel section node if the device
        /// declares any <see cref="DeviceSettingDescriptor"/>s (else null = no section).
        /// </summary>
        private Layout.Node? BuildDeviceSettingsIfAny(
            GuiAppState appState, ProfileData pd, Uri? deviceUri, string sectionLabel,
            float dpiScale, string fontPath)
        {
            if (deviceUri is null || deviceUri == NoneDevice.Instance.DeviceUri)
            {
                return null;
            }

            var device = EquipmentActions.TryDeviceFromUri(deviceUri);
            if (device is null || device.Settings.IsDefaultOrEmpty)
            {
                return null;
            }

            return BuildDeviceSettings(appState, pd, deviceUri, device.Settings, sectionLabel, dpiScale, fontPath);
        }

        /// <summary>
        /// Builds the expandable settings pane for a device as one section node. Iterates
        /// <see cref="DeviceSettingDescriptor"/>s and dispatches on <see cref="DeviceSettingKind"/> to
        /// the appropriate control. String-editor inputs are keyed <c>Fill</c> leaves painted through
        /// <see cref="_profilePanelFills"/> from the single panel RenderLayout.
        /// </summary>
        private Layout.Node BuildDeviceSettings(
            GuiAppState appState, ProfileData pd, Uri savedDeviceUri,
            ImmutableArray<DeviceSettingDescriptor> settings, string sectionLabel,
            float dpiScale, string fontPath)
        {
            var deviceKey = savedDeviceUri.GetLeftPart(UriPartial.Path);
            var isExpanded = State.ExpandedDeviceSettingsUri == deviceKey;
            var fontSize = BaseFontSize * dpiScale;
            float rowH = BaseItemHeight * 0.9f;   // design units

            // Toggle header
            var headerLabel = isExpanded ? $"    {sectionLabel} [-]" : $"    {sectionLabel} [+]";
            var toggle = FormRowLayout.ToggleHeaderRow(
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

            if (!isExpanded || State.EditingDeviceUri is not { } editingUri)
            {
                return toggle;
            }

            var rows = new List<Layout.Node> { toggle };

            // Local row builder shared by the basic pass and the advanced sub-section. Each row is one
            // tree: [pad | label | control | pad] with an alternating background. The control is a cycle
            // button, a [- value +] stepper, or a string editor (inline text-input Fill / click-to-edit button).
            var rowIndex = 0;
            Layout.Node SettingRow(DeviceSettingDescriptor desc)
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
                        var editFillKey = $"setting:{desc.Key}";
                        _profilePanelFills[editFillKey] =
                            r => RenderTextInput(State.StringSettingInput, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, fontSize * 0.85f);
                        control = Layout.Builder.Fill(key: editFillKey);
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

                return Layout.Builder.HStack(
                        Layout.Builder.Spacer().WFixed(BasePadding).HStar(),
                        Layout.Builder.Text($"{desc.Label}:", BaseFontSize * 0.85f, DimText).WStar(labelWeight).HStar(),
                        control.WStar(1f - labelWeight).HStar(),
                        Layout.Builder.Spacer().WFixed(BasePadding).HStar())
                    .RowH(rowH).Bg(rowBg);
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

                rows.Add(SettingRow(desc));
            }

            // Advanced sub-section: expert knobs with sensible defaults, collapsed by default.
            if (visibleAdvancedCount > 0)
            {
                var advancedExpanded = State.AdvancedDeviceSettingsExpanded;
                var advancedLabel = advancedExpanded ? "      Advanced [-]" : "      Advanced [+]";
                rows.Add(FormRowLayout.ToggleHeaderRow(
                    advancedLabel, rowH, FilterTableBg, DimText, BaseFontSize * 0.85f,
                    new HitResult.ButtonHit($"ToggleAdvanced_{deviceKey}"),
                    _ => State.AdvancedDeviceSettingsExpanded = !advancedExpanded));

                if (advancedExpanded)
                {
                    for (var i = 0; i < settings.Length; i++)
                    {
                        var desc = settings[i];
                        if (!desc.IsAdvanced || (desc.IsVisible is { } isVisible && !isVisible(editingUri)))
                        {
                            continue;
                        }

                        rows.Add(SettingRow(desc));
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

            // Save / Cancel buttons (only when dirty), right-aligned.
            if (State.DeviceSettingsDirty)
            {
                Layout.Node ActionBtn(string label, RGBAColor32 bg, RGBAColor32 textColor, string action, Action<InputModifier> onClick) =>
                    Layout.Builder.Text(label, BaseFontSize * 0.85f, textColor, TextAlign.Center, TextAlign.Center)
                        .WFixed(60f).HStar().Bg(bg).Clickable(new HitResult.ButtonHit(action), onClick);

                rows.Add(Layout.Builder.HStack(
                        Layout.Builder.Spacer().Stretch(),
                        ActionBtn("Save", CreateButton, BodyText, $"SaveSettings_{deviceKey}",
                            _ =>
                            {
                                if (appState.ActiveProfile is { Data: { } data } && State.EditingDeviceUri is { } newUri)
                                {
                                    var newData = EquipmentActions.UpdateDeviceUri(data, savedDeviceUri, newUri);
                                    PostSignal(new UpdateProfileSignal(newData));
                                    State.BeginEditingDeviceSettings(newUri);
                                }
                            }),
                        Layout.Builder.Spacer().WFixed(BasePadding).HStar(),
                        ActionBtn("Cancel", EditButtonBg, DimText, $"CancelSettings_{deviceKey}",
                            _ => State.BeginEditingDeviceSettings(savedDeviceUri)),
                        Layout.Builder.Spacer().WFixed(BasePadding).HStar())
                    .RowH(rowH));
            }

            return Layout.Builder.VStack([.. rows]).WStar();
        }

    }
}
