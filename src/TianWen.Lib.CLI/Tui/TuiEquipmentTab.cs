using System.Diagnostics.CodeAnalysis;
using Console.Lib;
using DIR.Lib;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// TUI equipment/profile tab. Shows profile summary as markdown
/// with keyboard commands for device discovery and management.
/// </summary>
internal sealed class TuiEquipmentTab(
    GuiAppState appState,
    EquipmentTabState eqState,
    EquipmentContent equipmentContent,
    SignalBus? bus = null) : TuiTabBase
{
    private MarkdownWidget? _profileWidget;
    private TextBar? _siteBar;
    private TextBar? _statusBar;

    /// <summary>Tracks which field is active during site editing: 0=lat, 1=lon, 2=elev.</summary>
    private int _editFieldIndex;

    [MemberNotNullWhen(true, nameof(_profileWidget), nameof(_siteBar), nameof(_statusBar))]
    protected override bool IsReady => _profileWidget is not null && _siteBar is not null && _statusBar is not null;

    [MemberNotNull(nameof(_profileWidget), nameof(_siteBar), nameof(_statusBar))]
    protected override void CreateWidgets(Panel panel)
    {
        var bottomVp = panel.Dock(DockStyle.Bottom, 1);
        var siteVp = panel.Dock(DockStyle.Bottom, 1);
        var fillVp = panel.Fill();

        _siteBar = new TextBar(siteVp);
        _statusBar = new TextBar(bottomVp);
        _profileWidget = new MarkdownWidget(fillVp);

        panel.Add(_siteBar).Add(_statusBar).Add(_profileWidget);
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
            _profileWidget.Markdown(equipmentContent.FormatProfileMarkdown(profile));

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
            _siteBar.Text(" Site: \u2014");
            _siteBar.RightText("");
        }

        _statusBar.Text(" D:discover  E:edit site  R:refresh  Q:quit");
        _statusBar.RightText(appState.StatusMessage ?? "");
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

            case InputKey.R:
                NeedsRedraw = true;
                return false;
        }

        return false;
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
