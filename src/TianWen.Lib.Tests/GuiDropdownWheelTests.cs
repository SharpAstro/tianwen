using System;
using System.Collections.Immutable;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The wheel over an open, overflowing Equipment menu scrolls that menu through the ROUTER alone.
/// </summary>
/// <remarks>
/// The tab used to forward the wheel to both of its menus by hand, and since DIR.Lib 10.2 that was a
/// second answer to a question the engine already asks: a painted <c>Layout.Builder.Dropdown</c> declares
/// its list as a scroll target, and <see cref="InputRouter"/> offers the wheel to the innermost one under
/// the pointer before anything else. The router here has NO fallback to the tab, so nothing the tab
/// declares in <c>HandleInput</c> can make this pass.
/// </remarks>
[Collection("UI")]
public class GuiDropdownWheelTests
{
    // Short enough that sixty rows cannot fit under the anchor.
    private const uint SurfaceW = 900;
    private const uint SurfaceH = 400;

    public enum Menu { Profile, FilterName }

    [Theory]
    [InlineData(Menu.Profile)]
    [InlineData(Menu.FilterName)]
    public void TheWheelOverALongEquipmentMenuScrollsItThroughTheRouter(Menu which)
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var tab = new EquipmentTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        // The menus are drawn only with a profile to show; with none the tab returns before them.
        var appState = new GuiAppState { ActiveProfile = new Profile(Guid.NewGuid(), "Test", ProfileData.Empty) };
        var menu = which is Menu.Profile ? tab.State.ProfileDropdown : tab.State.FilterNameDropdown;
        menu.Open(40f, 60f, 220f,
            Enumerable.Range(1, 60).Select(i => DropdownItem.Text($"Entry {i}")).ToImmutableArray());

        tab.Render(appState, new RectF32(0f, 0f, SurfaceW, SurfaceH));
        menu.Scroll.MaxOffset.ShouldBeGreaterThan(0f, "the menu overflows, or there is nothing to scroll");
        var row = DropdownRows.First(tab);

        var router = new InputRouter(tab.Ui, new BackgroundTaskTracker(), static () => { })
        {
            Widgets = () => [tab],
            Unhandled = static _ => false,
        };
        router.Handle(new InputEvent.Scroll(-3f, row.X + (row.Width / 2f), row.Y + (row.Height / 2f)))
            .ShouldBeTrue("the menu's own scroll target took the wheel");

        menu.Scroll.Offset.ShouldBeGreaterThan(0f, "a wheel down moved the list");
    }
}
