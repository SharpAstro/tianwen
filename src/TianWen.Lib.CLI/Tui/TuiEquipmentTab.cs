using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// TUI equipment/profile tab. Shows profile summary as markdown on the right,
/// interactive device settings list on the left, with site editing in the bottom bar.
/// </summary>
internal sealed class TuiEquipmentTab(
    GuiAppState appState,
    EquipmentTabState eqState,
    EquipmentContent equipmentContent,
    SignalBus? bus = null) : TuiTabBase
{
    private MarkdownWidget? _profileWidget;
    private ScrollableList<EquipmentFieldItem>? _settingsList;
    private TextBar? _siteBar;
    private TextBar? _statusBar;

    /// <summary>Tracks which field is active during site editing: 0=lat, 1=lon, 2=elev.</summary>
    private int _editFieldIndex;

    /// <summary>Selected field index in the settings list.</summary>
    private int _selectedFieldIndex;

    /// <summary>Total editable field count.</summary>
    private int _fieldCount;

    /// <summary>Last built items list (for keyboard lookup).</summary>
    private List<EquipmentFieldItem> _lastItems = [];

    /// <summary>Working copy of device URIs being edited (keyed by slot path).</summary>
    private readonly Dictionary<string, Uri> _editingUris = new Dictionary<string, Uri>();

    [MemberNotNullWhen(true, nameof(_profileWidget), nameof(_settingsList), nameof(_siteBar), nameof(_statusBar))]
    protected override bool IsReady =>
        _profileWidget is not null && _settingsList is not null && _siteBar is not null && _statusBar is not null;

    [MemberNotNull(nameof(_profileWidget), nameof(_settingsList), nameof(_siteBar), nameof(_statusBar))]
    protected override void CreateWidgets(Panel panel)
    {
        var bottomVp = panel.Dock(DockStyle.Bottom, 1);
        var siteVp = panel.Dock(DockStyle.Bottom, 1);
        var leftVp = panel.Dock(DockStyle.Left, 44);
        var fillVp = panel.Fill();

        _siteBar = new TextBar(siteVp);
        _statusBar = new TextBar(bottomVp);
        _profileWidget = new MarkdownWidget(leftVp);
        _settingsList = new ScrollableList<EquipmentFieldItem>(fillVp);

        panel.Add(_siteBar).Add(_statusBar).Add(_profileWidget).Add(_settingsList);
    }

    private static readonly string[] FieldLabels = ["Lat", "Lon", "Elev"];

    private TextInputState ActiveEditField => _editFieldIndex switch
    {
        0 => eqState.LatitudeInput,
        1 => eqState.LongitudeInput,
        _ => eqState.ElevationInput
    };

    protected override void RenderContent()
    {
        if (!IsReady)
        {
            return;
        }

        if (appState.ActiveProfile is { Data: { } data } profile)
        {
            // Right panel: markdown profile summary
            _profileWidget.Markdown(equipmentContent.FormatProfileMarkdown(profile));

            // Left panel: interactive settings list
            BuildSettingsList(data);

            // Site bar
            if (eqState.IsEditingSite)
            {
                var fields = new[] { eqState.LatitudeInput, eqState.LongitudeInput, eqState.ElevationInput };
                var parts = new string[3];
                for (var i = 0; i < 3; i++)
                {
                    parts[i] = FormatField(i, fields[i]);
                }
                _siteBar.Text($" {string.Join("  ", parts)}");
                _siteBar.RightText("Tab:next  Enter:save  Esc:cancel");
            }
            else
            {
                var siteLabel = equipmentContent.GetSiteLabel(data) ?? "not configured";
                _siteBar.Text($" Site: {siteLabel}");
                _siteBar.RightText("[E]dit site");
            }
        }
        else
        {
            _profileWidget.Markdown("## No profile selected");
            _settingsList.Items([]).Header("Equipment Settings");
            _siteBar.Text(" Site: \u2014");
            _siteBar.RightText("");
        }

        _statusBar.Text(" D:discover  E:edit site  \u2191\u2193:select  \u2190\u2192/Enter:adjust  R:refresh  Q:quit");
        _statusBar.RightText(appState.StatusMessage ?? "");
    }

    private void BuildSettingsList(ProfileData data)
    {
        var items = new List<EquipmentFieldItem>();
        var fieldIdx = 0;

        // Profile-level devices with settings
        AddDeviceSettings(items, ref fieldIdx, data.Guider, "Guider Settings");
        AddDeviceSettings(items, ref fieldIdx, data.GuiderCamera, "Guide Camera Settings");

        // Per-OTA devices with settings
        for (var i = 0; i < data.OTAs.Length; i++)
        {
            var ota = data.OTAs[i];
            AddDeviceSettings(items, ref fieldIdx, ota.Camera,
                data.OTAs.Length > 1 ? $"Camera Settings ({ota.Name})" : "Camera Settings");
        }

        _fieldCount = fieldIdx;
        _lastItems = items;
        _settingsList!.Items([.. items]).Header("Equipment Settings");

        // Scroll to keep selected item visible
        var selectedListIdx = items.FindIndex(i => i.IsSelected);
        if (selectedListIdx >= 0 && _settingsList.VisibleRows > 0)
        {
            _settingsList.ScrollTo(Math.Max(0, selectedListIdx - _settingsList.VisibleRows / 2));
        }
    }

    private void AddDeviceSettings(List<EquipmentFieldItem> items, ref int fieldIdx, Uri? deviceUri, string sectionLabel)
    {
        if (deviceUri is null)
        {
            return;
        }

        var device = EquipmentActions.TryDeviceFromUri(deviceUri);
        if (device is null || device.Settings.IsDefaultOrEmpty)
        {
            return;
        }

        // Use working copy of URI if we have one, otherwise the original
        var uriKey = deviceUri.GetLeftPart(System.UriPartial.Path);
        if (!_editingUris.TryGetValue(uriKey, out var workingUri))
        {
            workingUri = deviceUri;
            _editingUris[uriKey] = workingUri;
        }

        items.Add(new EquipmentFieldItem { SectionName = sectionLabel });

        foreach (var setting in device.Settings)
        {
            if (setting.IsVisible is { } predicate && predicate(workingUri) == false)
            {
                continue;
            }

            var capturedSetting = setting;
            var capturedKey = uriKey;
            var capturedIdx = fieldIdx;

            items.Add(new EquipmentFieldItem
            {
                Setting = setting,
                DeviceUri = workingUri,
                FieldIndex = fieldIdx,
                IsSelected = fieldIdx == _selectedFieldIndex,
                Increment = () =>
                {
                    var current = _editingUris[capturedKey];
                    _editingUris[capturedKey] = capturedSetting.Increment(current);
                    SaveDeviceSettings(capturedKey);
                },
                Decrement = capturedSetting.Decrement is not null
                    ? () =>
                    {
                        var current = _editingUris[capturedKey];
                        if (capturedSetting.Decrement(current) is { } decremented)
                        {
                            _editingUris[capturedKey] = decremented;
                            SaveDeviceSettings(capturedKey);
                        }
                    }
                    : null,
            });
            fieldIdx++;
        }
    }

    private void SaveDeviceSettings(string uriKey)
    {
        if (appState.ActiveProfile?.Data is not { } data)
        {
            return;
        }

        var newUri = _editingUris[uriKey];

        // Find the original URI in the profile and replace it
        var originalUri = FindOriginalUri(data, uriKey);
        if (originalUri is null)
        {
            return;
        }

        var updatedData = EquipmentActions.UpdateDeviceUri(data, originalUri, newUri);
        bus?.Post(new UpdateProfileSignal(updatedData));
    }

    private static Uri? FindOriginalUri(ProfileData data, string uriKey)
    {
        if (data.Guider is { } g && g.GetLeftPart(System.UriPartial.Path) == uriKey)
        {
            return g;
        }

        if (data.GuiderCamera is { } gc && gc.GetLeftPart(System.UriPartial.Path) == uriKey)
        {
            return gc;
        }

        for (var i = 0; i < data.OTAs.Length; i++)
        {
            if (data.OTAs[i].Camera is { } cam && cam.GetLeftPart(System.UriPartial.Path) == uriKey)
            {
                return cam;
            }
        }

        return null;
    }

    private string FormatField(int index, TextInputState field)
    {
        var label = FieldLabels[index];
        var value = field.Text;
        if (_editFieldIndex != index)
        {
            return $"{label}: {(value.Length > 0 ? value : "...")}";
        }

        var pos = Math.Clamp(field.CursorPos, 0, value.Length);
        var before = value[..pos];
        var cursorChar = pos < value.Length ? value[pos].ToString() : " ";
        var after = pos < value.Length ? value[(pos + 1)..] : "";
        return $"{label}: [{before}{VtStyle.ReverseOn}{cursorChar}{VtStyle.ReverseOff}{after}]";
    }

    public override bool HandleInput(InputEvent evt)
    {
        if (evt is not InputEvent.KeyDown(var key, var modifiers))
        {
            return false;
        }

        // Site editing mode
        if (eqState.IsEditingSite)
        {
            return HandleSiteEditInput(key, modifiers);
        }

        switch (key)
        {
            case InputKey.D:
                bus?.Post(new DiscoverDevicesSignal(IncludeFake: (modifiers & InputModifier.Shift) != 0));
                NeedsRedraw = true;
                return false;

            case InputKey.E:
                _editFieldIndex = 0;
                if (appState.ActiveProfile?.Data is { } pd)
                {
                    var site = pd.Mount is { } mount ? EquipmentActions.GetSiteFromMount(mount) : null;
                    if (site.HasValue)
                    {
                        eqState.LatitudeInput.Activate(site.Value.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        eqState.LongitudeInput.Activate(site.Value.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        eqState.ElevationInput.Activate(site.Value.Elev?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
                    }
                    else
                    {
                        eqState.LatitudeInput.Activate("");
                        eqState.LongitudeInput.Activate("");
                        eqState.ElevationInput.Activate("");
                    }
                }
                eqState.IsEditingSite = true;
                NeedsRedraw = true;
                return false;

            case InputKey.Up:
                if (_selectedFieldIndex > 0)
                {
                    _selectedFieldIndex--;
                    NeedsRedraw = true;
                }
                return false;

            case InputKey.Down:
                if (_selectedFieldIndex < _fieldCount - 1)
                {
                    _selectedFieldIndex++;
                    NeedsRedraw = true;
                }
                return false;

            case InputKey.Left:
                if (FindSelectedItem() is { Decrement: { } dec })
                {
                    dec();
                    NeedsRedraw = true;
                }
                return false;

            case InputKey.Right:
            case InputKey.Enter:
                if (FindSelectedItem() is { Increment: { } inc })
                {
                    inc();
                    NeedsRedraw = true;
                }
                return false;

            case InputKey.R:
                _editingUris.Clear();
                NeedsRedraw = true;
                return false;
        }

        return false;
    }

    private EquipmentFieldItem? FindSelectedItem()
    {
        var idx = _selectedFieldIndex;
        return idx >= 0 ? _lastItems.Find(i => i.FieldIndex == idx) : null;
    }

    private bool HandleSiteEditInput(InputKey key, InputModifier modifiers)
    {
        // Tab cycles fields
        if (key == InputKey.Tab)
        {
            _editFieldIndex = (_editFieldIndex + 1) % 3;
            NeedsRedraw = true;
            return false;
        }

        var field = ActiveEditField;

        // Delegate to TextInputState via the upstream key routing
        if (key.ToTextInputKey(modifiers) is { } textKey)
        {
            field.HandleKey(textKey);
            NeedsRedraw = true;

            if (field.IsCommitted)
            {
                field.IsCommitted = false;
                _ = field.OnCommit?.Invoke(field.Text);
            }
            else if (field.IsCancelled)
            {
                field.IsCancelled = false;
                field.OnCancel?.Invoke();
            }
            return false;
        }

        // Printable character input (uses upstream InputKeyCharMapping)
        if (key.ToChar(modifiers) is { } ch)
        {
            field.InsertText(ch.ToString());
            NeedsRedraw = true;
        }

        return false;
    }
}
