using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
            if (_fontPath is null)
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

            if (document is not null)
            {
                var zoomPct = state.Zoom * 100f;
                statusParts.Add($"Zoom: {zoomPct:F0}%");
            }

            if (document?.Stars is { Count: > 0 } detectedStars)
            {
                statusParts.Add($"Stars: {detectedStars.Count}  HFR: {document.AverageHFR:F2}  FWHM: {document.AverageFWHM:F2}");
            }

            if (state.StatusMessage is { } msg)
            {
                statusParts.Add(msg);
            }

            var statusText = string.Join("  |  ", statusParts);
            RenderTextBar(statusText.AsSpan(), _fontPath!, sb.X, sb.Y, sb.Width, sb.Height,
                FontSize, ViewerTheme.StatusBarBg, ViewerTheme.Palette.BodyText,
                horizontalPadding: PanelPadding, alignX: TextAlign.Near, alignY: TextAlign.Near);
        }

    }
}
