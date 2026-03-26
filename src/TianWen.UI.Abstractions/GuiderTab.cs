using System;
using System.Collections.Immutable;
using DIR.Lib;
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

        // Colors
        private static readonly RGBAColor32 ContentBg = new RGBAColor32(0x16, 0x16, 0x1e, 0xff);
        private static readonly RGBAColor32 PanelBg = new RGBAColor32(0x1e, 0x1e, 0x28, 0xff);
        private static readonly RGBAColor32 HeaderBg = new RGBAColor32(0x22, 0x22, 0x30, 0xff);
        private static readonly RGBAColor32 HeaderText = new RGBAColor32(0x88, 0xaa, 0xdd, 0xff);
        private static readonly RGBAColor32 BodyText = new RGBAColor32(0xcc, 0xcc, 0xcc, 0xff);
        private static readonly RGBAColor32 DimText = new RGBAColor32(0x88, 0x88, 0x88, 0xff);
        private static readonly RGBAColor32 ZeroLine = new RGBAColor32(0x44, 0x44, 0x55, 0xff);
        private static readonly RGBAColor32 RaColor = new RGBAColor32(0x44, 0x88, 0xff, 0xff);
        private static readonly RGBAColor32 DecColor = new RGBAColor32(0xff, 0x88, 0x44, 0xff);
        private static readonly RGBAColor32 PlaceholderText = new RGBAColor32(0x66, 0x66, 0x88, 0xff);

        public GuiderTabState State { get; } = new GuiderTabState();

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

            // Layout: graph (left) + stats panel (right)
            var bodyTop = contentRect.Y + headerH;
            var bodyHeight = contentRect.Height - headerH;
            var graphRect = new RectF32(contentRect.X, bodyTop, contentRect.Width - statsW, bodyHeight);
            var statsRect = new RectF32(contentRect.X + contentRect.Width - statsW, bodyTop, statsW, bodyHeight);

            RenderGraph(graphRect, dpiScale, fontPath, fontSize);
            RenderStats(statsRect, dpiScale, fontPath, fontSize, padding);
        }

        private void RenderGraph(RectF32 rect, float dpiScale, string fontPath, float fontSize)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, ContentBg);

            var samples = State.GuideSamples;
            if (samples.Length < 2)
            {
                DrawText("Waiting for guide data\u2026", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var padding = BasePadding * dpiScale;
            var plotX = rect.X + padding * 4; // room for Y-axis labels
            var plotY = rect.Y + padding;
            var plotW = rect.Width - padding * 5;
            var plotH = rect.Height - padding * 3;

            // Compute Y range
            var (min, max) = GuiderActions.ComputeGraphRange(samples);
            var range = max - min;

            // Zero line
            var zeroY = (int)(plotY + plotH * (1.0 - (0.0 - min) / range));
            for (var x = (int)plotX; x < (int)(plotX + plotW); x += 3)
            {
                FillRect(x, zeroY, 1, 1, ZeroLine);
            }

            // Y-axis labels
            DrawText($"{max:+0.0;-0.0}\"", fontPath,
                rect.X, plotY, padding * 4, fontSize * 1.2f,
                fontSize * 0.75f, DimText, TextAlign.Far, TextAlign.Near);
            DrawText("0\"", fontPath,
                rect.X, zeroY - fontSize * 0.6f, padding * 4, fontSize * 1.2f,
                fontSize * 0.75f, DimText, TextAlign.Far, TextAlign.Center);
            DrawText($"{min:+0.0;-0.0}\"", fontPath,
                rect.X, plotY + plotH - fontSize * 1.2f, padding * 4, fontSize * 1.2f,
                fontSize * 0.75f, DimText, TextAlign.Far, TextAlign.Far);

            // Plot RA and Dec as polylines (2px wide dots per sample)
            var samplesPerPixel = Math.Max(1.0, samples.Length / plotW);
            var dotSize = Math.Max(1, (int)(2 * dpiScale));

            for (var i = 0; i < samples.Length; i++)
            {
                var sample = samples[i];
                var x = (int)(plotX + (float)i / samples.Length * plotW);

                // RA point
                var raNorm = (sample.RaError - min) / range;
                var raY = (int)(plotY + plotH * (1.0 - raNorm));
                FillRect(x, raY, dotSize, dotSize, RaColor);

                // Dec point
                var decNorm = (sample.DecError - min) / range;
                var decY = (int)(plotY + plotH * (1.0 - decNorm));
                FillRect(x, decY, dotSize, dotSize, DecColor);
            }

            // Legend
            var legendY = rect.Y + rect.Height - padding * 2;
            FillRect((int)(plotX), (int)legendY, (int)(8 * dpiScale), (int)(3 * dpiScale), RaColor);
            DrawText("RA", fontPath,
                plotX + 10 * dpiScale, legendY - fontSize * 0.3f, 30 * dpiScale, fontSize,
                fontSize * 0.8f, RaColor, TextAlign.Near, TextAlign.Center);
            FillRect((int)(plotX + 50 * dpiScale), (int)legendY, (int)(8 * dpiScale), (int)(3 * dpiScale), DecColor);
            DrawText("Dec", fontPath,
                plotX + 60 * dpiScale, legendY - fontSize * 0.3f, 30 * dpiScale, fontSize,
                fontSize * 0.8f, DecColor, TextAlign.Near, TextAlign.Center);
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
            DrawRow("RA RMS:", $"{stats.RaRMS:F2}\"", RaColor);
            DrawRow("Dec RMS:", $"{stats.DecRMS:F2}\"", DecColor);
            cursor += lineH * 0.3f;
            DrawRow("Peak RA:", $"{stats.PeakRa:F2}\"");
            DrawRow("Peak Dec:", $"{stats.PeakDec:F2}\"");
            cursor += lineH * 0.3f;

            if (stats.LastRaErr.HasValue)
            {
                DrawRow("Last RA:", $"{stats.LastRaErr.Value:+0.00;-0.00}\"", RaColor);
                DrawRow("Last Dec:", $"{stats.LastDecErr ?? 0:+0.00;-0.00}\"", DecColor);
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
