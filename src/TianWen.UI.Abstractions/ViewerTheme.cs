using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The FITS viewer's chrome theme. Distinct from <see cref="GuiTheme"/> only in its
    /// <see cref="UiMetrics"/> (18px base font against the GUI's 14px) and in the five translucent
    /// panel fills below, which are alpha-blended over the rendered image and so do not map onto
    /// the opaque shared roles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The colours are <see cref="GuiTheme"/>'s, not a second scheme.</b> They used to be an
    /// independent neutral-grey ramp (R=G=B exactly), which read visibly colder and flatter than
    /// the GUI hosting it in a tab -- two palettes side by side on one screen, agreeing about
    /// nothing. Following the shared state also means the viewer goes Night with everything else,
    /// which a private palette could not.
    /// </para>
    /// <para>
    /// The panel fills derive their alpha from the state: over a dark image a light scrim would
    /// wash it out and vice versa, so <see cref="UiPalette.IsDark"/> selects which way to tint.
    /// </para>
    /// </remarks>
    public static class ViewerTheme
    {
        /// <summary>Shared chrome colour roles, following the app-wide theme state.</summary>
        public static UiPalette Palette => GuiTheme.Palette;

        /// <summary>Base (unscaled) layout metrics. The viewer renders at an 18px base font.</summary>
        public static UiMetrics Metrics { get; } = new UiMetrics(
            BaseFontSize: 18f,
            Padding:      6f,
            HeaderHeight: 40f,   // toolbar
            ItemHeight:   24f,   // status bar / list row
            ButtonHeight: 28f);

        /// <summary>The combined viewer theme (palette + viewer metrics).</summary>
        public static UiTheme Theme => new UiTheme { Palette = Palette, Metrics = Metrics };

        // Viewer-specific panel fills, alpha-blended over the rendered image. Derived from the
        // shared roles at a stated alpha rather than hardcoded, so they follow the theme; the alpha
        // IS the design here and only the hue comes from the palette.

        /// <summary>Toolbar strip background (opaque).</summary>
        public static RGBAColor32 ToolbarBg => Palette.HeaderBg;

        /// <summary>Status bar background (slightly translucent).</summary>
        public static RGBAColor32 StatusBarBg => WithAlpha(Palette.HeaderBg, 0xf2);

        /// <summary>File-list sidebar background (translucent).</summary>
        public static RGBAColor32 FileListBg => WithAlpha(Palette.PanelBg, 0xf2);

        /// <summary>Info-panel background (translucent so the image shows faintly behind it).</summary>
        public static RGBAColor32 InfoPanelBg => WithAlpha(Palette.PanelBg, 0xd9);

        /// <summary>
        /// Histogram overlay background. Tints toward the extreme the image is NOT, so the plot
        /// stays legible over both a dense star field and a blown flat.
        /// </summary>
        public static RGBAColor32 HistogramBg => Palette.IsDark
            ? new RGBAColor32(0x00, 0x00, 0x00, 0x99)
            : new RGBAColor32(0xff, 0xff, 0xff, 0x99);

        private static RGBAColor32 WithAlpha(RGBAColor32 c, byte alpha) => new RGBAColor32(c.Red, c.Green, c.Blue, alpha);
    }
}
