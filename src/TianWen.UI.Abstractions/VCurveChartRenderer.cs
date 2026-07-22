using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Paint-owning auto-focus V-curve chart: a scatter of measured HFD vs focuser position plus the
/// fitted hyperbola curve and the best-focus marker. Draws the whole chart onto any
/// <see cref="Renderer{TSurface}"/> (the same seam as <see cref="AltitudeChartRenderer"/> /
/// <see cref="GuideGraphRenderer"/>); the draw loop used to be inlined in
/// <see cref="LiveSessionTab{TSurface}"/>. Fills its own background, so the caller must not pre-fill.
/// </summary>
public static class VCurveChartRenderer
{
    private static readonly RGBAColor32 GraphBg   = new RGBAColor32(0x12, 0x12, 0x1a, 0xff);
    private static readonly RGBAColor32 AxisColor = new RGBAColor32(0x44, 0x44, 0x55, 0xff); // dim axis lines
    private static readonly RGBAColor32 DotColor  = new RGBAColor32(0x44, 0xcc, 0xff, 0xff); // cyan dots
    private static readonly RGBAColor32 FitColor  = new RGBAColor32(0xff, 0x66, 0x33, 0xcc); // orange-red fit curve
    private static readonly RGBAColor32 BestColor = new RGBAColor32(0x44, 0xff, 0x44, 0xaa); // green best-focus line

    /// <summary>Renders the V-curve: scatter dots for measured HFD + the fitted hyperbola + best-focus line.</summary>
    public static void Render<TSurface>(
        Renderer<TSurface> renderer,
        RectF32 rect,
        ImmutableArray<(int Position, float Hfd)> samples,
        FocusRunRecord? completedRun,
        float dpiScale,
        string fontPath,
        float fontSize)
    {
        FillRect(renderer, rect.X, rect.Y, rect.Width, rect.Height, GraphBg);

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
        FillRect(renderer, chartX, chartY + chartH, chartW, 1, AxisColor); // X axis
        FillRect(renderer, chartX, chartY, 1, chartH, AxisColor);          // Y axis

        // Scatter dots
        var dotR = 3f * dpiScale;
        foreach (var (pos, hfd) in samples)
        {
            var dx = PosToX(pos);
            var dy = HfdToY(hfd);
            FillRect(renderer, dx - dotR, dy - dotR, dotR * 2, dotR * 2, DotColor);
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
                var yVal = Hyperbola.CalculateValueAtPosition(xPos, bestPos, a, b);
                var px = PosToX(xPos);
                var py = HfdToY(yVal);
                if (py >= chartY && py <= chartY + chartH)
                {
                    FillRect(renderer, px, py, lineW, lineW, FitColor);
                }
            }

            // Best focus vertical line
            var bestX = PosToX(bestPos);
            FillRect(renderer, bestX, chartY, 1, chartH, BestColor);

            // Best focus label
            DrawText(renderer, $"↓ {bestPos} (HFD {a:F2})", fontPath,
                bestX + 2 * dpiScale, chartY, chartW * 0.4f, fontSize,
                fontSize * 0.8f, BestColor, TextAlign.Near, TextAlign.Near);
        }

        // X-axis label
        DrawText(renderer, $"Focuser position ({minPos}–{maxPos})", fontPath,
            chartX, chartY + chartH + 1, chartW, fontSize,
            fontSize * 0.75f, GuiTheme.Palette.DimText, TextAlign.Center, TextAlign.Near);
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
