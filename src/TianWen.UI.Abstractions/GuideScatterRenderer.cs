using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Paint-owning PHD2-style guide target view: a 2D scatter of RA (X) vs Dec (Y) error with concentric
/// arcsec rings, a sweet-spot disc, a centre crosshair, and an RMS circle. Recent samples plot bright,
/// older ones dim; the latest sample gets a larger marker. Draws onto any
/// <see cref="Renderer{TSurface}"/> (the same seam as <see cref="GuideGraphRenderer"/>); the draw loop
/// used to be inlined in <see cref="GuiderTab{TSurface}"/>. The pane background is a layout <c>.Bg</c>,
/// so this control does NOT fill its own background -- it paints only the scatter content.
/// </summary>
public static class GuideScatterRenderer
{
    private static readonly RGBAColor32 RingColor      = new RGBAColor32(0x33, 0x33, 0x44, 0xff);
    private static readonly RGBAColor32 SweetSpotColor = new RGBAColor32(0x18, 0x30, 0x18, 0xff);
    private static readonly RGBAColor32 RmsRingColor   = new RGBAColor32(0x44, 0x66, 0x44, 0xff);
    private static readonly RGBAColor32 RecentDotColor = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
    private static readonly RGBAColor32 OldDotColor    = new RGBAColor32(0x66, 0x66, 0x88, 0x88);
    private static readonly RGBAColor32 LatestDotColor = new RGBAColor32(0x00, 0xff, 0x00, 0xaa); // the guide crosshair green

    /// <summary>Renders the target scatter (rings, crosshair, axis labels, error dots) into <paramref name="rect"/>.</summary>
    public static void Render<TSurface>(
        Renderer<TSurface> renderer,
        RectF32 rect,
        ImmutableArray<GuideErrorSample> samples,
        GuideStats? stats,
        float dpiScale,
        string fontPath,
        float fontSize)
    {
        var padding = GuiTheme.Metrics.Padding * dpiScale;
        var side = Math.Min(rect.Width, rect.Height) - padding * 2;
        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        var halfSide = side / 2;

        // Fixed scale: rings at 3", 6", 9", 12" (outer ring = 12")
        const double targetScaleArcsec = 12.0;
        const double ringStepArcsec = 3.0;
        const double sweetSpotArcsec = 1.0;

        // Sweet spot -- filled disc showing acceptable guiding tolerance
        var sweetR = (float)(sweetSpotArcsec / targetScaleArcsec * halfSide);
        FillCircle(renderer, cx, cy, sweetR, SweetSpotColor);

        // Concentric rings at fixed arcsec intervals
        for (var ring = 1; ring <= 4; ring++)
        {
            var r = (float)(ring * ringStepArcsec / targetScaleArcsec * halfSide);
            DrawCircle(renderer, cx, cy, r, ring == 4 ? GuideGraphRenderer.ZeroLineColor : RingColor);
        }

        // Crosshair -- short marks at center only
        var crossLen = 8f * dpiScale;
        FillRect(renderer, cx - crossLen, cy, crossLen * 2, 1, RingColor);
        FillRect(renderer, cx, cy - crossLen, 1, crossLen * 2, RingColor);

        // Axis labels
        var labelSize = fontSize * 0.7f;
        DrawText(renderer, "RA", fontPath, rect.X + rect.Width - padding - 20 * dpiScale, cy + 2, 20 * dpiScale, labelSize,
            labelSize, GuideGraphRenderer.RaColor, TextAlign.Far, TextAlign.Near);
        DrawText(renderer, "Dec", fontPath, cx + 2, rect.Y + padding, 30 * dpiScale, labelSize,
            labelSize, GuideGraphRenderer.DecColor, TextAlign.Near, TextAlign.Near);

        // Scale label
        DrawText(renderer, $"±{targetScaleArcsec:F0}\"", fontPath,
            rect.X + padding, rect.Y + rect.Height - labelSize * 1.5f, 50 * dpiScale, labelSize,
            labelSize, GuideGraphRenderer.ZeroLineColor, TextAlign.Near, TextAlign.Far);

        // RMS circle
        if (stats is { TotalRMS: > 0 } gs)
        {
            var rmsR = (float)(gs.TotalRMS / targetScaleArcsec * halfSide);
            if (rmsR > 2)
            {
                DrawCircle(renderer, cx, cy, Math.Min(rmsR, halfSide), RmsRingColor);
            }
        }

        // Plot dots -- recent samples brighter, older samples dimmer
        var recentCount = Math.Min(samples.Length, 50);
        var startIdx = samples.Length - recentCount;

        for (var i = startIdx; i < samples.Length; i++)
        {
            var s = samples[i];
            var px = cx + (float)(Math.Clamp(s.RaError / targetScaleArcsec, -1, 1) * halfSide);
            var py = cy - (float)(Math.Clamp(s.DecError / targetScaleArcsec, -1, 1) * halfSide);

            // Fade: newest = white, oldest = dim
            var age = (float)(i - startIdx) / recentCount;
            var dotColor = age > 0.8f ? RecentDotColor : OldDotColor;
            var dotSize = age > 0.8f ? 3 : 2;

            FillRect(renderer, (int)px - dotSize / 2, (int)py - dotSize / 2, dotSize, dotSize, dotColor);
        }

        // Latest point as larger bright dot
        if (samples.Length > 0)
        {
            var last = samples[^1];
            var lx = cx + (float)(Math.Clamp(last.RaError / targetScaleArcsec, -1, 1) * halfSide);
            var ly = cy - (float)(Math.Clamp(last.DecError / targetScaleArcsec, -1, 1) * halfSide);
            FillRect(renderer, (int)lx - 2, (int)ly - 2, 5, 5, LatestDotColor);
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

    private static void FillCircle<TSurface>(Renderer<TSurface> renderer, float cx, float cy, float radius, RGBAColor32 color)
    {
        if (radius <= 0) return;
        var r = (int)radius;
        renderer.FillEllipse(
            new RectInt(new PointInt((int)(cx + r), (int)(cy + r)), new PointInt((int)(cx - r), (int)(cy - r))),
            color);
    }

    private static void DrawCircle<TSurface>(Renderer<TSurface> renderer, float cx, float cy, float radius, RGBAColor32 color, float strokeWidth = 1f)
    {
        if (radius <= 0) return;
        var r = (int)radius;
        renderer.DrawEllipse(
            new RectInt(new PointInt((int)(cx + r), (int)(cy + r)), new PointInt((int)(cx - r), (int)(cy - r))),
            color, strokeWidth);
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
