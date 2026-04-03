using System;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned Sky Map tab. Renders stars, constellation lines, grid, and horizon
/// to a cached <see cref="VkTexture"/> via CPU pixel buffer. Text labels are drawn
/// natively by the base class on top.
/// </summary>
public sealed class VkSkyMapTab(VkRenderer renderer) : SkyMapTab<VulkanContext>(renderer)
{
    private RgbaImageRenderer? _mapRenderer;
    private VkTexture? _mapTexture;
    private SkyMapCacheKey _cachedKey;

    protected override void RenderSkyMap(
        ICelestialObjectDB db, RectF32 contentRect, string fontPath,
        TimeProvider timeProvider, double siteLat, double siteLon)
    {
        var mapW = (int)contentRect.Width;
        var mapH = (int)contentRect.Height;
        if (mapW <= 0 || mapH <= 0)
        {
            return;
        }

        var key = new SkyMapCacheKey(
            mapW, mapH,
            State.CenterRA, State.CenterDec,
            State.FieldOfViewDeg,
            State.ShowHorizon,
            State.ShowConstellationBoundaries,
            State.ShowConstellationFigures,
            State.ShowGrid,
            State.ShowPlanets,
            State.MagnitudeLimit,
            timeProvider.GetUtcNow().ToUnixTimeSeconds() / 60
        );

        if (key != _cachedKey || _mapTexture is null || State.NeedsRedraw)
        {
            if (_mapRenderer is null || _mapRenderer.Width != (uint)mapW || _mapRenderer.Height != (uint)mapH)
            {
                _mapRenderer?.Dispose();
                _mapRenderer = new RgbaImageRenderer((uint)mapW, (uint)mapH);
            }

            SkyMapRenderer.Render(_mapRenderer.Surface, State, db, timeProvider, siteLat, siteLon, fontPath);

            // Swizzle RGBA → BGRA for Vulkan
            var pixels = _mapRenderer.Surface.Pixels;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }

            _mapTexture?.Dispose();
            _mapTexture = VkTexture.CreateFromBgra(renderer.Context, pixels, mapW, mapH);
            _cachedKey = key;
            State.NeedsRedraw = false;
        }

        renderer.DrawTexture(_mapTexture.DescriptorSet, contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height);
    }

    private readonly record struct SkyMapCacheKey(
        int Width, int Height,
        double CenterRA, double CenterDec,
        double FieldOfViewDeg,
        bool ShowHorizon,
        bool ShowConstellationBoundaries,
        bool ShowConstellationFigures,
        bool ShowGrid,
        bool ShowPlanets,
        float MagnitudeLimit,
        long TimeMinute);
}
