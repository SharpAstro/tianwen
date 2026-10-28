using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// What the navigation strip asks of the window: whether Back and Forward have a place to go to, and to go there. The
/// owner is the event handler (<see cref="GuiEventHandlerBase"/>), which is also who Alt+Left, Alt+Right and the mouse's
/// buttons reach, so the strip's press and those keys are one path (#1821).
/// </summary>
public interface IGuiNavigation
{
    /// <summary>Whether there is a place to go back to.</summary>
    bool CanGoBack { get; }

    /// <summary>Whether there is a place to go forward to.</summary>
    bool CanGoForward { get; }

    /// <summary>Goes back one place; false with nowhere to go.</summary>
    bool GoBack();

    /// <summary>Goes forward one place; false with nowhere to go.</summary>
    bool GoForward();
}

/// <summary>
/// Back and Forward at the top of the navigation rail: DIR.Lib's <see cref="Layout.Builder.NavigationStrip"/>, coloured
/// from <see cref="GuiTheme.Palette"/> and fed from the window's <see cref="IGuiNavigation"/>.
/// </summary>
/// <remarks>
/// A widget of its own, over the surface-generic base, so a test can paint it on a CPU surface and press the region it
/// registered (the Vulkan chrome that hosts it cannot be built headless). The strip declares its own hover, tooltip and
/// press, so the paint is also the hit: nothing here places a rect by hand. Its height is <see cref="BaseHeight"/>, ONE
/// constant the host reads to start the rail's tab cells below it.
/// </remarks>
public sealed class GuiNavigationStrip<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    /// <summary>
    /// The strip's height in design units, which the host scales by its own display scale to start the rail's tab cells
    /// below it. A tab cell is square (the rail's width), so this is a little over half of one.
    /// </summary>
    public const float BaseHeight = 28f;

    // A step mark a little larger than the tab glyphs' x-height read, in design units like the rest of the tree.
    private const float MarkSize = 14f;

    /// <summary>The window's navigation; null until the event handler has bound itself, which leaves both cells dimmed.</summary>
    public IGuiNavigation? Navigation { get; set; }

    /// <summary>The colours the strip draws in for <paramref name="palette"/>: the rail's own ground, its ink and its hover.</summary>
    public static Layout.NavigationStripColors ColorsFor(UiPalette palette) => new(
        Fill: palette.PanelBg,
        HoverFill: GuiTheme.Hover(palette.PanelBg),
        // The arrow is an ACTION, so it reads as the active tab's glyph does (BodyText); with nowhere to go it takes the
        // rail's inactive-tab ink (DimText), which stays an icon at 3:1 or better on the rail in Light, Dark and Night
        // (6.2, 5.6, 3.6) while reading plainly dimmer than the enabled one.
        Ink: palette.BodyText,
        DisabledInk: palette.DimText);

    /// <summary>Paints the strip into <paramref name="bounds"/> (the rail's width, <see cref="BaseHeight"/> tall once scaled).</summary>
    public void Render(RectF32 bounds)
    {
        BeginFrame();

        var navigation = Navigation;
        var colors = ColorsFor(GuiTheme.Palette);
        var strip = Layout.Builder.NavigationStrip(
            navigation?.CanGoBack ?? false,
            navigation?.CanGoForward ?? false,
            () => navigation?.GoBack(),
            () => navigation?.GoForward(),
            colors,
            MarkSize);
        RenderLayout(strip, bounds);
    }
}
