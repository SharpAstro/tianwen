using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Vortice.Vulkan;

namespace TianWen.UI.Shared;

/// <summary>
/// The orrery's bodies ray-traced on the GPU (docs/plans/atlas-planet-detail.md, A5, "The bodies on the GPU"): one draw a body through
/// <c>planet_body.vert</c> / <c>planet_body.frag</c>, its frame (<see cref="PlanetView"/>) and lighting as push constants, its colour
/// map a float texture uploaded ONCE per body and texture version. Nothing is built per frame, so Play costs a draw a body and no more.
/// Recorded into the 2D renderer's own command buffer between its draws, so the painter's order of the orrery holds. A map's upload is
/// QUEUED into the next frame (<see cref="VulkanContext.QueueTextureUpload"/>) and the body's previous texture is drawn until it lands;
/// before any has landed the draw answers false and the caller draws the flat dot.
/// </summary>
public sealed unsafe class VkPlanetBodies : IDisposable
{
    /// <summary>The push-constant block, seven vec4s: <c>planet_body.frag</c>'s <c>Body</c>.</summary>
    public const int PushBytes = PlanetBodyMap.BlockFloats * sizeof(float);

    private sealed class Slot
    {
        public (int Version, RGBAColor32 Flat) Key;
        public VkTexture? Shown;
        public (float R, float G, float B) ShownAlbedo;
        public VkTexture? Pending;
        public (float R, float G, float B) PendingAlbedo;
    }

    private readonly VulkanContext _ctx;
    private readonly Action _requestRedraw;
    private readonly Dictionary<CatalogIndex, Slot> _slots = [];
    private VkPipelineLayout _layout;
    private VkPipeline _pipeline;
    private bool _disposed;

    public VkPlanetBodies(VulkanContext ctx, Action requestRedraw)
    {
        _ctx = ctx;
        _requestRedraw = requestRedraw;
        CreatePipeline();
    }

    /// <summary>How many colour maps have been uploaded: one a body and texture version, never one a frame.</summary>
    public int Uploads { get; private set; }

    /// <summary>
    /// Draws <paramref name="view"/> centred on (<paramref name="x"/>, <paramref name="y"/>), its globe <paramref name="radiusPx"/>, into
    /// <paramref name="renderer"/>'s frame. False until a texture of the body has landed.
    /// </summary>
    public bool Draw(VkRenderer renderer, in PlanetView view, float x, float y, float radiusPx, PlanetColourMap? map, int textureVersion,
        RGBAColor32 flat, RGBAColor32? nightTint, float opacity = 1)
    {
        if (!_slots.TryGetValue(view.Body, out var slot))
        {
            _slots[view.Body] = slot = new Slot { Key = (int.MinValue, default) };
        }
        // A flat body's texture is its colour; a mapped body's is keyed by the texture version alone.
        var key = (textureVersion, map is null ? flat : default);
        if (slot.Key != key)
        {
            slot.Key = key;
            slot.Pending?.Dispose();
            var (texels, width, height) = PlanetBodyMap.FloatTexels(map, flat);
            slot.Pending = VkTexture.CreateDeferred(_ctx, MemoryMarshal.AsBytes(texels.AsSpan()), width, height, VkFormat.R32G32B32A32Sfloat);
            slot.PendingAlbedo = PlanetBodyMap.RingAlbedo(map, flat);
            _ctx.QueueTextureUpload(slot.Pending);
            Uploads++;
        }
        if (slot.Pending is { IsUploaded: true } landed)
        {
            slot.Shown?.Dispose();
            slot.Shown = landed;
            slot.ShownAlbedo = slot.PendingAlbedo;
            slot.Pending = null;
        }
        else if (slot.Pending is not null)
        {
            // Recorded at the start of the next frame: ask for it.
            _requestRedraw();
        }
        if (slot.Shown is not { } texture)
        {
            return false;
        }

        Span<float> push = stackalloc float[PlanetBodyMap.BlockFloats];
        PlanetBodyMap.WriteBlock(push, view, x, y, radiusPx, renderer.Width, renderer.Height, slot.ShownAlbedo, nightTint, opacity);

        var cmd = renderer.CurrentCommandBuffer;
        var api = _ctx.DeviceApi;
        // The renderer's own target, the whole window (or an offscreen frame, whose context has no swapchain to size it by).
        VkViewport viewport = new() { x = 0, y = 0, width = renderer.Width, height = renderer.Height, minDepth = 0f, maxDepth = 1f };
        VkRect2D scissor = new(0, 0, renderer.Width, renderer.Height);
        api.vkCmdSetViewport(cmd, 0, 1, &viewport);
        api.vkCmdSetScissor(cmd, 0, scissor);
        api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _pipeline);
        var set = texture.DescriptorSet;
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        fixed (float* p = push)
        {
            api.vkCmdPushConstants(cmd, _layout, VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, PushBytes, p);
        }
        api.vkCmdDraw(cmd, 6, 1, 0, 0);
        return true;
    }

    private void CreatePipeline()
    {
        var api = _ctx.DeviceApi;
        VkPushConstantRange push = new()
        {
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
            offset = 0,
            size = PushBytes,
        };
        var setLayout = _ctx.DescriptorSetLayout; // the global CombinedImageSampler layout every VkTexture's set is made with
        VkPipelineLayoutCreateInfo layoutCI = new()
        {
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &push,
        };
        api.vkCreatePipelineLayout(&layoutCI, null, out _layout).CheckResult();

        var vert = VkShaderModules.Load(_ctx, "planet_body.vert");
        var frag = VkShaderModules.Load(_ctx, "planet_body.frag");
        try
        {
            VkUtf8ReadOnlyString entryPoint = "main"u8;
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Vertex, module = vert, pName = entryPoint };
            stages[1] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Fragment, module = frag, pName = entryPoint };

            // No vertex buffer: the corners come from gl_VertexIndex.
            VkPipelineVertexInputStateCreateInfo vertexInput = new();
            VkPipelineInputAssemblyStateCreateInfo inputAssembly = new(VkPrimitiveTopology.TriangleList);
            VkPipelineViewportStateCreateInfo viewportState = new(1, 1);
            VkPipelineRasterizationStateCreateInfo rasterizer = new()
            {
                polygonMode = VkPolygonMode.Fill,
                lineWidth = 1.0f,
                cullMode = VkCullModeFlags.None,
                frontFace = VkFrontFace.Clockwise,
            };
            VkPipelineMultisampleStateCreateInfo multisample = new() { rasterizationSamples = _ctx.MsaaSamples };

            // Straight alpha over what is there, as the sprites were blended (stackalloc'd for the dangling-pAttachments reason
            // VkSkyMapPipeline.CreateGraphicsPipeline states).
            var blend = stackalloc VkPipelineColorBlendAttachmentState[1];
            blend[0] = new VkPipelineColorBlendAttachmentState
            {
                colorWriteMask = VkColorComponentFlags.All,
                blendEnable = true,
                srcColorBlendFactor = VkBlendFactor.SrcAlpha,
                dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                colorBlendOp = VkBlendOp.Add,
                srcAlphaBlendFactor = VkBlendFactor.One,
                dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                alphaBlendOp = VkBlendOp.Add,
            };
            VkPipelineColorBlendStateCreateInfo colorBlend = new() { attachmentCount = 1, pAttachments = blend };

            // The shared render pass has a depth attachment (VkRenderer.DrawMesh): state one, testing and writing nothing, so the
            // painter's order decides among 2D draws, as VkSkyMapPipeline's pipelines do.
            VkPipelineDepthStencilStateCreateInfo depthStencil = new()
            {
                depthTestEnable = false,
                depthWriteEnable = false,
                depthCompareOp = VkCompareOp.Always,
            };

            var dynamicStates = stackalloc VkDynamicState[2];
            dynamicStates[0] = VkDynamicState.Viewport;
            dynamicStates[1] = VkDynamicState.Scissor;
            VkPipelineDynamicStateCreateInfo dynamicState = new() { dynamicStateCount = 2, pDynamicStates = dynamicStates };

            VkGraphicsPipelineCreateInfo pipelineCI = new()
            {
                stageCount = 2,
                pStages = stages,
                pVertexInputState = &vertexInput,
                pInputAssemblyState = &inputAssembly,
                pViewportState = &viewportState,
                pRasterizationState = &rasterizer,
                pMultisampleState = &multisample,
                pDepthStencilState = &depthStencil,
                pColorBlendState = &colorBlend,
                pDynamicState = &dynamicState,
                layout = _layout,
                renderPass = _ctx.RenderPass,
                subpass = 0,
            };
            api.vkCreateGraphicsPipeline(pipelineCI, out _pipeline).CheckResult();
        }
        finally
        {
            api.vkDestroyShaderModule(vert);
            api.vkDestroyShaderModule(frag);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var slot in _slots.Values)
        {
            slot.Shown?.Dispose();
            slot.Pending?.Dispose();
        }
        _slots.Clear();
        // The pipeline may still be bound by a frame in flight: its destroy waits for the frames, as the sky map pipeline's does.
        if (!_ctx.IsAbandoned)
        {
            _ctx.TryWaitAllFramesIdle("planet bodies dispose");
        }
        var api = _ctx.DeviceApi;
        if (_pipeline != VkPipeline.Null)
        {
            api.vkDestroyPipeline(_pipeline);
        }
        if (_layout != VkPipelineLayout.Null)
        {
            api.vkDestroyPipelineLayout(_layout);
        }
    }
}
