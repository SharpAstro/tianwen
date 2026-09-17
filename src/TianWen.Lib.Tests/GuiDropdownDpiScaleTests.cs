using System;
using System.Collections.Immutable;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The GUI's own dropdowns apply the DPI scale ONCE, like the viewer's toolbar menus
/// (<see cref="ViewerToolbarLayoutTests.AToolbarDropdownAppliesTheDpiScaleOnce"/>).
/// </summary>
/// <remarks>
/// <para>
/// Both tabs hand <c>Layout.Builder.Dropdown</c> a font size of <c>BaseFontSize * DpiScale</c> and an
/// anchor taken from the trigger's arranged rect, which are device pixels, and both arranged it through
/// the default context, which scales a tree as design units. So the GUI menus had the viewer's defect
/// too, found by reading the code rather than by a report: the menu conversion that introduced it
/// changed all three sites together.
/// </para>
/// <para>
/// Each menu is opened directly at a device-pixel anchor, the shape its trigger produces, because the
/// defect is in how the open menu is ARRANGED, not in how it is opened.
/// </para>
/// </remarks>
[Collection("UI")]
public class GuiDropdownDpiScaleTests
{
    private const uint SurfaceW = 1800;
    private const uint SurfaceH = 1200;

    // Design units; each test scales them the way a trigger's arranged rect is scaled.
    private const float AnchorX = 40f;
    private const float AnchorY = 60f;
    private const float AnchorW = 220f;

    [Fact]
    public void TheLiveSessionModeMenuAppliesTheDpiScaleOnce()
    {
        var atOne = LiveSessionModeMenuRow(1f);
        var atTwo = LiveSessionModeMenuRow(2f);

        atTwo.Height.ShouldBe(atOne.Height * 2f, 0.5f,
            "a row is sized from the font alone, so twice the scale is exactly twice the height");
        atOne.Width.ShouldBe(AnchorW, 0.5f, "the rows are as wide as the anchor");
        atTwo.Width.ShouldBe(AnchorW * 2f, 0.5f,
            "and the anchor is already device pixels, so it must not be scaled a second time");
    }

    [Fact]
    public void TheEquipmentProfileMenuAppliesTheDpiScaleOnce()
    {
        var atOne = EquipmentProfileMenuRow(1f);
        var atTwo = EquipmentProfileMenuRow(2f);

        atTwo.Height.ShouldBe(atOne.Height * 2f, 0.5f,
            "a row is sized from the font alone, so twice the scale is exactly twice the height");
        atOne.Width.ShouldBe(AnchorW, 0.5f, "the rows are as wide as the anchor");
        atTwo.Width.ShouldBe(AnchorW * 2f, 0.5f,
            "and the anchor is already device pixels, so it must not be scaled a second time");
    }

    private static RectF32 LiveSessionModeMenuRow(float dpiScale)
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var tab = new LiveSessionTab<RgbaImage>(renderer)
        {
            DpiScale = dpiScale,
            FontPath = FontResolver.ResolveSystemFont(),
        };
        var state = new LiveSessionState();
        state.ModeDropdown.Open(AnchorX * dpiScale, AnchorY * dpiScale, AnchorW * dpiScale,
            ImmutableArray.Create(
                new DropdownItem<LiveSessionMode>("Preview", LiveSessionMode.Preview),
                new DropdownItem<LiveSessionMode>("Planetary", LiveSessionMode.Planetary)));

        tab.Render(state, new RectF32(0f, 0f, SurfaceW, SurfaceH), new SystemTimeProvider());

        return DropdownRows.First(tab);
    }

    private static RectF32 EquipmentProfileMenuRow(float dpiScale)
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var tab = new EquipmentTab<RgbaImage>(renderer)
        {
            DpiScale = dpiScale,
            FontPath = FontResolver.ResolveSystemFont(),
        };
        // The menus are drawn only with a profile to show; with none the tab returns before them.
        var appState = new GuiAppState { ActiveProfile = new Profile(Guid.NewGuid(), "Test", ProfileData.Empty) };
        tab.State.ProfileDropdown.Open(AnchorX * dpiScale, AnchorY * dpiScale, AnchorW * dpiScale,
            new[] { "Test", "Other" }.Select(DropdownItem.Text).ToImmutableArray());

        tab.Render(appState, new RectF32(0f, 0f, SurfaceW, SurfaceH));

        return DropdownRows.First(tab);
    }
}
