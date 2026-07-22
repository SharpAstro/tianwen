using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Compact guide graph. Both the guide graph and the auto-focus V-curve are now paint-owning
    /// controls in their own files (<see cref="GuideGraphRenderer"/>, <see cref="VCurveChartRenderer"/>);
    /// this partial just routes the compact strip to the shared control.
    /// </summary>
    public partial class LiveSessionTab<TSurface>
    {
        /// <summary>
        /// PHD2-style guide graph, chromeless (no axis labels / legend). Delegates to the shared
        /// paint-owning <see cref="GuideGraphRenderer.Render{TSurface}"/> -- the full pane in
        /// <see cref="GuiderTab{TSurface}"/> is the same control with labels + legend enabled.
        /// </summary>
        private void RenderCompactGuideGraph(LiveSessionState state, RectF32 rect)
            => GuideGraphRenderer.Render(Renderer, rect, state.GuideSamples, state.LastGuideStats, DpiScale);
    }
}
