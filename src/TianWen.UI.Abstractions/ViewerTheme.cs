using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The FITS viewer's own chrome theme -- the single source of truth for the colours that were
    /// previously hardcoded as inline float-RGB literals throughout <see cref="ImageRendererBase{TSurface}"/>.
    /// Distinct from <see cref="GuiTheme"/>: the viewer runs at an 18px base font (vs the GUI's 14px) and
    /// has its own panel backgrounds (toolbar / status bar / file list / info panel / histogram) that are
    /// alpha-blended over the image, so they don't map onto the shared 8-role <see cref="UiPalette"/>.
    /// Values match the pre-existing literals (via <see cref="RGBAColor32.FromFloat"/>) so adopting the
    /// theme is a pure dedup with no visual change. App/feature-specific colours (grid labels, star/object
    /// overlays, plate-solve markers, toolbar hover lerps) deliberately stay local to their draw site.
    /// </summary>
    public static class ViewerTheme
    {
        /// <summary>Shared chrome colour roles (the values the info panel + status bar text use).</summary>
        public static UiPalette Palette { get; } = new(
            ContentBg:  RGBAColor32.FromFloat(0.10f, 0.10f, 0.10f, 1f),  // window clear behind the image
            PanelBg:    RGBAColor32.FromFloat(0.15f, 0.15f, 0.15f, 1f),  // opaque panel base
            HeaderBg:   RGBAColor32.FromFloat(0.18f, 0.18f, 0.20f, 1f),  // toolbar strip
            HeaderText: RGBAColor32.FromFloat(0.60f, 0.80f, 1.00f, 1f),  // cyan section headers ("-- Metadata --")
            BodyText:   RGBAColor32.FromFloat(0.90f, 0.90f, 0.90f, 1f),  // primary panel text
            DimText:    RGBAColor32.FromFloat(0.70f, 0.70f, 0.70f, 1f),  // secondary / controls help
            Separator:  RGBAColor32.FromFloat(0.30f, 0.30f, 0.35f, 1f),  // divider lines
            Selection:  RGBAColor32.FromFloat(0.25f, 0.35f, 0.55f, 1f)); // selected file / active button

        /// <summary>Base (unscaled) layout metrics. The viewer renders at an 18px base font.</summary>
        public static UiMetrics Metrics { get; } = new(
            BaseFontSize: 18f,
            Padding:      6f,
            HeaderHeight: 40f,   // toolbar
            ItemHeight:   24f,   // status bar / list row
            ButtonHeight: 28f);

        /// <summary>The combined viewer theme (palette + metrics).</summary>
        public static UiTheme Theme { get; } = new(Palette, Metrics);

        // Viewer-specific panel fills, alpha-blended over the rendered image (so they don't fit the
        // opaque shared-palette roles). Values match the literals previously inlined in the renderer.
        /// <summary>Toolbar strip background (opaque).</summary>
        public static RGBAColor32 ToolbarBg { get; } = RGBAColor32.FromFloat(0.18f, 0.18f, 0.20f, 1f);
        /// <summary>Status bar background (slightly translucent).</summary>
        public static RGBAColor32 StatusBarBg { get; } = RGBAColor32.FromFloat(0.20f, 0.20f, 0.20f, 0.95f);
        /// <summary>File-list sidebar background (translucent).</summary>
        public static RGBAColor32 FileListBg { get; } = RGBAColor32.FromFloat(0.13f, 0.13f, 0.15f, 0.95f);
        /// <summary>Info-panel background (translucent so the image shows faintly behind it).</summary>
        public static RGBAColor32 InfoPanelBg { get; } = RGBAColor32.FromFloat(0.15f, 0.15f, 0.15f, 0.85f);
        /// <summary>Histogram overlay background (mostly transparent black).</summary>
        public static RGBAColor32 HistogramBg { get; } = RGBAColor32.FromFloat(0f, 0f, 0f, 0.6f);
    }
}
