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

            var palette = SelectionPanelPalette;
            var lines = StarInfoPanel.Lines(in star, Findings.Wcs);

            var actions = new ObjectInfoPanel.PanelActions(
                Close: () =>
                {
                    state.SelectedStar = null;
                    state.NeedsRedraw = true;
                });

            // Floated bottom-left of the image area by the engine, which measures the panel and keeps it
            // inside the area; the panel's own border node swallows a press anywhere on it.
            RenderLayout(
                Layout.Builder.Overlay(
                    Layout.Builder.Spacer(),
                    Layout.Builder.Anchored(StarInfoPanel.Build(lines, in palette, in actions),
                        Layout.DockSide.Bottom, offsetAlong: 12f, margin: 12f)),
                _layout.ImageArea, scale: Scale);
        }
    }
}
