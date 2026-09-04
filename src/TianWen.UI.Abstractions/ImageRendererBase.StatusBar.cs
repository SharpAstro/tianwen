using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        // -----------------------------------------------------------------------
        // Status bar
        // -----------------------------------------------------------------------

        private void RenderStatusBar(AstroImageDocument? document, ViewerState state)
        {
            if (string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            var sb = _layout.StatusBar;

            var statusParts = new List<string>();

            if (document?.Wcs is { HasCDMatrix: true } wcs)
            {
                var scale = wcs.PixelScaleArcsec;
                var label = wcs.IsApproximate ? "approx" : "solved";
                var ra = CoordinateUtils.HoursToHMS(wcs.CenterRA);
                var dec = CoordinateUtils.DegreesToDMS(wcs.CenterDec);
                statusParts.Add($"WCS: {label} ({scale:F2}\"/px)  RA {ra}  Dec {dec}");
            }

            // No zoom readout here any more: the toolbar's Zoom button IS the readout (it reads "Fit",
            // "1:1" or the percentage), so a second copy down here said the same thing a screen-height
            // away from the control that changes it. Fit mode is the one case the button shows a word
            // rather than a number, and the button's tooltip carries the number for it.

            if (document?.Stars is { Count: > 0 } detectedStars)
            {
                statusParts.Add($"Stars: {detectedStars.Count}  HFR: {document.AverageHFR:F2}  FWHM: {document.AverageFWHM:F2}");
            }

            // Say when this frame is NOT being shown with its own stretch, and name the frame it is
            // borrowing. Without it "why is this sub darker than the last one?" has no answer on screen
            // -- the carry is invisible by design, so the only honest place to declare it is here.
            if (document?.DisplayAnchor is { } anchor)
            {
                statusParts.Add(state.IsBlinking
                    ? $"Blink | held to {Path.GetFileName(anchor.FilePath)}"
                    : $"Held to {Path.GetFileName(anchor.FilePath)}");
            }
            else if (state.IsBlinking)
            {
                statusParts.Add("Blink");
            }

            if (state.StatusMessage is { } msg)
            {
                statusParts.Add(msg);
            }

            var statusText = string.Join("  |  ", statusParts);
            RenderTextBar(statusText.AsSpan(), FontPath, sb.X, sb.Y, sb.Width, sb.Height,
                FontSize, ViewerTheme.StatusBarBg, ViewerTheme.Palette.BodyText,
                horizontalPadding: PanelPadding, alignX: TextAlign.Near, alignY: TextAlign.Near);
        }

    }
}
