using System;
using System.Diagnostics.CodeAnalysis;
using Console.Lib;
using DIR.Lib;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui;

/// <summary>
/// TUI session configuration tab. Left: scrollable config form with keyboard steppers.
/// Right: per-OTA camera settings + observation list from pinned planner targets.
/// </summary>
internal sealed class TuiSessionTab(
    GuiAppState appState,
    SessionTabState sessionState,
    PlannerState plannerState,
    SignalBus bus) : TuiTabBase
{
    private ScrollableList<SessionFieldItem>? _configList;
    private MarkdownWidget? _rightPanel;
    private TextBar? _statusBar;
    private System.Collections.Generic.List<SessionFieldItem> _lastItems = [];
    private int _lastEnsuredIndex = -1;

    [MemberNotNullWhen(true, nameof(_configList), nameof(_rightPanel), nameof(_statusBar))]
    protected override bool IsReady => _configList is not null && _rightPanel is not null && _statusBar is not null;

    // Fill-leaf keys. The tree names these; PaintHost draws them.
    private const string ConfigKey = "config";
    private const string RightKey = "right";
    private const string StatusKey = "status";

    protected override void CreateWidgets()
    {
        _statusBar = new TextBar(Host(StatusKey));
        _rightPanel = new MarkdownWidget(Host(RightKey));
        _configList = new ScrollableList<SessionFieldItem>(Host(ConfigKey));
    }

    /// <summary>
    /// Config form left, per-OTA panel right at a fixed 44 columns, one status row underneath --
    /// the same arrangement the docked Panel produced, so this is a straight translation.
    /// <para>
    /// Now that it is a tree rebuilt per frame, a narrow terminal could branch here (stack the two
    /// vertically, or <c>CollapseBelow</c> the right panel) without touching widget construction.
    /// Deliberately not doing that in the migration -- behaviour first, responsiveness after.
    /// </para>
    /// </summary>
    protected override Layout.Node BuildLayout() =>
        Layout.Builder.VStack(
            // Stretch / ColW, not WStar / WFixed: in an HStack the cross axis is the height, and a Fill leaf
            // left on Auto height measures its MinHeight -- zero.
            Layout.Builder.HStack(
                Layout.Builder.Fill(key: ConfigKey).Stretch(),
                Layout.Builder.Fill(key: RightKey).ColW(44)).Stretch(),
            Layout.Builder.Fill(key: StatusKey).RowH(1));

    protected override void PaintHost(string key, Rect<int> rect, bool geometryChanged)
    {
        switch (key)
        {
            case ConfigKey:
                _configList?.Render();
                break;

            case RightKey:
                _rightPanel?.Render();
                break;

            case StatusKey:
                _statusBar?.Render();
                break;
        }
    }

    protected override void RenderContent()
    {
        if (!IsReady) return;

        // Reinitialize per-OTA settings when the profile changes
        if (sessionState.NeedsReinitialization(appState.ActiveProfile))
        {
            sessionState.InitializeFromProfile(appState.ActiveProfile);
        }

        // Build flat item list from config groups
        var groups = SessionConfigGroups.Groups;
        var items = new System.Collections.Generic.List<SessionFieldItem>();
        var fieldIdx = 0;

        foreach (var group in groups)
        {
            items.Add(new SessionFieldItem { GroupName = group.Name });

            foreach (var field in group.Fields)
            {
                items.Add(new SessionFieldItem
                {
                    Field = field,
                    FieldIndex = fieldIdx,
                    IsSelected = fieldIdx == sessionState.SelectedFieldIndex,
                    FormattedValue = field.FormatValue(sessionState.Configuration),
                });
                fieldIdx++;
            }
        }

        // Per-OTA camera settings
        for (var ota = 0; ota < sessionState.CameraSettings.Count; ota++)
        {
            var cam = sessionState.CameraSettings[ota];
            items.Add(new SessionFieldItem { GroupName = cam.OtaName });

            var capturedOta = ota;
            items.Add(new SessionFieldItem
            {
                OtaLabel = "Setpoint",
                FieldIndex = fieldIdx,
                IsSelected = fieldIdx == sessionState.SelectedFieldIndex,
                FormattedValue = $"{cam.SetpointTempC}°C",
                Increment = () => { cam.SetpointTempC = (sbyte)Math.Min(cam.SetpointTempC + 1, 30); sessionState.MarkDirty(); },
                Decrement = () => { cam.SetpointTempC = (sbyte)Math.Max(cam.SetpointTempC - 1, -40); sessionState.MarkDirty(); },
            });
            fieldIdx++;

            items.Add(new SessionFieldItem
            {
                OtaLabel = "Gain",
                FieldIndex = fieldIdx,
                IsSelected = fieldIdx == sessionState.SelectedFieldIndex,
                FormattedValue = cam.UsesGainMode && cam.Gain >= 0 && cam.Gain < cam.GainModes.Count
                    ? cam.GainModes[cam.Gain]
                    : $"{cam.Gain}",
                Increment = () =>
                {
                    cam.Gain = cam.UsesGainMode && cam.GainModes.Count > 0
                        ? (cam.Gain + 1) % cam.GainModes.Count
                        : Math.Min(cam.Gain + 10, 600);
                    sessionState.MarkDirty();
                },
                Decrement = () =>
                {
                    cam.Gain = cam.UsesGainMode && cam.GainModes.Count > 0
                        ? (cam.Gain - 1 + cam.GainModes.Count) % cam.GainModes.Count
                        : Math.Max(cam.Gain - 10, 0);
                    sessionState.MarkDirty();
                },
            });
            fieldIdx++;

            items.Add(new SessionFieldItem
            {
                OtaLabel = "Offset",
                FieldIndex = fieldIdx,
                IsSelected = fieldIdx == sessionState.SelectedFieldIndex,
                FormattedValue = $"{cam.Offset}",
                Increment = () => { cam.Offset = Math.Min(cam.Offset + 1, 255); sessionState.MarkDirty(); },
                Decrement = () => { cam.Offset = Math.Max(cam.Offset - 1, 0); sessionState.MarkDirty(); },
            });
            fieldIdx++;
        }

        sessionState.FieldCount = fieldIdx;
        _lastItems = items;
        _configList.Items([.. items]).Header("Session Configuration");

        // Scroll to keep selected item visible
        var selectedListIdx = items.FindIndex(i => i.IsSelected);
        if (selectedListIdx >= 0 && selectedListIdx != _lastEnsuredIndex)
        {
            _configList.EnsureVisible(selectedListIdx);
            _lastEnsuredIndex = selectedListIdx;
        }
        sessionState.ConfigScrollOffset = _configList.ScrollOffset;

        // Right panel: shared content model
        _rightPanel.Markdown(SessionContent.FormatRightPanelMarkdown(sessionState, plannerState));

        // Status bar
        _statusBar.Text(" \u2191\u2193:select  \u2190\u2192:adjust  G:start session  Q:quit");
        _statusBar.RightText(appState.StatusMessage ?? "");
    }

    /// <summary>
    /// Resolves a click on the config list to the field behind it. The list owns the geometry (viewport
    /// origin, header row, scroll offset, scrollbar column) and hands back the ITEM, so this only has to
    /// say what a click means -- a group header carries no field index and stays inert.
    /// <para>
    /// The selected field lives in the session state rather than in the list cursor (the keyboard moves it
    /// independently), so the click has to write it explicitly.
    /// </para>
    /// </summary>
    private bool DispatchConfigListClick(int x, int y)
    {
        if (_configList?.HitTestRow(x, y) is not { Item.FieldIndex: >= 0 and var fieldIndex })
        {
            return false;
        }

        sessionState.SelectedFieldIndex = fieldIndex;
        NeedsRedraw = true;
        return true;
    }

    public override bool HandleRawMouse(MouseEvent mouse)
    {
        if (_configList is { } list && list.HandleMouse(mouse))
        {
            NeedsRedraw = true;
            return true;
        }
        return false;
    }

    protected override void HandleTabInput(InputEvent evt){
        switch (evt)
        {
            case InputEvent.MouseUp(var x, var y, MouseButton.Left):
                if (!DispatchConfigListClick((int)x, (int)y) && Tracker.HitTestAndDispatch(x, y) is not null)
                {
                    NeedsRedraw = true;
                }
                return;

            case InputEvent.KeyDown(var key, _):
                // The helper bool means "did I consume this" for the tab's own use; it must not
                // travel further -- see ITuiTab.HandleInput on why a tab cannot ask the app to exit.
                _ = HandleKey(key);
                break;

            default:
                return;
        }
    }

    private SessionFieldItem? FindSelectedItem()
    {
        var idx = sessionState.SelectedFieldIndex;
        return idx >= 0 ? _lastItems.Find(i => i.FieldIndex == idx) : null;
    }

    private bool HandleKey(InputKey key)
    {
        switch (key)
        {
            case InputKey.Up:
                if (sessionState.SelectedFieldIndex > 0)
                {
                    sessionState.SelectedFieldIndex--;
                    NeedsRedraw = true;
                }
                return false;

            case InputKey.Down:
                if (sessionState.SelectedFieldIndex < sessionState.FieldCount - 1)
                {
                    sessionState.SelectedFieldIndex++;
                    NeedsRedraw = true;
                }
                return false;

            case InputKey.Left:
                if (FindSelectedItem() is { Decrement: { } dec })
                {
                    dec();
                }
                else
                {
                    sessionState.DecrementSelectedField();
                }
                NeedsRedraw = true;
                return false;

            case InputKey.Right:
            case InputKey.Enter:
                if (FindSelectedItem() is { Increment: { } inc })
                {
                    inc();
                }
                else
                {
                    sessionState.IncrementSelectedField();
                }
                NeedsRedraw = true;
                return false;

            case InputKey.R:
                NeedsRedraw = true;
                return false;

            case InputKey.G:
                // Start session (proposals exist and planning date is tonight)
                if (plannerState.Proposals.Length > 0 && !plannerState.PlanningDate.HasValue)
                {
                    bus.Post(new StartSessionSignal());
                }
                return false;
        }

        return false;
    }
}
