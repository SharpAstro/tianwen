using System;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Devices.Weather;
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

    /// <summary>
    /// True while a re-rendered chart frame is queued for GPU upload but not yet drawn, so the
    /// event loop must schedule one more frame.
    ///
    /// The upload is deferred a full frame: <see cref="RenderChart"/> queues
    /// <see cref="_pendingPixels"/> (and draws the PREVIOUS texture); the NEXT frame's
    /// BeginFrame -> OnPreRenderPass (<see cref="FlushChartUpload"/>) swaps in the new texture,
    /// and that same frame's RenderChart finally draws it. The interval where
    /// <see cref="_pendingPixels"/> is non-null straddles the event loop's CheckNeedsRedraw for
    /// that next frame, so exposing it there is what forces the follow-up frame. Without it the
    /// chart paints one selection behind the list/detail panels (which paint immediately), and
    /// the planner tab has no periodic redraw to catch it up -- so the chart only updated when
    /// some unrelated event (e.g. the mouse entering the chart) happened to trigger a redraw.
    /// Mirrors the renderer's own FontAtlasDirty -> redraw contract for deferred glyph uploads.
    /// </summary>
    public bool ChartTexturePendingDraw => _pendingPixels is not null;

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

        // Slider positions must be part of the cache key: dragging or arrow-stepping a handoff
        // slider changes its time (not the count or selected index), so without this the cached
        // chart texture would not rebuild and the slider line would appear frozen mid-drag.
        var slidersHash = new HashCode();
        foreach (var sliderTime in state.HandoffSliders)
        {
            slidersHash.Add(sliderTime.UtcTicks);
        }

        // Build cache key — excludes mouse and current time (drawn as overlays)
        var key = new ChartCacheKey(
            chartW, chartH,
            selectedIndex,
            state.PinnedCount,
            state.HandoffSliders.Length,
            state.SelectedSliderIndex,
            state.DraggingSliderIndex,
            state.MinHeightAboveHorizon,
            state.WeatherForecast is { Count: > 0 } wf ? wf[0].Time.GetHashCode() : 0,
            state.AstroDark.GetHashCode(),
            slidersHash.ToHashCode()
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
            DrawWeatherTooltip(mousePos, state, tStart, tEnd, plotX, plotW,
                (int)chartRect.X, (int)chartRect.Y, chartW, chartH, fontPath);
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

    /// <summary>
    /// Hover tooltip over the weather band: when the cursor is on an hour's icon/humidity row,
    /// finds the nearest forecast hour and draws a detail box (condition, cloud, chance of rain,
    /// temp/dew, humidity, wind, visibility). Drawn natively per-frame like the mouse follower so
    /// it never invalidates the cached chart texture.
    /// </summary>
    private void DrawWeatherTooltip((float, float)? mousePos, PlannerState state,
        DateTimeOffset tStart, DateTimeOffset tEnd, int plotX, int plotW,
        int areaX, int areaY, int chartW, int chartH, string fontPath)
    {
        if (mousePos is not var (mx, my))
        {
            return;
        }
        if (state.WeatherForecast is not { Count: > 0 } forecast)
        {
            return;
        }

        var bandOpt = AltitudeChartRenderer.GetWeatherBandLayout(state, areaX, areaY, chartW, chartH);
        if (bandOpt is not { } band)
        {
            return;
        }
        var (bandX, bandY, bandW, bandH) = band;

        // Only react while hovering the weather band row.
        if (mx < bandX || mx > bandX + bandW || my < bandY || my > bandY + bandH)
        {
            return;
        }

        // Nearest forecast hour to the cursor X (within half a slot).
        var tRange = (tEnd - tStart).TotalHours;
        if (tRange <= 0)
        {
            return;
        }
        var slotW = Math.Max(8.0, plotW / tRange);

        HourlyWeatherForecast? best = null;
        var bestDx = double.MaxValue;
        var bestX = 0.0;
        foreach (var entry in forecast)
        {
            var frac = (entry.Time - tStart).TotalHours / tRange;
            var ex = plotX + frac * plotW;
            var dx = Math.Abs(ex - mx);
            if (dx < bestDx)
            {
                bestDx = dx;
                best = entry;
                bestX = ex;
            }
        }
        if (best is not { } f || bestDx > slotW)
        {
            return;
        }

        // Highlight the hovered hour column for feedback.
        _renderer.FillRectangle(
            new RectInt(new PointInt((int)(bestX + slotW / 2), bandY + bandH), new PointInt((int)(bestX - slotW / 2), bandY)),
            new RGBAColor32(255, 255, 255, 30));

        var lines = AltitudeChartRenderer.BuildWeatherTooltipLines(f, state.SiteTimeZone);
        if (lines.Count == 0)
        {
            return;
        }

        var fontSize = Math.Max(7f, 11f * chartH / 800f);
        var lineH = fontSize * 1.35f;
        const float pad = 8f;

        var maxW = 0f;
        foreach (var line in lines)
        {
            var (w, _) = _renderer.MeasureText(line.AsSpan(), fontPath, fontSize);
            if (w > maxW)
            {
                maxW = w;
            }
        }

        var boxW = maxW + pad * 2f;
        var boxH = lines.Count * lineH + pad * 2f;

        // Below the band by default, centered on the cursor and clamped to the chart; flip above
        // the band if it would overflow the bottom edge.
        var boxX = Math.Clamp(mx - boxW / 2f, areaX + 2f, areaX + chartW - boxW - 2f);
        var boxY = bandY + bandH + 6f;
        if (boxY + boxH > areaY + chartH)
        {
            boxY = bandY - boxH - 6f;
        }

        _renderer.FillRectangle(
            new RectInt(new PointInt((int)(boxX + boxW), (int)(boxY + boxH)), new PointInt((int)boxX, (int)boxY)),
            new RGBAColor32(18, 20, 32, 235));
        DrawBoxBorder((int)boxX, (int)boxY, (int)boxW, (int)boxH, new RGBAColor32(120, 140, 200, 200));

        var ty = boxY + pad;
        for (var i = 0; i < lines.Count; i++)
        {
            var color = i == 0 ? new RGBAColor32(255, 255, 255, 255) : new RGBAColor32(205, 210, 225, 255);
            _renderer.DrawText(lines[i], fontPath, fontSize, color,
                new RectInt(new PointInt((int)(boxX + boxW - pad), (int)(ty + lineH)), new PointInt((int)(boxX + pad), (int)ty)),
                TextAlign.Near, TextAlign.Center);
            ty += lineH;
        }
    }

    /// <summary>Draws a 1px rectangle outline as four filled edges (FillRectangle is the primitive).</summary>
    private void DrawBoxBorder(int x, int y, int w, int h, RGBAColor32 color)
    {
        _renderer.FillRectangle(new RectInt(new PointInt(x + w, y + 1), new PointInt(x, y)), color);          // top
        _renderer.FillRectangle(new RectInt(new PointInt(x + w, y + h), new PointInt(x, y + h - 1)), color);  // bottom
        _renderer.FillRectangle(new RectInt(new PointInt(x + 1, y + h), new PointInt(x, y)), color);          // left
        _renderer.FillRectangle(new RectInt(new PointInt(x + w, y + h), new PointInt(x + w - 1, y)), color);  // right
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
        int AstroDarkHash,
        int SlidersHash);
}
