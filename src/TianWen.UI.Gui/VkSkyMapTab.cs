using System;
using System.Collections.Generic;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned Sky Map tab. Renders stars, constellation lines, grid, and horizon
/// via GPU shaders (<see cref="VkSkyMapPipeline"/>). Stars and static lines are stored
/// as J2000 unit vectors in persistent GPU buffers; projection happens in the vertex shader.
/// Text labels are drawn natively by the base class on top.
/// </summary>
public sealed unsafe class VkSkyMapTab(VkRenderer renderer) : SkyMapTab<VulkanContext>(renderer)
{
    private VkSkyMapPipeline? _pipeline;

    // Reusable lists for dynamic per-frame geometry
    private readonly List<float> _horizonFloats = new(2048);
    private readonly List<float> _meridianFloats = new(4096);

    protected override void RenderSkyMap(
        ICelestialObjectDB db, RectF32 contentRect, string fontPath,
        ITimeProvider timeProvider, double siteLat, double siteLon)
    {
        var mapW = contentRect.Width;
        var mapH = contentRect.Height;
        if (mapW <= 0 || mapH <= 0)
        {
            return;
        }

        // Lazy-create the pipeline
        _pipeline ??= new VkSkyMapPipeline(renderer.Context);

        // Build persistent geometry once when catalog is available
        if (!_pipeline.GeometryReady)
        {
            _pipeline.BuildGeometry(db);
        }

        // Fill background
        var bg = new RGBAColor32(0x05, 0x05, 0x0C, 0xFF);
        Renderer.FillRectangle(
            new RectInt(
                new PointInt((int)(contentRect.X + mapW), (int)(contentRect.Y + mapH)),
                new PointInt((int)contentRect.X, (int)contentRect.Y)),
            bg);

        // Build site context (needed for UBO horizon clipping and dynamic geometry)
        var site = SiteContext.Create(siteLat, siteLon, timeProvider);

        // Update UBO with current view + site for horizon clipping
        _pipeline.UpdateUbo(State, mapW, mapH, contentRect.X, contentRect.Y, site);

        _horizonFloats.Clear();
        _meridianFloats.Clear();

        if (State.ShowHorizon && site.IsValid)
        {
            VkSkyMapPipeline.BuildHorizonLine(site, _horizonFloats);
        }

        if (site.IsValid)
        {
            VkSkyMapPipeline.BuildMeridianLine(site.LST, _meridianFloats);
        }

        // Write dynamic geometry to the frame ring buffer
        var ctx = renderer.Context;
        var cmd = renderer.CurrentCommandBuffer;

        var horizonInfo = WriteToRingBuffer(ctx, _horizonFloats);
        var meridianInfo = WriteToRingBuffer(ctx, _meridianFloats);

        // Draw all sky map layers
        _pipeline.Draw(cmd, State, mapW, mapH, contentRect.X, contentRect.Y,
            horizonInfo, meridianInfo);

        // Restore the full-window viewport/scissor for text overlay rendering
        // (the pipeline sets a clipped viewport/scissor for the sky map area)
        var ctx2 = renderer.Context;
        var cmd2 = renderer.CurrentCommandBuffer;
        var api = ctx2.DeviceApi;
        Vortice.Vulkan.VkViewport fullVp = new()
        {
            x = 0, y = 0,
            width = ctx2.SwapchainWidth, height = ctx2.SwapchainHeight,
            minDepth = 0f, maxDepth = 1f
        };
        Vortice.Vulkan.VkRect2D fullScissor = new(0, 0, ctx2.SwapchainWidth, ctx2.SwapchainHeight);
        api.vkCmdSetViewport(cmd2, 0, 1, &fullVp);
        api.vkCmdSetScissor(cmd2, 0, fullScissor);
    }

    private static (Vortice.Vulkan.VkBuffer Buffer, uint ByteOffset, uint VertexCount) WriteToRingBuffer(
        VulkanContext ctx, List<float> floats)
    {
        if (floats.Count == 0)
        {
            return (default, 0, 0);
        }

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats);
        var byteOffset = ctx.WriteVertices(span);
        if (byteOffset == uint.MaxValue)
        {
            return (default, 0, 0); // ring buffer full — skip this frame
        }

        return (ctx.VertexBuffer, byteOffset, (uint)(floats.Count / 3));
    }
}
