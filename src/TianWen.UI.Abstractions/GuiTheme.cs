using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// TianWen's concrete dark UI theme: the single source of truth for the shared chrome
    /// colours and base metrics that were previously duplicated as <c>private static readonly</c>
    /// constants across every tab. Tab-specific colours (sky map, guide graph, plate-solve
    /// overlays, steppers, planner pins) deliberately stay local to their owner -- only the
    /// genuinely-shared chrome roles live here.
    /// </summary>
    public static class GuiTheme
    {
        /// <summary>Shared chrome colour roles. Values are the pre-existing dark scheme.</summary>
        public static UiPalette Palette { get; } = new(
            ContentBg:  new RGBAColor32(0x16, 0x16, 0x1e, 0xff),
            PanelBg:    new RGBAColor32(0x1e, 0x1e, 0x28, 0xff),
            HeaderBg:   new RGBAColor32(0x22, 0x22, 0x30, 0xff),
            HeaderText: new RGBAColor32(0x88, 0xaa, 0xdd, 0xff),
            BodyText:   new RGBAColor32(0xcc, 0xcc, 0xcc, 0xff),
            DimText:    new RGBAColor32(0x88, 0x88, 0x88, 0xff),
            Separator:  new RGBAColor32(0x33, 0x33, 0x44, 0xff),
            Selection:  new RGBAColor32(0x20, 0x30, 0x50, 0xff));

        /// <summary>Shared base (unscaled) layout metrics.</summary>
        public static UiMetrics Metrics { get; } = new(
            BaseFontSize: 14f,
            Padding:      8f,
            HeaderHeight: 28f,
            ItemHeight:   24f,
            ButtonHeight: 28f);

        /// <summary>The combined theme (palette + metrics).</summary>
        public static UiTheme Theme { get; } = new(Palette, Metrics);
    }
}
