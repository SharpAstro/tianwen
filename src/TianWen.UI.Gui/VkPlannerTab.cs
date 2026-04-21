using System;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;
using Vortice.Vulkan;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned Planner tab. Overrides chart rendering to use a cached GPU texture
/// instead of hundreds of individual draw calls per frame. Mouse follower and current-time
/// shade are drawn natively as lightweight overlays (2-4 draw calls).
///
/// Uploads use the deferred pattern (CreateDeferred + RecordUpload via OnPreRenderPass)
/// so the staging-to-image copy lands on the frame's own command buffer BEFORE the render
/// pass starts. The legacy CreateFromBgra path would submit a side command buffer while
/// the frame's render pass is active on the same queue, which some drivers reject with
/// VK_ERROR_INITIALIZATION_FAILED.
/// </summary>
public sealed class VkPlannerTab : PlannerTab<VulkanContext>, IDisposable
{
    private readonly VkRenderer _renderer;

    // CPU-side chart renderer — renders to RgbaImage pixel buffer
    private RgbaImageRenderer? _chartRenderer;

    // Cached GPU texture of the rendered chart, drawn every frame when non-null.
    private VkTexture? _chartTexture;

    // Texture swapped out by the previous OnPreRenderPass. Disposed on the NEXT
    // OnPreRenderPass — by then BeginFrame has waited on the fence from
    // MaxFramesInFlight frames ago, so the GPU is guaranteed done with it.
    private VkTexture? _deferredDispose;

    // CPU pixels awaiting GPU upload. Recorded at the next OnPreRenderPass (BEFORE
    // the render pass opens, since transfers can't happen inside a render pass).
    private byte[]? _pendingPixels;
    private int _pendingWidth;
    private int _pendingHeight;

    // Cache key to detect when the chart needs re-rendering
    private ChartCacheKey _cachedKey;

    public VkPlannerTab(VkRenderer renderer) : base(renderer)
    {
        _renderer = renderer;

        // Chain onto any existing OnPreRenderPass subscriber so we don't clobber it.
        var previous = renderer.OnPreRenderPass;
        _renderer.OnPreRenderPass = cmd =>
        {
            previous?.Invoke(cmd);
            FlushChartUpload(cmd);
        };
    }

    private void FlushChartUpload(VkCommandBuffer cmd)
    {
        // Safe to dispose: the texture we moved here at the PREVIOUS OnPreRenderPass
        // was last rendered one frame earlier, and BeginFrame just waited on the
        // matching per-slot fence (MaxFramesInFlight=2).
        _deferredDispose?.Dispose();
        _deferredDispose = null;

        if (_pendingPixels is null)
        {
            return;
        }

        // RgbaImageRenderer produces RGBA bytes; pass the matching format so the
        // driver reads them directly — no CPU swizzle loop needed on this hot path.
        var newTex = VkTexture.CreateDeferred(_renderer.Context, _pendingPixels, _pendingWidth, _pendingHeight,
            VkFormat.R8G8B8A8Unorm);
        newTex.RecordUpload(cmd);

        _deferredDispose = _chartTexture;
        _chartTexture = newTex;
        _pendingPixels = null;
    }

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

        // Re-render only on cache miss AND when no upload is already queued for
        // the next frame — avoids re-doing CPU work when nothing has changed.
        if (key != _cachedKey && _pendingPixels is null)
        {
            // Resize CPU renderer if needed
            if (_chartRenderer is null || _chartRenderer.Width != (uint)chartW || _chartRenderer.Height != (uint)chartH)
            {
                _chartRenderer?.Dispose();
                _chartRenderer = new RgbaImageRenderer((uint)chartW, (uint)chartH);
            }

            // Render chart to CPU pixel buffer at (0,0) — no overlays. Output is RGBA;
            // the GPU upload uses VkFormat.R8G8B8A8Unorm so no byte-swap is needed.
            AltitudeChartRenderer.Render(_chartRenderer, state, fontPath,
                0, 0, chartW, chartH,
                selectedIndex, currentTime: null, mouseScreenPosition: null,
                emojiFontPath);

            // Copy to a detached buffer — the upload happens next frame, and by
            // then _chartRenderer.Surface.Pixels may have been overwritten.
            _pendingPixels = [.. _chartRenderer.Surface.Pixels];
            _pendingWidth = chartW;
            _pendingHeight = chartH;
            _cachedKey = key;
        }

        // Draw the cached texture. On the very first frame after a cache miss
        // there is nothing to draw — the upload fires at the next OnPreRenderPass.
        if (_chartTexture is not null)
        {
            _renderer.DrawTexture(_chartTexture.DescriptorSet, chartRect.X, chartRect.Y, chartRect.Width, chartRect.Height);
        }

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
            _renderer.FillRectangle(
                new RectInt(new PointInt(nowX, plotY + plotH), new PointInt(plotX, plotY)),
                new RGBAColor32(0, 0, 0, 100));
            // Vertical line at current time
            _renderer.FillRectangle(
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
        _renderer.FillRectangle(
            new RectInt(new PointInt((int)mx + 1, plotY + plotH), new PointInt((int)mx, plotY)),
            new RGBAColor32(255, 255, 255, 50));

        // Time label near top of chart
        var mouseTime = AltitudeChartRenderer.XToTime(mx, tStart, tEnd, plotX, plotW);
        var mouseLabel = mouseTime.ToOffset(state.SiteTimeZone).ToString("HH:mm");
        var fontSize = Math.Max(6f, 10f * chartH / 800f);
        _renderer.DrawText(mouseLabel, fontPath, fontSize,
            new RGBAColor32(255, 255, 255, 160),
            new RectInt(new PointInt((int)mx + 20, plotY + 16), new PointInt((int)mx - 20, plotY + 2)),
            TextAlign.Center, TextAlign.Near);
    }

    public void Dispose()
    {
        _chartTexture?.Dispose();
        _deferredDispose?.Dispose();
        _chartRenderer?.Dispose();
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
