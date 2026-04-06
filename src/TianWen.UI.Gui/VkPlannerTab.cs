using System;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned Planner tab. Overrides chart rendering to use a cached GPU texture
/// instead of hundreds of individual draw calls per frame. Mouse follower and current-time
/// shade are drawn natively as lightweight overlays (2-4 draw calls).
/// </summary>
public sealed class VkPlannerTab(VkRenderer renderer) : PlannerTab<VulkanContext>(renderer)
{
    // CPU-side chart renderer — renders to RgbaImage pixel buffer
    private RgbaImageRenderer? _chartRenderer;

    // Cached GPU texture of the rendered chart
    private VkTexture? _chartTexture;

    // Cache key to detect when the chart needs re-rendering
    private ChartCacheKey _cachedKey;

    protected override void RenderChart(PlannerState state, RectF32 chartRect, string fontPath,
        int? selectedIndex, DateTimeOffset? chartCurrentTime, (float, float)? mousePos,
        string? emojiFontPath = null)
    {
        var chartW = (int)chartRect.Width;
        var chartH = (int)chartRect.Height;
        if (chartW <= 0 || chartH <= 0)
        {
            return;
        }

        // Build cache key — excludes mouse and current time (drawn as overlays)
        var key = new ChartCacheKey(
            chartW, chartH,
            selectedIndex,
            state.PinnedCount,
            state.HandoffSliders.Count,
            state.SelectedSliderIndex,
            state.DraggingSliderIndex,
            state.MinHeightAboveHorizon,
            state.WeatherForecast is { Count: > 0 } wf ? wf[0].Time.GetHashCode() : 0,
            state.AstroDark.GetHashCode()
        );

        if (key != _cachedKey || _chartTexture is null)
        {
            // Resize CPU renderer if needed
            if (_chartRenderer is null || _chartRenderer.Width != (uint)chartW || _chartRenderer.Height != (uint)chartH)
            {
                _chartRenderer?.Dispose();
                _chartRenderer = new RgbaImageRenderer((uint)chartW, (uint)chartH);
            }

            // Render chart to CPU pixel buffer at (0,0) — no overlays
            AltitudeChartRenderer.Render(_chartRenderer, state, fontPath,
                0, 0, chartW, chartH,
                selectedIndex, currentTime: null, mouseScreenPosition: null,
                emojiFontPath);

            // Swizzle RGBA → BGRA for Vulkan (VkFormat.B8G8R8A8Unorm)
            var pixels = _chartRenderer.Surface.Pixels;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }

            // Dispose old texture, create new one.
            // Uses blocking CreateFromBgra — acceptable since re-creation only happens
            // when chart inputs change (target selection, slider drag, window resize),
            // not every frame.
            _chartTexture?.Dispose();
            _chartTexture = VkTexture.CreateFromBgra(renderer.Context, pixels, chartW, chartH);

            _cachedKey = key;
        }

        // Draw the cached texture as a single quad
        renderer.DrawTexture(_chartTexture.DescriptorSet, chartRect.X, chartRect.Y, chartRect.Width, chartRect.Height);

        // Draw lightweight overlays natively (current-time shade + mouse follower)
        if (state.AstroDark != default)
        {
            var (tStart, tEnd, plotX, plotY, plotW, plotH) =
                AltitudeChartRenderer.GetChartPlotLayout(state,
                    (int)chartRect.X, (int)chartRect.Y, chartW, chartH);

            DrawTimeOverlay(chartCurrentTime, tStart, tEnd, plotX, plotY, plotW, plotH);
            DrawMouseFollower(mousePos, state, tStart, tEnd, plotX, plotY, plotW, plotH, fontPath, chartH);
        }
    }

    private void DrawTimeOverlay(DateTimeOffset? currentTime,
        DateTimeOffset tStart, DateTimeOffset tEnd,
        int plotX, int plotY, int plotW, int plotH)
    {
        if (currentTime is not { } now || now < tStart || now > tEnd)
        {
            return;
        }

        var fraction = (now - tStart).TotalHours / (tEnd - tStart).TotalHours;
        var nowX = plotX + (int)Math.Round(fraction * plotW);

        if (nowX > plotX)
        {
            // Grey shade over elapsed time
            renderer.FillRectangle(
                new RectInt(new PointInt(nowX, plotY + plotH), new PointInt(plotX, plotY)),
                new RGBAColor32(0, 0, 0, 100));
            // Vertical line at current time
            renderer.FillRectangle(
                new RectInt(new PointInt(nowX + 1, plotY + plotH), new PointInt(nowX, plotY)),
                new RGBAColor32(255, 255, 255, 120));
        }
    }

    private void DrawMouseFollower((float, float)? mousePos, PlannerState state,
        DateTimeOffset tStart, DateTimeOffset tEnd,
        int plotX, int plotY, int plotW, int plotH,
        string fontPath, int chartH)
    {
        if (mousePos is not var (mx, my) || mx < plotX || mx > plotX + plotW
            || my < plotY || my > plotY + plotH)
        {
            return;
        }

        // Vertical line
        renderer.FillRectangle(
            new RectInt(new PointInt((int)mx + 1, plotY + plotH), new PointInt((int)mx, plotY)),
            new RGBAColor32(255, 255, 255, 50));

        // Time label near top of chart
        var mouseTime = AltitudeChartRenderer.XToTime(mx, tStart, tEnd, plotX, plotW);
        var mouseLabel = mouseTime.ToOffset(state.SiteTimeZone).ToString("HH:mm");
        var fontSize = Math.Max(6f, 10f * chartH / 800f);
        renderer.DrawText(mouseLabel, fontPath, fontSize,
            new RGBAColor32(255, 255, 255, 160),
            new RectInt(new PointInt((int)mx + 20, plotY + 16), new PointInt((int)mx - 20, plotY + 2)),
            TextAlign.Center, TextAlign.Near);
    }

    private readonly record struct ChartCacheKey(
        int Width, int Height,
        int? SelectedIndex,
        int PinnedCount,
        int SliderCount,
        int SelectedSliderIndex,
        int DraggingSliderIndex,
        int MinAltitude,
        int WeatherForecastHash,
        int AstroDarkHash);
}
