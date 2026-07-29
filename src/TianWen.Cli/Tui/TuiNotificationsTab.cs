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

    // Fill-leaf keys. The tree names these; PaintHost draws them.
    private const string ListKey = "list";
    private const string StatusKey = "status";

    protected override void CreateWidgets()
    {
        _list = new ScrollableList<NotificationListItem>(Host(ListKey));
        _statusBar = new TextBar(Host(StatusKey));
    }

    /// <summary>The list takes everything the one-row status bar does not.</summary>
    protected override Layout.Node BuildLayout() =>
        Layout.Builder.VStack(
            Layout.Builder.Fill(key: ListKey).Stretch(),
            Layout.Builder.Fill(key: StatusKey).RowH(1));

    protected override void PaintHost(string key, Rect<int> rect, bool geometryChanged)
    {
        switch (key)
        {
            case ListKey:
                _list?.Render();
                break;

            case StatusKey:
                _statusBar?.Render();
                break;
        }
    }

    protected override void RenderContent()
    {
        if (!IsReady) return;

        var entries = appState.Notifications;
        var items = new NotificationListItem[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            items[i] = new NotificationListItem(entries[i], appState.SiteTimeZone);
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

    protected override void HandleTabInput(InputEvent evt){
        if (!IsReady) return;

        switch (evt)
        {
            case InputEvent.Scroll(var delta, _, _, _):
                if (_list.HandleWheel(delta > 0 ? 3 : -3))
                {
                    NeedsRedraw = true;
                }
                return;

            case InputEvent.KeyDown(var key, _):
                // The helper bool means "did I consume this" for the tab's own use; it must not
                // travel further -- see ITuiTab.HandleInput on why a tab cannot ask the app to exit.
                _ = HandleKey(key);
                break;
        }
        return;
    }

    private bool HandleKey(InputKey key)
    {
        if (!IsReady) return false;

        // Up/Down/PageUp/PageDown/Home/End all delegate to the list's cursor.
        // The cursor auto-scrolls so the focused row stays visible.
        var page = System.Math.Max(1, _list.VisibleRows - 1);
        var moved = key switch
        {
            InputKey.Up => _list.MoveCursor(-1),
            InputKey.Down => _list.MoveCursor(+1),
            InputKey.PageUp => _list.MoveCursor(-page),
            InputKey.PageDown => _list.MoveCursor(+page),
            InputKey.Home => _list.MoveTo(0),
            InputKey.End => _list.MoveTo(int.MaxValue),
            _ => false,
        };
        if (moved)
        {
            NeedsRedraw = true;
            return false;
        }

        if (key == InputKey.C)
        {
            appState.ClearNotifications();
            NeedsRedraw = true;
            return false;
        }
        return false;
    }
}
