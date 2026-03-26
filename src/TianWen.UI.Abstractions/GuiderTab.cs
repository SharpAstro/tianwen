using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic guider tab. Shows guide error graph (RA/Dec polylines),
    /// RMS stats panel, and placeholder states when not guiding.
    /// </summary>
    public class GuiderTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        // Layout constants (at 1x scale)
        private const float BaseFontSize = 14f;
        private const float BasePadding = 8f;
        private const float BaseHeaderHeight = 32f;
        private const float BaseStatsWidth = 220f;
        private const float BaseCameraFraction = 0.4f; // guide camera gets 40% of width

        // Colors
        private static readonly RGBAColor32 ContentBg = new RGBAColor32(0x16, 0x16, 0x1e, 0xff);
        private static readonly RGBAColor32 PanelBg = new RGBAColor32(0x1e, 0x1e, 0x28, 0xff);
        private static readonly RGBAColor32 HeaderBg = new RGBAColor32(0x22, 0x22, 0x30, 0xff);
        private static readonly RGBAColor32 HeaderText = new RGBAColor32(0x88, 0xaa, 0xdd, 0xff);
        private static readonly RGBAColor32 BodyText = new RGBAColor32(0xcc, 0xcc, 0xcc, 0xff);
        private static readonly RGBAColor32 DimText = new RGBAColor32(0x88, 0x88, 0x88, 0xff);
        private static readonly RGBAColor32 PlaceholderText = new RGBAColor32(0x66, 0x66, 0x88, 0xff);

        public GuiderTabState State { get; } = new GuiderTabState();

        /// <summary>Optional mini viewer widget for the guide camera image. Set by the host.</summary>
        public IMiniViewerWidget? GuideCameraViewer { get; set; }

        /// <summary>Tracks which guide frame reference is displayed to avoid redundant uploads.</summary>
        private Image? _displayedGuideFrame;

        public override bool HandleInput(InputEvent evt) => false;

        public void Render(
            LiveSessionState liveState,
            RectF32 contentRect,
            float dpiScale,
            string fontPath,
            TimeProvider timeProvider)
        {
            BeginFrame();
            State.PollFromLiveState(liveState);

            var fontSize = BaseFontSize * dpiScale;
            var padding = BasePadding * dpiScale;
            var headerH = BaseHeaderHeight * dpiScale;
            var statsW = BaseStatsWidth * dpiScale;

            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, ContentBg);

            // Header strip
            var headerRect = new RectF32(contentRect.X, contentRect.Y, contentRect.Width, headerH);
            FillRect(headerRect.X, headerRect.Y, headerRect.Width, headerRect.Height, HeaderBg);

            var placeholder = State.PlaceholderReason;
            if (placeholder is { } reason)
            {
                DrawText(GuiderActions.PlaceholderText(reason), fontPath,
                    headerRect.X + padding, headerRect.Y, headerRect.Width - padding * 2, headerRect.Height,
                    fontSize, PlaceholderText, TextAlign.Near, TextAlign.Center);

                // Large centered placeholder
                var bodyY = contentRect.Y + headerH;
                var bodyH = contentRect.Height - headerH;
                DrawText(GuiderActions.PlaceholderText(reason), fontPath,
                    contentRect.X, bodyY, contentRect.Width, bodyH,
                    fontSize * 1.5f, PlaceholderText, TextAlign.Center, TextAlign.Center);
                return;
            }

            // Header: guider state + RMS
            var guiderLabel = State.GuiderState ?? "Guiding";
            DrawText($"[{guiderLabel}]", fontPath,
                headerRect.X + padding, headerRect.Y, 200 * dpiScale, headerRect.Height,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
            DrawText(GuiderActions.FormatRmsSummary(State.LastGuideStats), fontPath,
                headerRect.X + padding + 200 * dpiScale, headerRect.Y,
                headerRect.Width - 200 * dpiScale - padding * 2, headerRect.Height,
                fontSize * 0.9f, BodyText, TextAlign.Far, TextAlign.Center);

            // Layout: top row = camera (left) + stats (right), bottom = full-width graph
            var bodyTop = contentRect.Y + headerH;
            var bodyHeight = contentRect.Height - headerH;
            var graphH = Math.Max(bodyHeight * 0.2f, 80f * dpiScale);
            var topH = bodyHeight - graphH;
            var cameraW = contentRect.Width - statsW;

            var cameraRect = new RectF32(contentRect.X, bodyTop, cameraW, topH);
            var statsRect = new RectF32(contentRect.X + cameraW, bodyTop, statsW, topH);
            var graphRect = new RectF32(contentRect.X, bodyTop + topH, contentRect.Width, graphH);

            RenderGuideCamera(cameraRect, dpiScale, fontPath, fontSize);
            RenderStats(statsRect, dpiScale, fontPath, fontSize, padding);
            RenderGraph(graphRect, dpiScale, fontPath, fontSize);
        }

        private static readonly RGBAColor32 CrosshairColor = new RGBAColor32(0x00, 0xff, 0x00, 0xaa);
        private static readonly RGBAColor32 CameraBg = new RGBAColor32(0x0a, 0x0a, 0x0a, 0xff);

        private void RenderGuideCamera(RectF32 rect, float dpiScale, string fontPath, float fontSize)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, CameraBg);

            var image = State.LastGuideFrame;

            // Queue new guide frame to the mini viewer if changed
            if (GuideCameraViewer is { } viewer)
            {
                if (image is not null && !ReferenceEquals(image, _displayedGuideFrame))
                {
                    _displayedGuideFrame = image;
                    viewer.QueueImage(image);
                }

                if (viewer.HasImage)
                {
                    viewer.Render(rect, Renderer.Width, Renderer.Height);

                    // Crosshair overlay on guide star position
                    if (State.GuideStarPosition is var (starX, starY) && image is not null)
                    {
                        var imgW = image.Width;
                        var imgH = image.Height;
                        var fitScale = Math.Min(rect.Width / imgW, rect.Height / imgH);
                        var drawW = imgW * fitScale;
                        var drawH = imgH * fitScale;
                        var offsetX = rect.X + (rect.Width - drawW) / 2;
                        var offsetY = rect.Y + (rect.Height - drawH) / 2;

                        var cx = (int)(offsetX + starX * fitScale);
                        var cy = (int)(offsetY + starY * fitScale);
                        var crossLen = (int)(15 * dpiScale);
                        var crossGap = (int)(4 * dpiScale);

                        FillRect(cx - crossLen, cy, crossLen - crossGap, 1, CrosshairColor);
                        FillRect(cx + crossGap, cy, crossLen - crossGap, 1, CrosshairColor);
                        FillRect(cx, cy - crossLen, 1, crossLen - crossGap, CrosshairColor);
                        FillRect(cx, cy + crossGap, 1, crossLen - crossGap, CrosshairColor);
                    }

                    // SNR label in corner
                    if (State.GuideStarSNR is { } snr)
                    {
                        DrawText($"SNR: {snr:F0}", fontPath,
                            rect.X + 4 * dpiScale, rect.Y + rect.Height - fontSize * 1.4f,
                            100 * dpiScale, fontSize * 1.2f,
                            fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Far);
                    }

                    return;
                }
            }

            // Fallback: no viewer or no image
            DrawText(State.IsRunning ? "Waiting for guide frame\u2026" : "No guide camera",
                fontPath, rect.X, rect.Y, rect.Width, rect.Height,
                fontSize, DimText, TextAlign.Center, TextAlign.Center);
        }

        private void RenderGraph(RectF32 rect, float dpiScale, string fontPath, float fontSize)
        {
            var samples = State.GuideSamples;
            if (samples.Length < 2)
            {
                FillRect(rect.X, rect.Y, rect.Width, rect.Height, ContentBg);
                DrawText("Waiting for guide data\u2026", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GuideGraphRenderer.GraphBg);

            var yScale = GuideGraphRenderer.ComputeYScale(State.LastGuideStats);
            var padding = BasePadding * dpiScale;
            var halfH = rect.Height / 2;
            var zeroY = rect.Y + halfH;

            // Grid lines
            for (var arcsec = 1; arcsec < (int)yScale; arcsec++)
            {
                var gridY = (float)(arcsec / yScale) * halfH;
                FillRect(rect.X, zeroY - gridY, rect.Width, 1, GuideGraphRenderer.GridColor);
                FillRect(rect.X, zeroY + gridY, rect.Width, 1, GuideGraphRenderer.GridColor);
            }
            FillRect(rect.X, zeroY, rect.Width, 1, GuideGraphRenderer.ZeroLineColor);

            // Y-axis labels
            var labelW = 40f * dpiScale;
            DrawText($"+{yScale:F0}\"", fontPath,
                rect.X, rect.Y, labelW, fontSize * 1.2f,
                fontSize * 0.75f, GuideGraphRenderer.ZeroLineColor, TextAlign.Near, TextAlign.Near);
            DrawText("0\"", fontPath,
                rect.X, zeroY - fontSize * 0.5f, labelW, fontSize,
                fontSize * 0.75f, GuideGraphRenderer.ZeroLineColor, TextAlign.Near, TextAlign.Center);
            DrawText($"-{yScale:F0}\"", fontPath,
                rect.X, rect.Y + rect.Height - fontSize * 1.2f, labelW, fontSize * 1.2f,
                fontSize * 0.75f, GuideGraphRenderer.ZeroLineColor, TextAlign.Near, TextAlign.Far);

            // Connected step-style lines
            var (startIdx, visibleCount, spacing) = GuideGraphRenderer.ComputeWindow(samples.Length, rect.Width, dpiScale);
            var lineW = Math.Max(dpiScale, 1f);

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

            // Legend
            var legendY = rect.Y + rect.Height - padding * 2;
            FillRect((int)(rect.X + padding), (int)legendY, (int)(8 * dpiScale), (int)(3 * dpiScale), GuideGraphRenderer.RaColor);
            DrawText("RA", fontPath,
                rect.X + padding + 10 * dpiScale, legendY - fontSize * 0.3f, 30 * dpiScale, fontSize,
                fontSize * 0.8f, GuideGraphRenderer.RaColor, TextAlign.Near, TextAlign.Center);
            FillRect((int)(rect.X + padding + 50 * dpiScale), (int)legendY, (int)(8 * dpiScale), (int)(3 * dpiScale), GuideGraphRenderer.DecColor);
            DrawText("Dec", fontPath,
                rect.X + padding + 60 * dpiScale, legendY - fontSize * 0.3f, 30 * dpiScale, fontSize,
                fontSize * 0.8f, GuideGraphRenderer.DecColor, TextAlign.Near, TextAlign.Center);
        }

        private void RenderStats(RectF32 rect, float dpiScale, string fontPath, float fontSize, float padding)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBg);

            var stats = State.LastGuideStats;
            var cursor = rect.Y + padding;
            var lineH = fontSize * 1.6f;
            var labelW = 90f * dpiScale;
            var valueX = rect.X + padding + labelW;
            var valueW = rect.Width - padding * 2 - labelW;

            // Header
            DrawText("Guide Stats", fontPath,
                rect.X + padding, cursor, rect.Width - padding * 2, lineH,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
            cursor += lineH * 1.2f;

            if (stats is null)
            {
                DrawText("No data", fontPath,
                    rect.X + padding, cursor, rect.Width - padding * 2, lineH,
                    fontSize * 0.9f, DimText, TextAlign.Near, TextAlign.Center);
                return;
            }

            // Stats rows
            void DrawRow(string label, string value, RGBAColor32? valueColor = null)
            {
                DrawText(label, fontPath, rect.X + padding, cursor, labelW, lineH,
                    fontSize * 0.9f, DimText, TextAlign.Near, TextAlign.Center);
                DrawText(value, fontPath, valueX, cursor, valueW, lineH,
                    fontSize * 0.9f, valueColor ?? BodyText, TextAlign.Near, TextAlign.Center);
                cursor += lineH;
            }

            DrawRow("Total RMS:", $"{stats.TotalRMS:F2}\"");
            DrawRow("RA RMS:", $"{stats.RaRMS:F2}\"", GuideGraphRenderer.RaColor);
            DrawRow("Dec RMS:", $"{stats.DecRMS:F2}\"", GuideGraphRenderer.DecColor);
            cursor += lineH * 0.3f;
            DrawRow("Peak RA:", $"{stats.PeakRa:F2}\"");
            DrawRow("Peak Dec:", $"{stats.PeakDec:F2}\"");
            cursor += lineH * 0.3f;

            if (stats.LastRaErr.HasValue)
            {
                DrawRow("Last RA:", $"{stats.LastRaErr.Value:+0.00;-0.00}\"", GuideGraphRenderer.RaColor);
                DrawRow("Last Dec:", $"{stats.LastDecErr ?? 0:+0.00;-0.00}\"", GuideGraphRenderer.DecColor);
                cursor += lineH * 0.3f;
            }

            if (State.GuideExposure > TimeSpan.Zero)
            {
                DrawRow("Exposure:", $"{State.GuideExposure.TotalSeconds:F1}s");
            }

            var settle = State.GuiderSettleProgress;
            if (settle is { Done: false })
            {
                DrawRow("Settle:", $"{settle.Distance:F2}\" / {settle.SettlePx:F2}\"");
            }
        }
    }
}
