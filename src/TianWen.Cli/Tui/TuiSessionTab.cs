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

    [MemberNotNullWhen(true, nameof(_configList), nameof(_rightPanel), nameof(_statusBar))]
    protected override bool IsReady => _configList is not null && _rightPanel is not null && _statusBar is not null;

    protected override void CreateWidgets(Panel panel)
    {
        var bottomVp = panel.Dock(DockStyle.Bottom, 1);
        var rightVp = panel.Dock(DockStyle.Right, 44);
        var fillVp = panel.Fill();

        _statusBar = new TextBar(bottomVp);
        _rightPanel = new MarkdownWidget(rightVp);
        _configList = new ScrollableList<SessionFieldItem>(fillVp);

        panel.Add(_statusBar).Add(_rightPanel).Add(_configList);
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
        if (selectedListIdx >= 0 && _configList.VisibleRows > 0)
        {
            sessionState.ConfigScrollOffset = Math.Max(0, selectedListIdx - _configList.VisibleRows / 2);
            _configList.ScrollTo(sessionState.ConfigScrollOffset);
        }

        // Right panel: shared content model
        _rightPanel.Markdown(SessionContent.FormatRightPanelMarkdown(sessionState, plannerState));

        // Status bar
        _statusBar.Text(" \u2191\u2193:select  \u2190\u2192:adjust  G:start session  Q:quit");
        _statusBar.RightText(appState.StatusMessage ?? "");
    }

    protected override void RegisterClickableRegions()
    {
        if (_configList is null || _lastItems.Count == 0)
        {
            return;
        }

        var cellSize = _configList.Viewport.CellSize;
        var offset = _configList.Viewport.Offset;
        var baseX = (float)(offset.Column * cellSize.Width);
        var baseY = (float)(offset.Row * cellSize.Height);
        var rowW = (float)(_configList.Viewport.Size.Width * cellSize.Width);
        var rowH = (float)cellSize.Height;
        var headerRows = 1; // ScrollableList header row

        for (var i = 0; i < _configList.VisibleRows && sessionState.ConfigScrollOffset + i < _lastItems.Count; i++)
        {
            var item = _lastItems[sessionState.ConfigScrollOffset + i];
            if (item.FieldIndex < 0)
            {
                continue; // skip group headers
            }

            var capturedIdx = item.FieldIndex;
            var y = baseY + (headerRows + i) * rowH;
            Tracker.Register(baseX, y, rowW, rowH,
                new HitResult.ListItemHit("ConfigField", item.FieldIndex),
                _ => { sessionState.SelectedFieldIndex = capturedIdx; });
        }
    }

    public override bool HandleInput(InputEvent evt)
    {
        switch (evt)
        {
            case InputEvent.MouseUp(var x, var y, MouseButton.Left):
                if (Tracker.HitTestAndDispatch(x, y) is not null)
                {
                    NeedsRedraw = true;
                }
                return false;

            case InputEvent.KeyDown(var key, _):
                return HandleKey(key);

            default:
                return false;
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
