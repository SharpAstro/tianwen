using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Paint-owning tiny cooling sparkline for a single camera: a temperature step-line (auto-ranged) over
/// a cooler-power step-line (0-100%). Draws onto any <see cref="Renderer{TSurface}"/> (the same seam as
/// <see cref="GuideGraphRenderer"/>); the draw loop used to be inlined in
/// <see cref="LiveSessionTab{TSurface}"/>. The caller resolves both series colours from its palette and
/// passes them in, so this control carries no per-camera palette. Fills its own background.
/// </summary>
public static class CoolingSparklineRenderer
{
    private static readonly RGBAColor32 GraphBg = new RGBAColor32(0x12, 0x12, 0x1a, 0xff);

    /// <summary>Renders the last ~20 cooling samples for <paramref name="cameraIndex"/> as a temp-over-power sparkline.</summary>
    public static void Render<TSurface>(
        Renderer<TSurface> renderer,
        RectF32 rect,
        ImmutableArray<CoolingSample> allSamples,
        int cameraIndex,
        float dpiScale,
        RGBAColor32 tempColor,
        RGBAColor32 powerColor)
    {
        FillRect(renderer, rect.X, rect.Y, rect.Width, rect.Height, GraphBg);

        // Collect last N samples for this camera
        const int maxPoints = 20;
        Span<float> temps = stackalloc float[maxPoints];
        Span<float> powers = stackalloc float[maxPoints];
        var count = 0;
        for (var i = allSamples.Length - 1; i >= 0 && count < maxPoints; i--)
        {
            if (allSamples[i].CameraIndex == cameraIndex)
            {
                temps[maxPoints - 1 - count] = (float)allSamples[i].TemperatureC;
                powers[maxPoints - 1 - count] = (float)allSamples[i].CoolerPowerPercent;
                count++;
            }
        }

        if (count < 2)
        {
            return;
        }

        var start = maxPoints - count;
        var tempSlice = temps.Slice(start, count);
        var powerSlice = powers.Slice(start, count);

        // Find temp range
        var minT = float.MaxValue;
        var maxT = float.MinValue;
        for (var i = 0; i < count; i++)
        {
            if (tempSlice[i] < minT) minT = tempSlice[i];
            if (tempSlice[i] > maxT) maxT = tempSlice[i];
        }
        var range = Math.Max(maxT - minT, 2f);
        minT -= 1;
        maxT = minT + range + 2;

        var stepX = rect.Width / Math.Max(count - 1, 1);

        // Draw power line first (behind temp)
        for (var i = 1; i < count; i++)
        {
            var x1 = rect.X + (i - 1) * stepX;
            var x2 = rect.X + i * stepX;
            var y1 = rect.Y + rect.Height - (powerSlice[i - 1] / 100f) * rect.Height;
            var y2 = rect.Y + rect.Height - (powerSlice[i] / 100f) * rect.Height;

            FillRect(renderer, x1, y1, x2 - x1, Math.Max(1, dpiScale), powerColor);
            FillRect(renderer, x2, Math.Min(y1, y2), Math.Max(1, dpiScale), Math.Abs(y2 - y1) + dpiScale, powerColor);
        }

        // Draw temp line on top
        for (var i = 1; i < count; i++)
        {
            var x1 = rect.X + (i - 1) * stepX;
            var x2 = rect.X + i * stepX;
            var y1 = rect.Y + rect.Height - ((tempSlice[i - 1] - minT) / (maxT - minT)) * rect.Height;
            var y2 = rect.Y + rect.Height - ((tempSlice[i] - minT) / (maxT - minT)) * rect.Height;

            FillRect(renderer, x1, y1, x2 - x1, Math.Max(1, dpiScale), tempColor);
            FillRect(renderer, x2, Math.Min(y1, y2), Math.Max(1, dpiScale), Math.Abs(y2 - y1) + dpiScale, tempColor);
        }
    }

    // --- Drawing helper (float -> RectInt wrapper, byte-identical to PixelWidgetBase's) ---

    private static void FillRect<TSurface>(Renderer<TSurface> renderer, float x, float y, float w, float h, RGBAColor32 color)
    {
        if (w <= 0 || h <= 0) return;
        renderer.FillRectangle(
            new RectInt(new PointInt((int)(x + w), (int)(y + h)), new PointInt((int)x, (int)y)),
            color);
    }
}
