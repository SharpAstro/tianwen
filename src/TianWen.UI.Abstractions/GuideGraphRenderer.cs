using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Shared, paint-owning guide error graph control. PHD2-style connected lines for RA (blue) and
/// Dec (orange), dynamic Y scale, scrolling window, grid lines at integer arcsec, correction bars,
/// settling shading, and dither markers. The <see cref="Render{TSurface}"/> entry point draws the
/// whole graph onto any <see cref="Renderer{TSurface}"/> (the same seam as
/// <see cref="AltitudeChartRenderer"/>); the static colour + math members remain the shared source
/// for callers that need the palette (e.g. the guide-stats panel). Used by both the compact
/// <see cref="LiveSessionTab{TSurface}"/> strip and the full <see cref="GuiderTab{TSurface}"/> pane.
/// </summary>
public static class GuideGraphRenderer
{
    public static readonly RGBAColor32 GraphBg = new RGBAColor32(0x12, 0x12, 0x1a, 0xff);
    public static readonly RGBAColor32 GridColor = new RGBAColor32(0x33, 0x33, 0x44, 0xff);
    public static readonly RGBAColor32 ZeroLineColor = new RGBAColor32(0x55, 0x55, 0x66, 0xff);
    public static readonly RGBAColor32 RaColor = new RGBAColor32(0x44, 0x88, 0xff, 0xff);
    public static readonly RGBAColor32 DecColor = new RGBAColor32(0xff, 0x88, 0x44, 0xff);
    public static readonly RGBAColor32 DitherMarkerColor = new RGBAColor32(0xff, 0xff, 0x44, 0x88);
    public static readonly RGBAColor32 SettlingShadeColor = new RGBAColor32(0x44, 0x44, 0x00, 0x30);
    public static readonly RGBAColor32 RaCorrectionColor = new RGBAColor32(0x44, 0x88, 0xff, 0x55);
    public static readonly RGBAColor32 DecCorrectionColor = new RGBAColor32(0xff, 0x88, 0x44, 0x55);

    /// <summary>
    /// Computes the bar height fraction [0, 1] for a correction pulse using log scale.
    /// A 1ms correction → ~0.15, 10ms → ~0.45, 100ms → ~0.75, 1000ms → ~1.0.
    /// Log scale ensures even small corrections (2-5ms) are visible.
    /// </summary>
    public static float CorrectionBarFraction(double pulseMs)
    {
        if (pulseMs == 0) return 0;
        // log10(1)=0, log10(1000)=3 → normalize to [0, 1] over range [1ms, 1000ms]
        var logVal = Math.Log10(Math.Max(Math.Abs(pulseMs), 1.0));
        return (float)Math.Clamp(logVal / 3.0, 0.05, 1.0); // minimum 5% so tiny corrections are visible
    }

    /// <summary>
    /// Computes the Y scale from the visible window samples, ignoring settling spikes.
    /// Falls back to guide stats if no samples are visible.
    /// </summary>
    public static double ComputeYScale(GuideStats? stats, ImmutableArray<GuideErrorSample> samples, int startIdx, int visibleCount)
    {
        var peakErr = 0.5;

        // Compute peak from visible window, skipping settling samples (dither spikes)
        for (var i = startIdx; i < startIdx + visibleCount; i++)
        {
            var s = samples[i];
            if (s.IsSettling) continue; // don't let settling spikes blow out the scale
            peakErr = Math.Max(peakErr, Math.Max(Math.Abs(s.RaError), Math.Abs(s.DecError)));
        }

        // If all visible samples were settling, fall back to stats
        if (peakErr <= 0.5 && stats is { } gs)
        {
            // Use RMS * 4 as a reasonable peak estimate (not the all-time peak which includes dither spikes)
            peakErr = Math.Max(gs.RaRMS, gs.DecRMS) * 4;
        }

        return peakErr < 0.3 ? 0.5
            : peakErr < 0.7 ? 1.0
            : peakErr < 1.5 ? 2.0
            : peakErr < 3.0 ? 4.0
            : peakErr < 6.0 ? 8.0
            : 12.0;
    }

    /// <summary>
    /// Computes the Y scale from guide stats (legacy overload for compact graphs without sample access).
    /// </summary>
    public static double ComputeYScale(GuideStats? stats)
    {
        var peakErr = 1.0;
        if (stats is { } gs)
        {
            // Use RMS * 4 as a reasonable peak — avoids dither spikes locking the scale
            peakErr = Math.Max(gs.RaRMS, gs.DecRMS) * 4;
            peakErr = Math.Max(peakErr, 0.5);
        }
        return peakErr < 0.3 ? 0.5
            : peakErr < 0.7 ? 1.0
            : peakErr < 1.5 ? 2.0
            : peakErr < 3.0 ? 4.0
            : peakErr < 6.0 ? 8.0
            : 12.0;
    }

    /// <summary>
    /// Computes the scrolling window parameters.
    /// </summary>
    public static (int StartIdx, int VisibleCount, float SampleSpacing) ComputeWindow(
        int sampleCount, float width, float dpiScale)
    {
        var sampleSpacing = Math.Max(5f * dpiScale, 6f);
        var maxVisible = (int)(width / sampleSpacing);
        var startIdx = Math.Max(0, sampleCount - maxVisible);
        var visibleCount = sampleCount - startIdx;
        return (startIdx, visibleCount, sampleSpacing);
    }

    /// <summary>
    /// Converts an error value to a Y pixel coordinate.
    /// </summary>
    public static float ErrorToY(double error, double yScale, float zeroY, float halfH)
    {
        var clamped = Math.Clamp(error, -yScale, yScale);
        return zeroY - (float)(clamped / yScale * halfH);
    }

    /// <summary>
    /// Paints the complete PHD2-style guide error graph into <paramref name="rect"/> on any
    /// <see cref="Renderer{TSurface}"/>: background, arcsec grid + zero line, per-sample settling
    /// shading, RA/Dec correction bars, connected RA/Dec error step-lines, and dither markers.
    /// This is the single source of truth for the guide graph -- both the full
    /// <see cref="GuiderTab{TSurface}"/> pane and the compact <see cref="LiveSessionTab{TSurface}"/>
    /// strip call it (the draw loop used to be inlined + duplicated in both). Pass
    /// <paramref name="showAxisLabels"/> / <paramref name="showLegend"/> (with a real
    /// <paramref name="fontPath"/> + <paramref name="fontSize"/>) for the full pane; omit them for the
    /// chromeless compact strip. Fills its own background, so the caller must not pre-fill the rect.
    /// </summary>
    public static void Render<TSurface>(
        Renderer<TSurface> renderer,
        RectF32 rect,
        ImmutableArray<GuideErrorSample> samples,
        GuideStats? stats,
        float dpiScale,
        string? fontPath = null,
        float fontSize = 0f,
        bool showAxisLabels = false,
        bool showLegend = false)
    {
        FillRect(renderer, rect.X, rect.Y, rect.Width, rect.Height, GraphBg);

        if (samples.Length < 2)
        {
            return;
        }

        var halfH = rect.Height / 2;
        var zeroY = rect.Y + halfH;

        // Compute the scrolling window first so the Y scale can use the visible samples.
        var (startIdx, visibleCount, spacing) = ComputeWindow(samples.Length, rect.Width, dpiScale);
        var yScale = ComputeYScale(stats, samples, startIdx, visibleCount);

        // Grid lines at integer arcsec + the zero line.
        for (var arcsec = 1; arcsec < (int)yScale; arcsec++)
        {
            var gridY = (float)(arcsec / yScale) * halfH;
            FillRect(renderer, rect.X, zeroY - gridY, rect.Width, 1, GridColor);
            FillRect(renderer, rect.X, zeroY + gridY, rect.Width, 1, GridColor);
        }
        FillRect(renderer, rect.X, zeroY, rect.Width, 1, ZeroLineColor);

        // Y-axis labels (full pane only).
        if (showAxisLabels && !string.IsNullOrEmpty(fontPath))
        {
            var labelW = 40f * dpiScale;
            DrawText(renderer, $"+{yScale:F0}\"", fontPath,
                rect.X, rect.Y, labelW, fontSize * 1.2f,
                fontSize * 0.75f, ZeroLineColor, TextAlign.Near, TextAlign.Near);
            DrawText(renderer, "0\"", fontPath,
                rect.X, zeroY - fontSize * 0.5f, labelW, fontSize,
                fontSize * 0.75f, ZeroLineColor, TextAlign.Near, TextAlign.Center);
            DrawText(renderer, $"-{yScale:F0}\"", fontPath,
                rect.X, rect.Y + rect.Height - fontSize * 1.2f, labelW, fontSize * 1.2f,
                fontSize * 0.75f, ZeroLineColor, TextAlign.Near, TextAlign.Far);
        }

        var lineW = Math.Max(dpiScale, 1f);

        // Pass 1: settling shading (behind everything).
        for (var i = 0; i < visibleCount; i++)
        {
            if (samples[startIdx + i].IsSettling)
            {
                var sx = rect.X + i * spacing;
                FillRect(renderer, sx, rect.Y, spacing + 1, rect.Height, SettlingShadeColor);
            }
        }

        // Pass 2: correction bars (behind the error lines, on top of the shading).
        var barW = Math.Max(spacing * 0.3f, 1f);
        for (var i = 0; i < visibleCount; i++)
        {
            var sample = samples[startIdx + i];
            var bx = rect.X + i * spacing;

            if (sample.RaCorrectionMs != 0)
            {
                // Bar extends up for positive (West), down for negative (East).
                var barH = CorrectionBarFraction(sample.RaCorrectionMs) * halfH;
                if (sample.RaCorrectionMs > 0)
                    FillRect(renderer, bx, zeroY - barH, barW, barH, RaCorrectionColor);
                else
                    FillRect(renderer, bx, zeroY, barW, barH, RaCorrectionColor);
            }
            if (sample.DecCorrectionMs != 0)
            {
                var barH = CorrectionBarFraction(sample.DecCorrectionMs) * halfH;
                var dbx = bx + barW + 1; // offset Dec bars slightly right of RA bars
                if (sample.DecCorrectionMs > 0)
                    FillRect(renderer, dbx, zeroY - barH, barW, barH, DecCorrectionColor);
                else
                    FillRect(renderer, dbx, zeroY, barW, barH, DecCorrectionColor);
            }
        }

        // Pass 3: connected RA/Dec error step-lines (horizontal segment + vertical connector).
        for (var i = 1; i < visibleCount; i++)
        {
            var x1 = rect.X + (i - 1) * spacing;
            var x2 = rect.X + i * spacing;

            var raY1 = ErrorToY(samples[startIdx + i - 1].RaError, yScale, zeroY, halfH);
            var raY2 = ErrorToY(samples[startIdx + i].RaError, yScale, zeroY, halfH);
            FillRect(renderer, x1, raY1, x2 - x1, lineW, RaColor);
            FillRect(renderer, x2, Math.Min(raY1, raY2), lineW, Math.Abs(raY2 - raY1) + lineW, RaColor);

            var decY1 = ErrorToY(samples[startIdx + i - 1].DecError, yScale, zeroY, halfH);
            var decY2 = ErrorToY(samples[startIdx + i].DecError, yScale, zeroY, halfH);
            FillRect(renderer, x1, decY1, x2 - x1, lineW, DecColor);
            FillRect(renderer, x2, Math.Min(decY1, decY2), lineW, Math.Abs(decY2 - decY1) + lineW, DecColor);
        }

        // Pass 4: dither markers (dashed vertical line, on top of everything).
        for (var i = 0; i < visibleCount; i++)
        {
            if (samples[startIdx + i].IsDither)
            {
                var dx = rect.X + i * spacing;
                for (var dy = rect.Y; dy < rect.Y + rect.Height; dy += 6 * dpiScale)
                {
                    FillRect(renderer, dx, dy, Math.Max(1, lineW), 3 * dpiScale, DitherMarkerColor);
                }
            }
        }

        // Legend (full pane only): RA/Dec colour swatches + labels.
        if (showLegend && !string.IsNullOrEmpty(fontPath))
        {
            var padding = GuiTheme.Metrics.Padding * dpiScale;
            var legendY = rect.Y + rect.Height - padding * 2;
            FillRect(renderer, (int)(rect.X + padding), (int)legendY, (int)(8 * dpiScale), (int)(3 * dpiScale), RaColor);
            DrawText(renderer, "RA", fontPath,
                rect.X + padding + 10 * dpiScale, legendY - fontSize * 0.3f, 30 * dpiScale, fontSize,
                fontSize * 0.8f, RaColor, TextAlign.Near, TextAlign.Center);
            FillRect(renderer, (int)(rect.X + padding + 50 * dpiScale), (int)legendY, (int)(8 * dpiScale), (int)(3 * dpiScale), DecColor);
            DrawText(renderer, "Dec", fontPath,
                rect.X + padding + 60 * dpiScale, legendY - fontSize * 0.3f, 30 * dpiScale, fontSize,
                fontSize * 0.8f, DecColor, TextAlign.Near, TextAlign.Center);
        }
    }

    // --- Drawing helpers (float -> RectInt wrappers, byte-identical to PixelWidgetBase's) ---

    private static void FillRect<TSurface>(Renderer<TSurface> renderer, float x, float y, float w, float h, RGBAColor32 color)
    {
        if (w <= 0 || h <= 0) return;
        renderer.FillRectangle(
            new RectInt(new PointInt((int)(x + w), (int)(y + h)), new PointInt((int)x, (int)y)),
            color);
    }

    private static void DrawText<TSurface>(Renderer<TSurface> renderer, ReadOnlySpan<char> text, string fontPath,
        float x, float y, float w, float h, float fontSize, RGBAColor32 color, TextAlign horizAlign, TextAlign vertAlign)
    {
        if (string.IsNullOrEmpty(fontPath)) return;
        renderer.DrawText(text, fontPath, fontSize, color,
            new RectInt(new PointInt((int)(x + w), (int)(y + h)), new PointInt((int)x, (int)y)),
            horizAlign, vertAlign);
    }
}
