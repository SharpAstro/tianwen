using System.Diagnostics.CodeAnalysis;
using Console.Lib;
using DIR.Lib;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui;

/// <summary>
/// TUI notifications history tab. Newest first. Keyboard: Up/Down scroll one row,
/// PgUp/PgDn page, Home/End jump to ends, C clears. Mouse wheel scrolls.
/// </summary>
internal sealed class TuiNotificationsTab(GuiAppState appState) : TuiTabBase
{
    private ScrollableList<NotificationListItem>? _list;
    private TextBar? _statusBar;

    [MemberNotNullWhen(true, nameof(_list), nameof(_statusBar))]
    protected override bool IsReady => _list is not null && _statusBar is not null;

    protected override void CreateWidgets(Panel panel)
    {
        var bottomVp = panel.Dock(DockStyle.Bottom, 1);
        var fillVp = panel.Fill();

        _statusBar = new TextBar(bottomVp);
        _list = new ScrollableList<NotificationListItem>(fillVp);

        panel.Add(_statusBar).Add(_list);
    }

    protected override void RenderContent()
    {
        if (!IsReady) return;

        var entries = appState.Notifications;
        var items = new NotificationListItem[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            items[i] = new NotificationListItem(entries[i]);
        }

        var header = entries.Length > 0
            ? $" Notifications ({entries.Length})"
            : " Notifications \u2014 nothing yet";
        _list.Items(items).Header(header);

        _statusBar.Text(" \u2191\u2193:scroll  PgUp/PgDn:page  Home/End:jump  C:clear  Q:quit");
        _statusBar.RightText(appState.StatusMessage ?? "");
    }

    public override bool HandleRawMouse(MouseEvent mouse)
    {
        if (_list is { } list && list.HandleMouse(mouse))
        {
            NeedsRedraw = true;
            return true;
        }
        return false;
    }

    public override bool HandleInput(InputEvent evt)
    {
        if (!IsReady) return false;

        switch (evt)
        {
            case InputEvent.Scroll(var delta, _, _, _):
                if (_list.HandleWheel(delta > 0 ? 3 : -3))
                {
                    NeedsRedraw = true;
                }
                return false;

            case InputEvent.KeyDown(var key, _):
                return HandleKey(key);
        }
        return false;
    }

    private bool HandleKey(InputKey key)
    {
        if (!IsReady) return false;

        switch (key)
        {
            case InputKey.Up:
                if (_list.HandleWheel(1)) NeedsRedraw = true;
                return false;

            case InputKey.Down:
                if (_list.HandleWheel(-1)) NeedsRedraw = true;
                return false;

            case InputKey.PageUp:
                if (_list.HandleWheel(System.Math.Max(1, _list.VisibleRows - 1))) NeedsRedraw = true;
                return false;

            case InputKey.PageDown:
                if (_list.HandleWheel(-System.Math.Max(1, _list.VisibleRows - 1))) NeedsRedraw = true;
                return false;

            case InputKey.Home:
                _list.ScrollTo(0);
                NeedsRedraw = true;
                return false;

            case InputKey.End:
                _list.ScrollTo(int.MaxValue);
                NeedsRedraw = true;
                return false;

            case InputKey.C:
                appState.ClearNotifications();
                NeedsRedraw = true;
                return false;
        }
        return false;
    }
}
