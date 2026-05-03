using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui;

/// <summary>
/// TUI equipment/profile tab. Left: profile picker. Right: unified scrollable list
/// with all profile elements (device slots, OTA properties, filters, device settings).
/// In assignment mode the settings list is replaced by the device picker.
/// </summary>
internal sealed class TuiEquipmentTab(
    GuiAppState appState,
    EquipmentTabState eqState,
    EquipmentContent equipmentContent,
    IConsoleHost consoleHost,
    SignalBus? bus = null) : TuiTabBase
{
    /// <summary>Interaction mode state machine.</summary>
    private enum Mode { Browse, Assignment, InlineEdit }

    private ScrollableList<ProfilePickerItem>? _profileList;
    private ScrollableList<EquipmentFieldItem>? _settingsList;
    private TextBar? _siteBar;
    private TextBar? _statusBar;

    private Mode _mode = Mode.Browse;

    /// <summary>Tracks which field is active during site editing: 0=lat, 1=lon, 2=elev.</summary>
    private int _editFieldIndex;

    /// <summary>OTA index pending deletion confirmation (-1 = none).</summary>
    private int _pendingDeleteOtaIndex = -1;

    /// <summary>Last built items list (for keyboard lookup).</summary>
    private List<EquipmentFieldItem> _lastItems = [];

    /// <summary>Working copy of device URIs being edited (keyed by slot path).</summary>
    private readonly Dictionary<string, Uri> _editingUris = new Dictionary<string, Uri>();

    /// <summary>Active inline text input (filter name editing).</summary>
    private TextInputState? _activeInlineInput;

    /// <summary>Cached profile list for the picker.</summary>
    private IReadOnlyCollection<Profile> _cachedProfiles = [];

    /// <summary>Whether profiles have been loaded at least once.</summary>
    private bool _profilesLoaded;

    /// <summary>The mode the settings list was last built for. Used to reset the cursor on mode transitions.</summary>
    private Mode _lastBuiltMode = Mode.Browse;

    /// <summary>Saved settings-list cursor row index, captured when entering Assignment mode and restored on exit.</summary>
    private int _savedBrowseCursor;

    [MemberNotNullWhen(true, nameof(_profileList), nameof(_settingsList), nameof(_siteBar), nameof(_statusBar))]
    protected override bool IsReady =>
        _profileList is not null && _settingsList is not null && _siteBar is not null && _statusBar is not null;

    [MemberNotNull(nameof(_profileList), nameof(_settingsList), nameof(_siteBar), nameof(_statusBar))]
    protected override void CreateWidgets(Panel panel)
    {
        var bottomVp = panel.Dock(DockStyle.Bottom, 1);
        var siteVp = panel.Dock(DockStyle.Bottom, 1);
        var leftVp = panel.Dock(DockStyle.Left, 24);
        var fillVp = panel.Fill();

        _siteBar = new TextBar(siteVp);
        _statusBar = new TextBar(bottomVp);
        _profileList = new ScrollableList<ProfilePickerItem>(leftVp);
        _settingsList = new ScrollableList<EquipmentFieldItem>(fillVp);

        panel.Add(_siteBar).Add(_statusBar).Add(_profileList).Add(_settingsList);
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

        // Load profiles once on first render
        if (!_profilesLoaded)
        {
            _profilesLoaded = true;
            RefreshProfiles();
        }

        // Left panel: profile picker
        BuildProfileList();

        if (appState.ActiveProfile is { Data: { } data })
        {
            // Right panel: settings list or device picker
            if (_mode == Mode.Assignment)
            {
                BuildDevicePickerList(data);
            }
            else
            {
                BuildSettingsList(data);
            }

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
            _settingsList.Items([]).Header("Equipment");
            _siteBar.Text(" Site: \u2014");
            _siteBar.RightText("");
        }

        // Status bar varies by mode
        var statusText = _mode switch
        {
            Mode.Assignment => " \u2191\u2193:select  Enter:assign  Esc:cancel",
            Mode.InlineEdit => " Type filter name  Enter:save  Esc:cancel",
            _ when eqState.IsEditingSite => " Tab:next field  Enter:save  Esc:cancel",
            _ when _pendingDeleteOtaIndex >= 0 => " Press X again to confirm delete, any other key to cancel",
            _ when eqState.PendingDisconnectConfirm is not null =>
                $" {PendingDisconnectPrompt(eqState.PendingDisconnectSafety)}  W:warm+off  F:force  Esc:cancel",
            _ => " D:discover  E:site  A:add OTA  O:on/off  Enter:assign  \u2190\u2192:adjust  Q:quit"
        };
        _statusBar.Text(statusText);
        _statusBar.RightText(appState.StatusMessage ?? "");
    }

    private void BuildProfileList()
    {
        if (_profileList is not { } profileList) return;

        var items = new List<ProfilePickerItem>();
        var activeId = appState.ActiveProfile?.ProfileId;
        var activeIdx = -1;
        var idx = 0;

        foreach (var profile in _cachedProfiles)
        {
            var isActive = profile.ProfileId == activeId;
            items.Add(new ProfilePickerItem
            {
                Profile = profile,
                IsActive = isActive,
            });
            if (isActive)
            {
                activeIdx = idx;
            }
            idx++;
        }

        // Items() clamps the cursor; if we have no cursor yet (first build) snap to active.
        var firstBuild = profileList.ItemCount == 0 && items.Count > 0;
        profileList.Items(items).Header("Profiles");
        if (firstBuild && activeIdx >= 0)
        {
            profileList.MoveTo(activeIdx);
        }
    }

    private void BuildSettingsList(ProfileData data)
    {
        if (_settingsList is not { } settingsList) return;

        var items = new List<EquipmentFieldItem>();
        var fieldIdx = 0;

        // -- Profile-level device slots --
        items.Add(new EquipmentFieldItem { SectionName = "Profile Devices" });
        foreach (var slot in equipmentContent.GetProfileSlots(data))
        {
            items.Add(MakeSlotItem(data, slot, ref fieldIdx));
        }

        // -- Per-OTA sections --
        var otaSummaries = equipmentContent.GetOtaSummaries(data);
        for (var i = 0; i < otaSummaries.Count; i++)
        {
            var ota = otaSummaries[i];
            var otaData = data.OTAs[i];

            // OTA header with actions
            items.Add(new EquipmentFieldItem
            {
                SectionName = $"Telescope #{ota.Index}: {ota.Name}",
                IsOtaHeader = true,
                OtaIndex = i,
            });

            // Device sub-slots
            foreach (var slot in ota.DeviceSlots)
            {
                items.Add(MakeSlotItem(data, slot, ref fieldIdx));
            }

            // OTA property steppers
            var capturedOtaIdx = i;

            // Focal Length
            items.Add(MakePropertyStepper(ref fieldIdx, "FL (mm)", otaData.FocalLength.ToString(),
                () =>
                {
                    var updated = EquipmentActions.UpdateOTA(data, capturedOtaIdx,
                        focalLength: Math.Min(otaData.FocalLength + 50, 10000));
                    bus?.Post(new UpdateProfileSignal(updated));
                },
                () =>
                {
                    var updated = EquipmentActions.UpdateOTA(data, capturedOtaIdx,
                        focalLength: Math.Max(otaData.FocalLength - 50, 50));
                    bus?.Post(new UpdateProfileSignal(updated));
                }));

            // Aperture
            items.Add(MakePropertyStepper(ref fieldIdx, "Aperture (mm)", otaData.Aperture?.ToString() ?? "\u2014",
                () =>
                {
                    var updated = EquipmentActions.UpdateOTA(data, capturedOtaIdx,
                        aperture: Math.Min((otaData.Aperture ?? 0) + 10, 2000));
                    bus?.Post(new UpdateProfileSignal(updated));
                },
                () =>
                {
                    var newAp = Math.Max((otaData.Aperture ?? 0) - 10, 0);
                    var updated = EquipmentActions.UpdateOTA(data, capturedOtaIdx, aperture: newAp);
                    bus?.Post(new UpdateProfileSignal(updated));
                }));

            // Optical Design (cycle)
            var designs = Enum.GetValues<OpticalDesign>();
            items.Add(new EquipmentFieldItem
            {
                PropertyLabel = "Design",
                PropertyValue = otaData.OpticalDesign.ToString(),
                IsCycleField = true,
                FieldIndex = fieldIdx,
                Increment = () =>
                {
                    var idx = Array.IndexOf(designs, otaData.OpticalDesign);
                    var next = designs[(idx + 1) % designs.Length];
                    var updated = EquipmentActions.UpdateOTA(data, capturedOtaIdx, opticalDesign: next);
                    bus?.Post(new UpdateProfileSignal(updated));
                },
            });
            fieldIdx++;

            // Filter rows (if filter wheel assigned)
            if (ota.Filters is { Count: > 0 })
            {
                items.Add(new EquipmentFieldItem { SectionName = "Filters" });
                var installedFilters = EquipmentActions.GetFilterConfig(data, i);
                for (var f = 0; f < installedFilters.Count; f++)
                {
                    var capturedFilterIdx = f;
                    var capturedOtaForFilter = i;
                    var filter = installedFilters[f];

                    items.Add(new EquipmentFieldItem
                    {
                        FilterIndex = f + 1,
                        FilterName = EquipmentActions.FilterDisplayName(filter),
                        FilterOffset = filter.Position,
                        OtaIndex = i,
                        FieldIndex = fieldIdx,
                        // Left/Right adjusts offset
                        Increment = () =>
                        {
                            var currentFilters = new List<InstalledFilter>(EquipmentActions.GetFilterConfig(data, capturedOtaForFilter));
                            if (capturedFilterIdx < currentFilters.Count)
                            {
                                var f2 = currentFilters[capturedFilterIdx];
                                currentFilters[capturedFilterIdx] = new InstalledFilter(f2.Filter, f2.Position + 10, f2.CustomName);
                                var updated = EquipmentActions.SetFilterConfig(data, capturedOtaForFilter, currentFilters);
                                bus?.Post(new UpdateProfileSignal(updated));
                            }
                        },
                        Decrement = () =>
                        {
                            var currentFilters = new List<InstalledFilter>(EquipmentActions.GetFilterConfig(data, capturedOtaForFilter));
                            if (capturedFilterIdx < currentFilters.Count)
                            {
                                var f2 = currentFilters[capturedFilterIdx];
                                currentFilters[capturedFilterIdx] = new InstalledFilter(f2.Filter, f2.Position - 10, f2.CustomName);
                                var updated = EquipmentActions.SetFilterConfig(data, capturedOtaForFilter, currentFilters);
                                bus?.Post(new UpdateProfileSignal(updated));
                            }
                        },
                    });
                    fieldIdx++;
                }
            }

            // Device settings for assigned devices in this OTA
            AddDeviceSettings(items, ref fieldIdx, otaData.Camera,
                data.OTAs.Length > 1 ? $"Camera Settings ({ota.Name})" : "Camera Settings");
        }

        // -- Weather device settings --
        AddDeviceSettings(items, ref fieldIdx, data.Weather, "Weather Settings");

        // -- Guider/Guide camera device settings --
        AddDeviceSettings(items, ref fieldIdx, data.Guider, "Guider Settings");
        AddDeviceSettings(items, ref fieldIdx, data.GuiderCamera, "Guide Camera Settings");

        // -- Add OTA action row --
        items.Add(new EquipmentFieldItem
        {
            ActionLabel = "+ Add OTA",
            FieldIndex = fieldIdx,
            Increment = () => bus?.Post(new AddOtaSignal()),
        });
        fieldIdx++;

        _lastItems = items;
        settingsList.Items(items).Header("Equipment");

        // Returning from Assignment mode -> restore the saved browse cursor.
        if (_lastBuiltMode == Mode.Assignment)
        {
            _lastBuiltMode = Mode.Browse;
            settingsList.MoveTo(_savedBrowseCursor);
            SnapPastHeaders(settingsList, +1);
        }
        else if (settingsList.Selected is { FieldIndex: < 0 })
        {
            // First build (or boundary correction) -> land on the first content row.
            SnapPastHeaders(settingsList, +1);
        }
    }

    /// <summary>
    /// Short human-readable reason for why a disconnect was held back by the safety
    /// pre-check. Drives the warning in the status-bar confirm strip.
    /// </summary>
    private static string PendingDisconnectPrompt(EquipmentActions.DisconnectSafety safety) => safety switch
    {
        EquipmentActions.DisconnectSafety.CoolerOn => "Camera is cooled.",
        EquipmentActions.DisconnectSafety.Busy => "Camera is busy.",
        EquipmentActions.DisconnectSafety.BusyAndCool => "Camera is busy and cooled.",
        EquipmentActions.DisconnectSafety.Unknown => "Camera state unknown.",
        _ => "Safe to disconnect.",
    };

    /// <summary>
    /// Builds a device-slot row with connection state pulled from the hub. Keeps
    /// BuildSettingsList readable and ensures profile-level and per-OTA slots render
    /// identically.
    /// </summary>
    private EquipmentFieldItem MakeSlotItem(ProfileData data, DeviceSlotRow slot, ref int fieldIdx)
    {
        var uri = slot.IsAssigned ? EquipmentActions.GetAssignedDevice(data, slot.Slot) : null;
        var connected = uri is not null && appState.DeviceHub?.IsConnected(uri) == true;
        var pending = uri is not null && eqState.PendingTransitions.ContainsKey(uri);

        var item = new EquipmentFieldItem
        {
            Slot = slot.Slot,
            SlotLabel = slot.Label,
            SlotDeviceName = slot.DeviceName,
            IsSlotActive = slot.IsAssigned,
            SlotDeviceUri = uri,
            IsConnected = connected,
            IsPending = pending,
            FieldIndex = fieldIdx,
        };
        fieldIdx++;
        return item;
    }

    private static EquipmentFieldItem MakePropertyStepper(ref int fieldIdx, string label, string value, Action inc, Action dec)
    {
        var item = new EquipmentFieldItem
        {
            PropertyLabel = label,
            PropertyValue = value,
            FieldIndex = fieldIdx,
            Increment = inc,
            Decrement = dec,
        };
        fieldIdx++;
        return item;
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
        var uriKey = deviceUri.GetLeftPart(UriPartial.Path);
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

            items.Add(new EquipmentFieldItem
            {
                Setting = setting,
                DeviceUri = workingUri,
                FieldIndex = fieldIdx,
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

    private void BuildDevicePickerList(ProfileData data)
    {
        if (_settingsList is not { } settingsList) return;
        if (eqState.ActiveAssignment is not { } target)
        {
            return;
        }

        var items = new List<EquipmentFieldItem>();

        // Only show devices of the expected type — the picker is scoped to a single
        // slot and off-type rows are dead weight (AssignDeviceSignal would reject them
        // anyway with "Expected X, got Y"). FieldIndex stays the real DiscoveredDevices
        // index so click/keyboard dispatch still points at the right device.
        var matchCount = 0;
        for (var i = 0; i < eqState.DiscoveredDevices.Count; i++)
        {
            var device = eqState.DiscoveredDevices[i];
            if (device.DeviceType != target.ExpectedDeviceType) continue;

            var assigned = EquipmentActions.IsDeviceAssigned(data, device.DeviceUri);
            items.Add(new EquipmentFieldItem
            {
                SlotLabel = device.DeviceType.ToString(),
                SlotDeviceName = device.DisplayName + (assigned ? " \u2713" : ""),
                IsSlotActive = false,
                Slot = target, // reuse for type info
                FieldIndex = i,
            });
            matchCount++;
        }

        if (matchCount == 0)
        {
            items.Add(new EquipmentFieldItem { SectionName = $"No {target.ExpectedDeviceType} devices discovered" });
            items.Add(new EquipmentFieldItem { PropertyLabel = "Press D to discover", PropertyValue = "", IsCycleField = true });
        }

        _lastItems = items;
        settingsList.Items(items).Header($"Assign {target.ExpectedDeviceType}");

        // Entering Assignment mode -> save the browse cursor and start at row 0
        // (which is the first matching device, since the list is filtered).
        if (_lastBuiltMode != Mode.Assignment)
        {
            _lastBuiltMode = Mode.Assignment;
            settingsList.MoveTo(0);
        }
    }

    private void SaveDeviceSettings(string uriKey)
    {
        if (appState.ActiveProfile?.Data is not { } data)
        {
            return;
        }

        var newUri = _editingUris[uriKey];
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
        if (data.Weather is { } w && w.GetLeftPart(UriPartial.Path) == uriKey)
        {
            return w;
        }

        if (data.Guider is { } g && g.GetLeftPart(UriPartial.Path) == uriKey)
        {
            return g;
        }

        if (data.GuiderCamera is { } gc && gc.GetLeftPart(UriPartial.Path) == uriKey)
        {
            return gc;
        }

        for (var i = 0; i < data.OTAs.Length; i++)
        {
            if (data.OTAs[i].Camera is { } cam && cam.GetLeftPart(UriPartial.Path) == uriKey)
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

    private void RefreshProfiles()
    {
        // Fire-and-forget profile load — results arrive via callback
        _ = LoadProfilesAsync();
    }

    private async Task LoadProfilesAsync()
    {
        try
        {
            _cachedProfiles = await consoleHost.ListDevicesAsync<Profile>(
                DeviceType.Profile, DeviceDiscoveryOption.Force, default);
            NeedsRedraw = true;
        }
        catch
        {
            // Ignore — profiles stay empty
        }
    }

    private void SwitchToSelectedProfile()
    {
        if (_profileList?.Selected is not { } picked) return;

        if (picked.Profile.ProfileId != appState.ActiveProfile?.ProfileId)
        {
            appState.ActiveProfile = picked.Profile;
            _editingUris.Clear();
            // The settings list rebuilds against the new profile next render; reset
            // the cursor target so we land on the first content row.
            _settingsList?.MoveTo(0);
            appState.NeedsRedraw = true;
        }
    }

    // ----------------------------------------------------------------
    // Input handling — state machine
    // ----------------------------------------------------------------

    public override bool HandleRawMouse(MouseEvent mouse)
    {
        if (RouteMouse(_profileList, mouse))
        {
            return true;
        }
        if (RouteMouse(_settingsList, mouse))
        {
            // ScrollableList moves cursor on click; if the click landed on a
            // section header, snap to the next content row so the highlight is
            // never stuck on a non-interactive row.
            if (_settingsList is { } sl && sl.Selected is { FieldIndex: < 0 })
            {
                SnapPastHeaders(sl, +1);
            }
            return true;
        }
        return false;
    }

    private bool RouteMouse<T>(ScrollableList<T>? list, MouseEvent mouse) where T : IRowFormatter
    {
        if (list is null || !list.HandleMouse(mouse)) return false;
        NeedsRedraw = true;
        return true;
    }

    private bool RouteWheel<T>(ScrollableList<T>? list, int step, int mx, int my) where T : IRowFormatter
    {
        if (list is null || list.HitTest(mx, my) is null || !list.HandleWheel(step)) return false;
        NeedsRedraw = true;
        return true;
    }

    /// <summary>
    /// Walks the cursor past any section / OTA-header rows in the given direction
    /// (+1 or -1). On boundary, reverses direction once. No-op when the cursor
    /// already sits on a content row.
    /// </summary>
    private static void SnapPastHeaders(ScrollableList<EquipmentFieldItem> list, int direction)
    {
        var dir = direction == 0 ? +1 : direction;
        var reversed = false;
        while (list.Selected is { FieldIndex: < 0 })
        {
            if (!list.MoveCursor(dir))
            {
                if (reversed) break;
                dir = -dir;
                reversed = true;
            }
        }
    }

    protected override void RegisterClickableRegions()
    {
        // Left panel: clicking a profile row selects and switches to it. The list's
        // own HandleMouse moved the cursor on MouseDown; the tracker callback fires
        // on MouseUp and adds the side-effect (switch active profile). We re-issue
        // MoveTo so a drag-release (down on row A, up on row B) still ends up on B.
        if (_profileList is { } pl && _cachedProfiles.Count > 0)
        {
            var (bx, by, rowW, rowH) = GetRowGeometry(pl);
            var count = _cachedProfiles.Count;
            for (var i = 0; i < pl.VisibleRows && pl.ScrollOffset + i < count; i++)
            {
                var capturedIdx = pl.ScrollOffset + i;
                var y = by + (1 + i) * rowH; // +1 for header row
                Tracker.Register(bx, y, rowW, rowH,
                    new HitResult.ListItemHit("Profile", capturedIdx),
                    _ =>
                    {
                        pl.MoveTo(capturedIdx);
                        SwitchToSelectedProfile();
                        NeedsRedraw = true;
                    });
            }
        }

        // Right panel: ScrollableList's built-in HandleMouse moves its own cursor
        // on click — no tracker registration needed for cursor-only effects. We
        // still need a tracker hit for header rows (so HandleRawMouse can snap
        // past them), but since header rows have FieldIndex < 0, they're excluded
        // here exactly as before.
    }

    // Computes (baseX, baseY, rowWidth, rowHeight) for a list, excluding the
    // scrollbar column from the clickable width so drag/click on the track
    // reaches ScrollableList.HandleMouse instead of triggering a row-select.
    private static (float BaseX, float BaseY, float RowW, float RowH) GetRowGeometry<T>(
        ScrollableList<T> list) where T : IRowFormatter
    {
        var vp = list.Viewport;
        var cell = vp.CellSize;
        var bx = (float)(vp.Offset.Column * cell.Width);
        var by = (float)(vp.Offset.Row * cell.Height);
        var cols = vp.Size.Width - (list.ItemCount > list.VisibleRows ? 1 : 0);
        var rowW = (float)(cols * cell.Width);
        var rowH = (float)cell.Height;
        return (bx, by, rowW, rowH);
    }

    public override bool HandleInput(InputEvent evt)
    {
        if (evt is InputEvent.MouseUp(var mx, var my, MouseButton.Left))
        {
            if (Tracker.HitTestAndDispatch(mx, my) is not null)
            {
                NeedsRedraw = true;
            }
            return false;
        }

        if (evt is InputEvent.Scroll(var delta, var wx, var wy, _))
        {
            // Round to at least 1 row so a slow wheel still moves the list.
            var step = (int)Math.Round(delta);
            if (step == 0) step = delta > 0 ? 1 : -1;
            _ = RouteWheel(_profileList, step, (int)wx, (int)wy)
                || RouteWheel(_settingsList, step, (int)wx, (int)wy);
            return false;
        }

        if (evt is not InputEvent.KeyDown(var key, var modifiers))
        {
            return false;
        }

        // Site editing takes priority over all modes
        if (eqState.IsEditingSite)
        {
            return HandleSiteEditInput(key, modifiers);
        }

        return _mode switch
        {
            Mode.Browse => HandleBrowseInput(key, modifiers),
            Mode.Assignment => HandleAssignmentInput(key, modifiers),
            Mode.InlineEdit => HandleInlineEditInput(key, modifiers),
            _ => false,
        };
    }

    private bool HandleBrowseInput(InputKey key, InputModifier modifiers)
    {
        // Delete confirmation guard
        if (_pendingDeleteOtaIndex >= 0)
        {
            if (key == InputKey.X)
            {
                // Confirmed: delete the OTA
                if (appState.ActiveProfile?.Data is { } data)
                {
                    var updated = EquipmentActions.RemoveOTA(data, _pendingDeleteOtaIndex);
                    bus?.Post(new UpdateProfileSignal(updated));
                }
            }
            // Any key (including X) clears the guard
            _pendingDeleteOtaIndex = -1;
            NeedsRedraw = true;
            return false;
        }

        // Pending-disconnect confirmation guard. Triggered when DisconnectDeviceSignal's
        // safety pre-check classifies the device as unsafe to yank (cooled camera, busy
        // exposure). The handler leaves eqState.PendingDisconnectConfirm set and expects
        // the UI to offer a warm-then-off, force, or cancel choice.
        if (eqState.PendingDisconnectConfirm is { } pendingUri)
        {
            switch (key)
            {
                case InputKey.W:
                    bus?.Post(new WarmAndDisconnectDeviceSignal(pendingUri));
                    NeedsRedraw = true;
                    return false;
                case InputKey.F:
                    bus?.Post(new ForceDisconnectDeviceSignal(pendingUri));
                    NeedsRedraw = true;
                    return false;
                case InputKey.Escape:
                    // PendingDisconnectConfirm controls whether the strip is shown;
                    // PendingDisconnectSafety is a non-nullable cached classification
                    // that's only read while the confirm is active, so leaving it
                    // alone is harmless.
                    eqState.PendingDisconnectConfirm = null;
                    NeedsRedraw = true;
                    return false;
                default:
                    // Any other key is ignored while the confirm strip is up; fall through
                    // so navigation/quit aren't blocked.
                    break;
            }
        }

        switch (key)
        {
            case InputKey.D:
                bus?.Post(new DiscoverDevicesSignal(IncludeFake: (modifiers & InputModifier.Shift) != 0));
                NeedsRedraw = true;
                return false;

            case InputKey.O:
                {
                    var item = FindSelectedItem();
                    if (item is { Slot: not null, IsSlotActive: true, SlotDeviceUri: { } slotUri })
                    {
                        // Post the opposite of the current hub state. Safety / warm-up
                        // decisions happen in AppSignalHandler.
                        if (item.IsConnected)
                        {
                            bus?.Post(new DisconnectDeviceSignal(slotUri));
                        }
                        else
                        {
                            bus?.Post(new ConnectDeviceSignal(slotUri));
                        }
                        NeedsRedraw = true;
                    }
                    return false;
                }

            case InputKey.E:
                _editFieldIndex = 0;
                if (appState.ActiveProfile?.Data is { } pd)
                {
                    var site = EquipmentActions.GetSiteFromProfile(pd);
                    if (site.HasValue)
                    {
                        eqState.LatitudeInput.Activate(site.Value.Lat.ToString(CultureInfo.InvariantCulture));
                        eqState.LongitudeInput.Activate(site.Value.Lon.ToString(CultureInfo.InvariantCulture));
                        eqState.ElevationInput.Activate(site.Value.Elev?.ToString(CultureInfo.InvariantCulture) ?? "");
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

            case InputKey.A:
                bus?.Post(new AddOtaSignal());
                NeedsRedraw = true;
                return false;

            case InputKey.X:
                var otaIdx = FindNearestOtaIndex();
                if (otaIdx >= 0)
                {
                    _pendingDeleteOtaIndex = otaIdx;
                    NeedsRedraw = true;
                }
                return false;

            // Profile picker navigation (Ctrl+Up/Down)
            case InputKey.Up when (modifiers & InputModifier.Ctrl) != 0:
                if (_profileList is { } plUp && plUp.MoveCursor(-1))
                {
                    SwitchToSelectedProfile();
                    NeedsRedraw = true;
                }
                return false;

            case InputKey.Down when (modifiers & InputModifier.Ctrl) != 0:
                if (_profileList is { } plDn && plDn.MoveCursor(+1))
                {
                    SwitchToSelectedProfile();
                    NeedsRedraw = true;
                }
                return false;

            case InputKey.Up:
                if (_settingsList is { } slUp && slUp.MoveCursor(-1))
                {
                    SnapPastHeaders(slUp, -1);
                    NeedsRedraw = true;
                }
                return false;

            case InputKey.Down:
                if (_settingsList is { } slDn && slDn.MoveCursor(+1))
                {
                    SnapPastHeaders(slDn, +1);
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
                if (FindSelectedItem() is { Increment: { } inc })
                {
                    inc();
                    NeedsRedraw = true;
                }
                return false;

            case InputKey.Enter:
                var selected = FindSelectedItem();
                if (selected is null)
                {
                    return false;
                }

                // Slot row → enter assignment mode
                if (selected.Slot is { } slot)
                {
                    EnterAssignmentMode(slot);
                    return false;
                }

                // Filter row → enter inline edit for filter name
                if (selected.FilterIndex > 0 && selected.OtaIndex >= 0)
                {
                    EnterFilterNameEdit(selected.OtaIndex, selected.FilterIndex - 1, selected.FilterName ?? "");
                    return false;
                }

                // Action row or cycle field → invoke increment
                if (selected.Increment is { } enterInc)
                {
                    enterInc();
                    NeedsRedraw = true;
                }
                return false;

            case InputKey.N when (modifiers & InputModifier.Ctrl) != 0:
                bus?.Post(new CreateProfileSignal());
                NeedsRedraw = true;
                return false;

            case InputKey.R:
                _editingUris.Clear();
                RefreshProfiles();
                NeedsRedraw = true;
                return false;
        }

        return false;
    }

    private bool HandleAssignmentInput(InputKey key, InputModifier modifiers)
    {
        switch (key)
        {
            case InputKey.Escape:
                ExitAssignmentMode();
                return false;

            case InputKey.Up:
                if (_settingsList?.MoveCursor(-1) == true) NeedsRedraw = true;
                return false;

            case InputKey.Down:
                if (_settingsList?.MoveCursor(+1) == true) NeedsRedraw = true;
                return false;

            case InputKey.Enter:
                // The picker list is filtered to matching devices only; FieldIndex
                // on each row is the original DiscoveredDevices index.
                if (_settingsList?.Selected is { FieldIndex: >= 0 } picked)
                {
                    bus?.Post(new AssignDeviceSignal(picked.FieldIndex));
                    ExitAssignmentMode();
                }
                return false;

            case InputKey.D:
                // Mirrors the Browse-mode D binding — the assignment picker advertises
                // "Press D to discover" when it's empty, so the key must be live here too.
                bus?.Post(new DiscoverDevicesSignal(IncludeFake: (modifiers & InputModifier.Shift) != 0));
                NeedsRedraw = true;
                return false;
        }

        return false;
    }

    private bool HandleInlineEditInput(InputKey key, InputModifier modifiers)
    {
        if (_activeInlineInput is not { } input)
        {
            _mode = Mode.Browse;
            NeedsRedraw = true;
            return false;
        }

        // Navigation/editing keys
        if (key.ToTextInputKey(modifiers) is { } textKey)
        {
            input.HandleKey(textKey);
            NeedsRedraw = true;

            if (input.IsCommitted)
            {
                input.IsCommitted = false;
                _ = input.OnCommit?.Invoke(input.Text);
                ExitInlineEdit();
            }
            else if (input.IsCancelled)
            {
                input.IsCancelled = false;
                input.OnCancel?.Invoke();
                ExitInlineEdit();
            }
            return false;
        }

        // Printable character input
        if (key.ToChar(modifiers) is { } ch)
        {
            input.InsertText(ch.ToString());
            NeedsRedraw = true;
        }

        return false;
    }

    // ----------------------------------------------------------------
    // Mode transitions
    // ----------------------------------------------------------------

    private void EnterAssignmentMode(AssignTarget slot)
    {
        // Enter the mode unconditionally -- BuildDevicePickerList already renders
        // a "No devices discovered. Press D to discover." empty state when the
        // discovery list is empty, which is what the user should actually see.
        // The previous early-return looked like Enter did nothing.
        eqState.ActiveAssignment = slot;
        _mode = Mode.Assignment;
        // Save the browse-mode cursor row so ExitAssignmentMode can restore it.
        // Pre-selection of the first matching device happens in BuildDevicePickerList
        // by snapping the cursor to row 0 of the (already filtered) list.
        _savedBrowseCursor = _settingsList?.CursorIndex ?? 0;
        NeedsRedraw = true;
    }

    private void ExitAssignmentMode()
    {
        eqState.ActiveAssignment = null;
        _mode = Mode.Browse;
        NeedsRedraw = true;
    }

    private void EnterFilterNameEdit(int otaIndex, int filterIdx, string currentName)
    {
        var input = new TextInputState { Placeholder = "Filter name..." };
        input.Activate(currentName);

        var capturedOta = otaIndex;
        var capturedFilter = filterIdx;

        input.OnCommit = _ =>
        {
            if (appState.ActiveProfile?.Data is { } data)
            {
                var filters = new List<InstalledFilter>(EquipmentActions.GetFilterConfig(data, capturedOta));
                if (capturedFilter < filters.Count)
                {
                    var newName = input.Text.Trim();
                    if (newName.Length > 0)
                    {
                        filters[capturedFilter] = new InstalledFilter(newName, filters[capturedFilter].Position);
                        var updated = EquipmentActions.SetFilterConfig(data, capturedOta, filters);
                        bus?.Post(new UpdateProfileSignal(updated));
                    }
                }
            }
            return System.Threading.Tasks.Task.CompletedTask;
        };

        _activeInlineInput = input;
        _mode = Mode.InlineEdit;
        NeedsRedraw = true;
    }

    private void ExitInlineEdit()
    {
        _activeInlineInput = null;
        _mode = Mode.Browse;
        NeedsRedraw = true;
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private EquipmentFieldItem? FindSelectedItem()
    {
        // The settings-list cursor IS the selection; Selected returns null when the
        // list is empty and a header item (FieldIndex < 0) when the cursor is parked
        // on a non-content row, both of which we want to skip.
        return _settingsList?.Selected is { FieldIndex: >= 0 } sel ? sel : null;
    }

    /// <summary>
    /// Finds the OTA index for the nearest OTA header at or above the current cursor.
    /// </summary>
    private int FindNearestOtaIndex()
    {
        if (_settingsList is not { } sl) return -1;
        var pos = sl.CursorIndex;
        for (var i = pos; i >= 0 && i < _lastItems.Count; i--)
        {
            if (_lastItems[i].IsOtaHeader && _lastItems[i].OtaIndex >= 0)
            {
                return _lastItems[i].OtaIndex;
            }
        }

        return -1;
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
