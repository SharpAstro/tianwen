using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        /// <summary>
        /// The selected detected star's floating panel (#708), where the object panel floats: bottom-left
        /// of the image area, over the picture. The two selections are exclusive, so only one of them
        /// ever paints. Its rows are <see cref="StarInfoPanel.Lines"/>, its palette and Close are the
        /// object panel's own.
        /// </summary>
        private void RenderStarPanel(ViewerState state)
        {
            if (state.HideChrome
                || state.SelectedStar is not { } star
                || string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            var dpiScale = DpiScale;
            var area = _layout.ImageArea;
            var palette = SelectionPanelPalette;
            var lines = StarInfoPanel.Lines(in star, Findings.Wcs);

            var actions = new ObjectInfoPanel.PanelActions(
                Close: () =>
                {
                    state.SelectedStar = null;
                    state.NeedsRedraw = true;
                });

            var pw = ObjectInfoPanel.DesignWidth * dpiScale;
            var ph = StarInfoPanel.DesignHeight(lines.Length) * dpiScale;
            var px = area.X + (12f * dpiScale);
            var py = MathF.Max(area.Y, area.Y + area.Height - ph - (12f * dpiScale));

            // One no-op Clickable on the border rect swallows a press anywhere on the panel, so it never
            // falls through to a pan or a re-selection underneath (as the object panel does).
            RenderLayout(Layout.Builder.Spacer().Bg(palette.Border)
                    .Clickable(new HitResult.ButtonHit("StarPanelBackground"), _ => { }),
                new RectF32(px - 1, py - 1, pw + 2, ph + 2));
            RenderLayout(Layout.Builder.Spacer().Bg(palette.Background), new RectF32(px, py, pw, ph));

            var textX = px + (10f * dpiScale);
            RenderLayout(StarInfoPanel.BuildRows(lines, in palette),
                new RectF32(textX, py + (4f * dpiScale), pw - (40f * dpiScale), ph - (8f * dpiScale)), scale: Scale);

            var closeSize = ObjectInfoPanel.DesignCloseSize * dpiScale;
            if (ObjectInfoPanel.BuildCloseButton(in actions, in palette) is { } closeNode)
            {
                RenderLayout(closeNode, new RectF32(px + pw - closeSize, py, closeSize, closeSize), scale: Scale);
            }
        }
    }
}
