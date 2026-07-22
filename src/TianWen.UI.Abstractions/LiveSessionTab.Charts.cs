using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Sequencing.PolarAlignment;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Compact guide graph and auto-focus V-curve chart.
    /// </summary>
    public partial class LiveSessionTab<TSurface>
    {
        /// <summary>
        /// PHD2-style guide graph using shared <see cref="GuideGraphRenderer"/> helpers.
        /// </summary>
        private void RenderCompactGuideGraph(LiveSessionState state, RectF32 rect)
        {
            var dpiScale = DpiScale;
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GuideGraphRenderer.GraphBg);

            var samples = state.GuideSamples;
            var halfH = rect.Height / 2;
            var zeroY = rect.Y + halfH;

            if (samples.Length < 2) return;

            var (startIdx, visibleCount, spacing) = GuideGraphRenderer.ComputeWindow(samples.Length, rect.Width, dpiScale);
            var yScale = GuideGraphRenderer.ComputeYScale(state.LastGuideStats, samples, startIdx, visibleCount);

            // Grid lines
            for (var arcsec = 1; arcsec < (int)yScale; arcsec++)
            {
                var gridY = (float)(arcsec / yScale) * halfH;
                FillRect(rect.X, zeroY - gridY, rect.Width, 1, GuideGraphRenderer.GridColor);
                FillRect(rect.X, zeroY + gridY, rect.Width, 1, GuideGraphRenderer.GridColor);
            }
            FillRect(rect.X, zeroY, rect.Width, 1, GuideGraphRenderer.ZeroLineColor);
            var lineW = Math.Max(dpiScale, 1f);

            // Settling shading + correction bars + dither markers (behind lines)
            var barW = Math.Max(spacing * 0.3f, 1f);
            for (var i = 0; i < visibleCount; i++)
            {
                var sample = samples[startIdx + i];
                var sx = rect.X + i * spacing;
                if (sample.IsSettling)
                {
                    FillRect(sx, rect.Y, spacing + 1, rect.Height, GuideGraphRenderer.SettlingShadeColor);
                }
                if (sample.RaCorrectionMs != 0)
                {
                    var cbH = GuideGraphRenderer.CorrectionBarFraction(sample.RaCorrectionMs) * halfH;
                    if (sample.RaCorrectionMs > 0)
                        FillRect(sx, zeroY - cbH, barW, cbH, GuideGraphRenderer.RaCorrectionColor);
                    else
                        FillRect(sx, zeroY, barW, cbH, GuideGraphRenderer.RaCorrectionColor);
                }
                if (sample.DecCorrectionMs != 0)
                {
                    var cbH = GuideGraphRenderer.CorrectionBarFraction(sample.DecCorrectionMs) * halfH;
                    var dbx = sx + barW + 1;
                    if (sample.DecCorrectionMs > 0)
                        FillRect(dbx, zeroY - cbH, barW, cbH, GuideGraphRenderer.DecCorrectionColor);
                    else
                        FillRect(dbx, zeroY, barW, cbH, GuideGraphRenderer.DecCorrectionColor);
                }
                if (sample.IsDither)
                {
                    for (var dy = rect.Y; dy < rect.Y + rect.Height; dy += 6 * dpiScale)
                    {
                        FillRect(sx, dy, Math.Max(1, lineW), 3 * dpiScale, GuideGraphRenderer.DitherMarkerColor);
                    }
                }
            }

            for (var i = 1; i < visibleCount; i++)
            {
                var x1 = rect.X + (i - 1) * spacing;
                var x2 = rect.X + i * spacing;

                var raY1 = GuideGraphRenderer.ErrorToY(samples[startIdx + i - 1].RaError, yScale, zeroY, halfH);
                var raY2 = GuideGraphRenderer.ErrorToY(samples[startIdx + i].RaError, yScale, zeroY, halfH);
                FillRect(x1, raY1, x2 - x1, lineW, GuideGraphRenderer.RaColor);
                FillRect(x2, Math.Min(raY1, raY2), lineW, Math.Abs(raY2 - raY1) + lineW, GuideGraphRenderer.RaColor);

                var decY1 = GuideGraphRenderer.ErrorToY(samples[startIdx + i - 1].DecError, yScale, zeroY, halfH);
                var decY2 = GuideGraphRenderer.ErrorToY(samples[startIdx + i].DecError, yScale, zeroY, halfH);
                FillRect(x1, decY1, x2 - x1, lineW, GuideGraphRenderer.DecColor);
                FillRect(x2, Math.Min(decY1, decY2), lineW, Math.Abs(decY2 - decY1) + lineW, GuideGraphRenderer.DecColor);
            }
        }

        // -----------------------------------------------------------------------
        // Main: per-OTA panels with live exposure state
        // -----------------------------------------------------------------------

        /// <summary>Renders V-curve chart: scatter dots for measured HFD + fitted hyperbola curve.</summary>
        private void RenderVCurveChart(
            ImmutableArray<(int Position, float Hfd)> samples,
            FocusRunRecord? completedRun,
            RectF32 rect, float fontSize)
        {
            var fontPath = FontPath;
            var dpiScale = DpiScale;
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GraphBg);

            if (samples.Length < 2) return;

            // Compute data bounds
            var minPos = int.MaxValue;
            var maxPos = int.MinValue;
            var minHfd = float.MaxValue;
            var maxHfd = float.MinValue;
            foreach (var (pos, hfd) in samples)
            {
                if (pos < minPos) minPos = pos;
                if (pos > maxPos) maxPos = pos;
                if (hfd < minHfd) minHfd = hfd;
                if (hfd > maxHfd) maxHfd = hfd;
            }

            if (maxPos <= minPos || maxHfd <= minHfd) return;

            var margin = 4f * dpiScale;
            var chartX = rect.X + margin;
            var chartY = rect.Y + margin;
            var chartW = rect.Width - margin * 2;
            var chartH = rect.Height - margin * 2 - fontSize; // leave room for axis label

            // Add padding so dots don't sit on the axis edges
            var hfdRange = maxHfd - minHfd;
            if (hfdRange < 0.5f) hfdRange = 0.5f; // minimum range to avoid noise magnification
            minHfd -= hfdRange * 0.15f;
            maxHfd += hfdRange * 0.15f;
            if (minHfd < 0) minHfd = 0;
            hfdRange = maxHfd - minHfd;

            var posRange = maxPos - minPos;
            var posPad = Math.Max(posRange * 0.05, 1);
            minPos -= (int)posPad;
            maxPos += (int)posPad;
            posRange = maxPos - minPos;

            float PosToX(double pos) => chartX + (float)((pos - minPos) / posRange) * chartW;
            float HfdToY(double hfd) => chartY + chartH - (float)((hfd - minHfd) / hfdRange) * chartH;

            // Axis lines
            FillRect(chartX, chartY + chartH, chartW, 1, VCurveAxisColor); // X axis
            FillRect(chartX, chartY, 1, chartH, VCurveAxisColor);          // Y axis

            // Scatter dots
            var dotR = 3f * dpiScale;
            foreach (var (pos, hfd) in samples)
            {
                var dx = PosToX(pos);
                var dy = HfdToY(hfd);
                FillRect(dx - dotR, dy - dotR, dotR * 2, dotR * 2, VCurveDotColor);
            }

            // Fitted hyperbola curve (if we have fit parameters)
            if (completedRun is { FitA: var a, FitB: var b, BestPosition: var bestPos }
                && !double.IsNaN(a) && !double.IsNaN(b) && b > 0)
            {
                // Draw smooth curve
                var steps = (int)chartW;
                var lineW = Math.Max(1f, dpiScale);
                for (var i = 0; i < steps; i++)
                {
                    var xPos = minPos + (double)i / steps * posRange;
                    var yVal = TianWen.Lib.Astrometry.Focus.Hyperbola.CalculateValueAtPosition(xPos, bestPos, a, b);
                    var px = PosToX(xPos);
                    var py = HfdToY(yVal);
                    if (py >= chartY && py <= chartY + chartH)
                    {
                        FillRect(px, py, lineW, lineW, VCurveFitColor);
                    }
                }

                // Best focus vertical line
                var bestX = PosToX(bestPos);
                FillRect(bestX, chartY, 1, chartH, VCurveBestColor);

                // Best focus label
                DrawText($"\u2193 {bestPos} (HFD {a:F2})", fontPath,
                    bestX + 2 * dpiScale, chartY, chartW * 0.4f, fontSize,
                    fontSize * 0.8f, VCurveBestColor, TextAlign.Near, TextAlign.Near);
            }

            // X-axis label
            DrawText($"Focuser position ({minPos}\u2013{maxPos})", fontPath,
                chartX, chartY + chartH + 1, chartW, fontSize,
                fontSize * 0.75f, DimText, TextAlign.Center, TextAlign.Near);
        }
    }
}
