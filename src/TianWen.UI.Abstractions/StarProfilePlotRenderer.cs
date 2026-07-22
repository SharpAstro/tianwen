using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Paint-owning guide-star profile plot: 1D intensity cross-sections through the guide-star centre,
/// horizontal (green) and vertical (cyan) overlaid, each with a moment-estimated Gaussian fit and an
/// FWHM readout + legend. Draws onto any <see cref="Renderer{TSurface}"/> (the same seam as
/// <see cref="GuideGraphRenderer"/>); the draw loops used to be inlined in
/// <see cref="GuiderTab{TSurface}"/>. The pane background is a layout <c>.Bg</c>, so (unlike the other
/// chart controls) this one does NOT fill its own background -- it paints only the plot content.
/// </summary>
public static class StarProfilePlotRenderer
{
    private static readonly RGBAColor32 LineColor  = new RGBAColor32(0x44, 0x99, 0x44, 0x88);
    private static readonly RGBAColor32 VLineColor = new RGBAColor32(0x44, 0x88, 0x99, 0x88);
    private static readonly RGBAColor32 FitColor   = new RGBAColor32(0x66, 0xff, 0x66, 0xff);
    private static readonly RGBAColor32 VFitColor  = new RGBAColor32(0x66, 0xdd, 0xff, 0xff);

    /// <summary>
    /// Renders the horizontal + vertical star-profile cross-sections with their Gaussian fits into the
    /// PLOT rect (the pane's padding + title row are layout). The FWHM readout draws over the plot's
    /// top band and the H/V legend over the bottom band.
    /// </summary>
    public static void Render<TSurface>(
        Renderer<TSurface> renderer,
        RectF32 rect,
        float[] hProfile,
        float[] vProfile,
        float dpiScale,
        string fontPath,
        float fontSize)
    {
        var plotX = rect.X;
        var plotY = rect.Y;
        var plotW = rect.Width;
        var plotH = rect.Height;

        if (plotW < 10 || plotH < 10) return;

        // Find max value across both profiles for a shared Y scale
        var maxVal = 1f;
        for (var i = 0; i < hProfile.Length; i++)
        {
            if (hProfile[i] > maxVal) maxVal = hProfile[i];
        }
        for (var i = 0; i < vProfile.Length; i++)
        {
            if (vProfile[i] > maxVal) maxVal = vProfile[i];
        }

        // Draw raw profiles as step-style line charts
        var lineW = Math.Max(1f, dpiScale);
        DrawProfileLine(renderer, hProfile, plotX, plotY, plotW, plotH, maxVal, lineW, LineColor);
        DrawProfileLine(renderer, vProfile, plotX, plotY, plotW, plotH, maxVal, lineW, VLineColor);

        // Gaussian fit overlay (moment estimation -- no iterative solver)
        var hFit = FitGaussian(hProfile);
        var vFit = FitGaussian(vProfile);

        if (hFit is var (hA, hMu, hSigma))
        {
            DrawGaussianCurve(renderer, hA, hMu, hSigma, hProfile.Length, plotX, plotY, plotW, plotH, maxVal, lineW, FitColor);
        }
        if (vFit is var (vA, vMu, vSigma))
        {
            DrawGaussianCurve(renderer, vA, vMu, vSigma, vProfile.Length, plotX, plotY, plotW, plotH, maxVal, lineW, VFitColor);
        }

        // FWHM text
        var fwhmText = "";
        if (hFit is var (_, _, hs)) fwhmText += $"H:{2.355 * hs:F1}px";
        if (vFit is var (_, _, vs)) fwhmText += (fwhmText.Length > 0 ? "  " : "") + $"V:{2.355 * vs:F1}px";
        if (fwhmText.Length > 0)
        {
            DrawText(renderer, fwhmText, fontPath,
                plotX, plotY, plotW, fontSize,
                fontSize * 0.75f, GuiTheme.Palette.BodyText, TextAlign.Far, TextAlign.Near);
        }

        // Legend
        var legendY = rect.Y + rect.Height - fontSize;
        FillRect(renderer, (int)plotX, (int)legendY, (int)(6 * dpiScale), (int)(2 * dpiScale), LineColor);
        DrawText(renderer, "H", fontPath, plotX + 8 * dpiScale, legendY - fontSize * 0.2f, 15 * dpiScale, fontSize,
            fontSize * 0.7f, LineColor, TextAlign.Near, TextAlign.Center);
        FillRect(renderer, (int)(plotX + 25 * dpiScale), (int)legendY, (int)(6 * dpiScale), (int)(2 * dpiScale), VLineColor);
        DrawText(renderer, "V", fontPath, plotX + 33 * dpiScale, legendY - fontSize * 0.2f, 15 * dpiScale, fontSize,
            fontSize * 0.7f, VLineColor, TextAlign.Near, TextAlign.Center);
    }

    private static void DrawProfileLine<TSurface>(Renderer<TSurface> renderer, float[] profile,
        float plotX, float plotY, float plotW, float plotH, float maxVal, float lineW, RGBAColor32 color)
    {
        if (profile.Length < 2) return;

        var step = plotW / (profile.Length - 1);
        for (var i = 1; i < profile.Length; i++)
        {
            var x1 = plotX + (i - 1) * step;
            var x2 = plotX + i * step;
            var y1 = plotY + plotH - (profile[i - 1] / maxVal * plotH);
            var y2 = plotY + plotH - (profile[i] / maxVal * plotH);

            // Horizontal segment then vertical connector (step-style)
            FillRect(renderer, x1, y1, x2 - x1, lineW, color);
            FillRect(renderer, x2, Math.Min(y1, y2), lineW, Math.Abs(y2 - y1) + lineW, color);
        }
    }

    /// <summary>
    /// Fits a Gaussian to a 1D profile via moment estimation (no iteration).
    /// Returns (amplitude, center, sigma) or default if the profile is flat.
    /// </summary>
    private static (float A, float Mu, float Sigma) FitGaussian(float[] profile)
    {
        var sumI = 0.0;
        var sumIX = 0.0;
        var peak = 0f;

        for (var i = 0; i < profile.Length; i++)
        {
            var v = profile[i];
            sumI += v;
            sumIX += v * i;
            if (v > peak) peak = v;
        }

        if (sumI <= 0 || peak <= 0)
        {
            return default;
        }

        var mu = sumIX / sumI;

        var sumIXX = 0.0;
        for (var i = 0; i < profile.Length; i++)
        {
            var d = i - mu;
            sumIXX += profile[i] * d * d;
        }

        var sigma = Math.Sqrt(sumIXX / sumI);
        if (sigma < 0.5) sigma = 0.5; // minimum width

        return ((float)peak, (float)mu, (float)sigma);
    }

    private static void DrawGaussianCurve<TSurface>(Renderer<TSurface> renderer, float amplitude, float mu, float sigma, int profileLen,
        float plotX, float plotY, float plotW, float plotH, float maxVal, float lineW, RGBAColor32 color)
    {
        var steps = (int)plotW;
        if (steps < 2) return;

        var twoSigmaSq = 2.0 * sigma * sigma;
        for (var i = 1; i < steps; i++)
        {
            var t0 = (float)(i - 1) / steps * (profileLen - 1);
            var t1 = (float)i / steps * (profileLen - 1);
            var g0 = amplitude * Math.Exp(-((t0 - mu) * (t0 - mu)) / twoSigmaSq);
            var g1 = amplitude * Math.Exp(-((t1 - mu) * (t1 - mu)) / twoSigmaSq);

            var x1 = plotX + (float)(i - 1) / steps * plotW;
            var x2 = plotX + (float)i / steps * plotW;
            var y1 = plotY + plotH - (float)(g0 / maxVal) * plotH;
            var y2 = plotY + plotH - (float)(g1 / maxVal) * plotH;

            FillRect(renderer, x1, y1, x2 - x1, lineW, color);
            FillRect(renderer, x2, Math.Min(y1, y2), lineW, Math.Abs(y2 - y1) + lineW, color);
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
