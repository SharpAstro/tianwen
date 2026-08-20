using SdlVulkan.Renderer;
using TianWen.Lib.Imaging;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace TianWen.UI.Shared;

/// <summary>
/// Vulkan side-car pipeline for the FITS image viewer.
/// Owns its own descriptor set layouts, descriptor pool, pipeline layout, and pipelines
/// for image rendering (stretch + WCS grid) and histogram rendering.
/// </summary>
public sealed unsafe class VkFitsImagePipeline : IDisposable
{
    private const int ChannelCount = 3;
    private const int HistogramBins = 512;

    // ------------------------------------------------------------------ UBO sizes

    /// <summary>
    /// std140 StretchUBO: see field layout in struct definition below.
    /// Total: 416 bytes (192 base + 16 wb + 16 bgNeut + 144 curveData + 16 lumaWeights
    /// + 16 lumaStretch + 16 stretchBlend).
    /// </summary>
    private const int StretchUboSize = 416;

    /// <summary>
    /// How many independent StretchUBO slots the stretch buffer holds.
    /// <para><b>Two, and this is load-bearing for the before/after split.</b> The split records TWO
    /// image draws into ONE command buffer, and the GPU reads a UBO at EXECUTE time, not at record
    /// time. With a single region the sequence write-A / draw / write-B / draw would hand BOTH draws
    /// whatever was written last, so the "before" half would render with the after's settings and the
    /// comparison would show no difference at all -- a silent wrong answer, not a crash.</para>
    /// </summary>
    private const int StretchUboSlots = 2;

    /// <summary>Slot 0 of the stretch UBO: the live rendition (the only slot a non-split draw uses).</summary>
    public const int UboSlotPrimary = 0;

    /// <summary>Slot 1 of the stretch UBO: the comparison ("before") rendition.</summary>
    public const int UboSlotComparison = 1;

    /// <summary>
    /// std140 HistogramUBO: 4 x int/float fields = 16 bytes.
    /// </summary>
    private const int HistogramUboSize = 16;

    // ------------------------------------------------------------------ Shader sources




    // ------------------------------------------------------------------ Vulkan objects

    private readonly VulkanContext _ctx;

    // Descriptor set layouts (shared by both pipelines)
    private VkDescriptorSetLayout _uboSetLayout;       // set 0: UBO
    private VkDescriptorSetLayout _samplerSetLayout;   // set 1: 3x samplers

    // Descriptor pool
    private VkDescriptorPool _descriptorPool;

    // Descriptor sets: image UBO (slot 0) + image UBO (slot 1, the comparison rendition) + histogram
    // UBO + image samplers + before-image samplers + histogram samplers.
    private VkDescriptorSet _imageUboSet;
    private VkDescriptorSet _imageUboSetComparison;
    private VkDescriptorSet _histogramUboSet;
    private VkDescriptorSet _imageSamplerSet;
    private VkDescriptorSet _beforeSamplerSet;
    private VkDescriptorSet _histogramSamplerSet;

    // Shared pipeline layout
    private VkPipelineLayout _pipelineLayout;

    // Pipelines
    private VkPipeline _imagePipeline;
    private VkPipeline _histogramPipeline;

    // Shared sampler
    private VkSampler _linearSampler;
    private VkFormatFeatureFlags _r32SfloatOptimalTilingFeatures;
    private bool _r32SfloatLinearFilterSupported;

    /// <summary>
    /// The format feature flags advertised by the physical device for
    /// <see cref="VkFormat.R32Sfloat"/>'s optimal tiling, captured at sampler creation.
    /// Exposed for diagnostics: lavapipe historically returns 0 samples when linear
    /// filtering is requested without `SampledImageFilterLinear` support.
    /// </summary>
    public VkFormatFeatureFlags R32SfloatOptimalTilingFeatures => _r32SfloatOptimalTilingFeatures;
    public bool R32SfloatLinearFilterSupported => _r32SfloatLinearFilterSupported;

    // Channel textures (3x R32_SFLOAT 2D)
    private readonly VkImage[] _channelImages = new VkImage[ChannelCount];
    private readonly VkDeviceMemory[] _channelMemories = new VkDeviceMemory[ChannelCount];
    private readonly VkImageView[] _channelViews = new VkImageView[ChannelCount];
    private readonly int[] _channelWidth = new int[ChannelCount];
    private readonly int[] _channelHeight = new int[ChannelCount];

    // Retained pre-enhance channel textures (the "before" half of the split). Populated ONLY by
    // TryRetainChannelsAsBefore, which MOVES the live handles here rather than copying any pixels --
    // see that method for why that is free.
    private readonly VkImage[] _beforeImages = new VkImage[ChannelCount];
    private readonly VkDeviceMemory[] _beforeMemories = new VkDeviceMemory[ChannelCount];
    private readonly VkImageView[] _beforeViews = new VkImageView[ChannelCount];
    private readonly int[] _beforeWidth = new int[ChannelCount];
    private readonly int[] _beforeHeight = new int[ChannelCount];
    private long _beforeChannelBytes;

    // Histogram textures (3x R32F stored as 512×1 2D)
    private readonly VkImage[] _histImages = new VkImage[ChannelCount];
    private readonly VkDeviceMemory[] _histMemories = new VkDeviceMemory[ChannelCount];
    private readonly VkImageView[] _histViews = new VkImageView[ChannelCount];

    // Stretch UBO buffer (persistently mapped). Holds StretchUboSlots slots, each aligned up to the
    // device's minUniformBufferOffsetAlignment so a descriptor can address a slot by offset.
    private VkBuffer _stretchUboBuffer;
    private VkDeviceMemory _stretchUboMemory;
    private byte* _stretchUboMapped;
    private int _stretchUboSlotStride;

    // Histogram UBO buffer (persistently mapped)
    private VkBuffer _histogramUboBuffer;
    private VkDeviceMemory _histogramUboMemory;
    private byte* _histogramUboMapped;

    // Staging buffer for texture uploads
    private VkBuffer _stagingBuffer;
    private VkDeviceMemory _stagingMemory;
    private ulong _stagingSize;

    private bool _disposed;

    // ------------------------------------------------------------------ Constructor

    public VkFitsImagePipeline(VulkanContext ctx)
    {
        _ctx = ctx;

        CreateDescriptorSetLayouts();
        CreateDescriptorPool();
        AllocateDescriptorSets();
        CreatePipelineLayout();
        CreateSampler();
        CreateUboBuffers();
        CreatePlaceholderTextures();
        CreatePipelines();
    }

    // ------------------------------------------------------------------ Public API

    /// <summary>
    /// Reads back the first <paramref name="count"/> floats of channel slot
    /// <paramref name="channel"/> into <paramref name="destination"/>. Diagnostic helper used
    /// by tests to confirm whether texture upload actually placed data on the GPU. Blocks
    /// on a one-shot command buffer + queue idle (slow -- not for production hot paths).
    /// </summary>
    public unsafe void ReadbackChannelFirstFloats(int channel, Span<float> destination)
    {
        if ((uint)channel >= ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel));
        if (_channelWidth[channel] == 0 || _channelHeight[channel] == 0)
            throw new InvalidOperationException("Channel texture has no data uploaded.");

        var api = _ctx.DeviceApi;
        var count = destination.Length;
        var byteSize = (ulong)(count * sizeof(float));

        // Host-visible scratch buffer (separate from _stagingBuffer to avoid trampling it).
        VkBufferCreateInfo bufCI = new()
        {
            size = byteSize,
            usage = VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out var scratch).CheckResult();
        try
        {
            api.vkGetBufferMemoryRequirements(scratch, out var memReqs);
            VkMemoryAllocateInfo allocInfo = new()
            {
                allocationSize = memReqs.size,
                memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits,
                    VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
            };
            api.vkAllocateMemory(&allocInfo, null, out var scratchMem).CheckResult();
            try
            {
                api.vkBindBufferMemory(scratch, scratchMem, 0);

                // Clamp copy extent so the GPU writes exactly `count` texels into the
                // scratch buffer -- a full row (`_channelWidth` texels) would overflow our
                // tiny scratch buffer. Hardware drivers tolerate the overflow silently;
                // lavapipe segfaults the process and tanks the rest of the test suite.
                var width = (uint)Math.Min(count, _channelWidth[channel]);
                var rowsNeeded = (int)Math.Ceiling((double)count / Math.Max(width, 1));
                var height = (uint)Math.Min(_channelHeight[channel], Math.Max(rowsNeeded, 1));

                _ctx.ExecuteOneShot(cmd =>
                {
                    TransitionImageLayout(cmd, _channelImages[channel],
                        VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferSrcOptimal);

                    VkBufferImageCopy region = new()
                    {
                        bufferOffset = 0,
                        bufferRowLength = 0,
                        bufferImageHeight = 0,
                        imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
                        imageOffset = new VkOffset3D(0, 0, 0),
                        imageExtent = new VkExtent3D(width, height, 1)
                    };
                    api.vkCmdCopyImageToBuffer(cmd, _channelImages[channel],
                        VkImageLayout.TransferSrcOptimal, scratch, 1, &region);

                    TransitionImageLayout(cmd, _channelImages[channel],
                        VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal);
                });

                void* mapped;
                api.vkMapMemory(scratchMem, 0, byteSize, 0, &mapped);
                new ReadOnlySpan<float>(mapped, count).CopyTo(destination);
                api.vkUnmapMemory(scratchMem);
            }
            finally
            {
                api.vkFreeMemory(scratchMem);
            }
        }
        finally
        {
            api.vkDestroyBuffer(scratch);
        }
    }

    /// <summary>
    /// Uploads a R32_SFLOAT 2D image into channel slot <paramref name="channel"/> (0-based).
    /// Creates or recreates the texture if dimensions changed.
    /// </summary>
    public void UploadChannelTexture(ReadOnlySpan<float> data, int channel, int width, int height)
    {
        if ((uint)channel >= ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel));

        var byteSize = (ulong)(data.Length * sizeof(float));
        EnsureStagingBuffer(byteSize);
        CopyToStaging(data, byteSize);

        if (_channelWidth[channel] != width || _channelHeight[channel] != height)
        {
            DestroyChannelTexture(channel);
            CreateChannelTextureOrFreeBefore(channel, width, height);
            BindChannelSampler(channel, _channelViews[channel], _imageSamplerSet);
            if (!HasBeforeChannels)
            {
                // Keep the before set pointing at live views while nothing is retained, so it can
                // never reference the view this recreation just destroyed.
                BindChannelSampler(channel, _channelViews[channel], _beforeSamplerSet);
            }
        }

        UploadToImage(_channelImages[channel], (uint)width, (uint)height, byteSize, VkFormat.R32Sfloat);
        _channelWidth[channel] = width;
        _channelHeight[channel] = height;
    }

    /// <summary>
    /// True while pre-enhance channel textures are retained for the before/after split's left half.
    /// </summary>
    public bool HasBeforeChannels { get; private set; }

    /// <summary>
    /// Device memory currently held by the retained before textures, in bytes. Reported so the host
    /// can declare it to the GC (<c>GC.AddMemoryPressure</c>): a <c>VkImage</c> is invisible to
    /// <c>GC.GetGCMemoryInfo().MemoryLoadBytes</c>, so without this the runtime under-estimates how
    /// tight memory is by exactly the amount this cache is costing.
    /// </summary>
    public long BeforeChannelBytes => _beforeChannelBytes;

    /// <summary>
    /// Moves the CURRENT channel textures into the before slot, so the next upload allocates fresh
    /// ones and the pre-upload pixels stay available for the split's left half.
    /// </summary>
    /// <remarks>
    /// <para><b>This copies nothing.</b> <see cref="UploadChannelTexture"/> reuses a same-size
    /// texture and writes over it, so at the moment an enhanced frame is applied the pre-enhance
    /// pixels are ALREADY resident on the GPU. Retaining them is therefore not a capture but a
    /// decision not to overwrite: three handles move aside and the replacement allocates. No
    /// readback, no host copy, no second upload, no added latency on the apply -- the only cost is
    /// the deferred free.</para>
    /// <para>Call on the render thread, OUTSIDE command-buffer recording (the same position in the
    /// frame as a texture upload). Returns false when there is nothing worth retaining.</para>
    /// </remarks>
    public bool TryRetainChannelsAsBefore()
    {
        // A 1x1 placeholder is not a "before" -- retaining it would light up the split affordance
        // for a comparison that shows one interpolated pixel.
        var haveRealTexture = false;
        for (var i = 0; i < ChannelCount; i++)
        {
            if (_channelImages[i] != VkImage.Null && _channelWidth[i] > 1 && _channelHeight[i] > 1)
            {
                haveRealTexture = true;
                break;
            }
        }
        if (!haveRealTexture)
        {
            return false;
        }

        ReleaseBeforeChannels();

        long bytes = 0;
        for (var i = 0; i < ChannelCount; i++)
        {
            _beforeImages[i] = _channelImages[i];
            _beforeMemories[i] = _channelMemories[i];
            _beforeViews[i] = _channelViews[i];
            _beforeWidth[i] = _channelWidth[i];
            _beforeHeight[i] = _channelHeight[i];
            bytes += (long)_channelWidth[i] * _channelHeight[i] * sizeof(float);

            // Vacate the live slot WITHOUT destroying anything: zeroing the dimensions is what
            // makes the next UploadChannelTexture take its create-fresh branch instead of writing
            // over the pixels just retained.
            _channelImages[i] = VkImage.Null;
            _channelMemories[i] = VkDeviceMemory.Null;
            _channelViews[i] = VkImageView.Null;
            _channelWidth[i] = 0;
            _channelHeight[i] = 0;

            if (_beforeViews[i] != VkImageView.Null)
            {
                BindChannelSampler(i, _beforeViews[i], _beforeSamplerSet);
            }
        }

        _beforeChannelBytes = bytes;
        HasBeforeChannels = true;

        // Declare it: a VkImage is invisible to the GC, so without this the runtime believes it has
        // ~100 MB more headroom than it does -- and the weak-reference DocumentCache goes on keeping
        // stale documents alive at exactly the moment this cache made memory scarce.
        GC.AddMemoryPressure(bytes);
        return true;
    }

    /// <summary>
    /// Frees the retained before textures. Safe to call when nothing is retained. Call on the render
    /// thread, outside command-buffer recording.
    /// </summary>
    public void ReleaseBeforeChannels()
    {
        if (!HasBeforeChannels)
        {
            return;
        }

        var api = _ctx.DeviceApi;
        for (var i = 0; i < ChannelCount; i++)
        {
            if (_beforeViews[i] != VkImageView.Null)
            {
                api.vkDestroyImageView(_beforeViews[i]);
                _beforeViews[i] = VkImageView.Null;
            }
            if (_beforeImages[i] != VkImage.Null)
            {
                api.vkDestroyImage(_beforeImages[i]);
                _beforeImages[i] = VkImage.Null;
            }
            if (_beforeMemories[i] != VkDeviceMemory.Null)
            {
                api.vkFreeMemory(_beforeMemories[i]);
                _beforeMemories[i] = VkDeviceMemory.Null;
            }
            _beforeWidth[i] = 0;
            _beforeHeight[i] = 0;
        }

        if (_beforeChannelBytes > 0)
        {
            GC.RemoveMemoryPressure(_beforeChannelBytes);
        }
        _beforeChannelBytes = 0;
        HasBeforeChannels = false;

        // Re-point the (still bindable) before set at the live views, so it can never reference the
        // views just destroyed.
        //
        // NOT during teardown. Dispose destroys the descriptor pool (which frees every set, this one
        // included) and the sampler BEFORE it releases the before channels, so re-pointing there
        // writes a freed descriptor set with a destroyed sampler -- an access violation inside
        // vkUpdateDescriptorSets that takes the process down with exit 139 on window close. Guarding
        // on _disposed rather than reordering Dispose: the flag is set before anything is destroyed,
        // so this holds no matter how the teardown sequence is later rearranged, and there is nothing
        // to re-point at when every descriptor is about to be freed anyway.
        if (_disposed)
        {
            return;
        }

        for (var i = 0; i < ChannelCount; i++)
        {
            if (_channelViews[i] != VkImageView.Null)
            {
                BindChannelSampler(i, _channelViews[i], _beforeSamplerSet);
            }
        }
    }

    // Creates a live channel texture, and if the device is out of memory, frees the retained before
    // set and tries once more. The before textures are a CACHE: the live image is what the user is
    // looking at, so it always wins the memory. Without this the enhance apply would surface an
    // allocation failure as a broken viewer rather than as a quietly-dropped comparison.
    private void CreateChannelTextureOrFreeBefore(int channel, int width, int height)
    {
        try
        {
            CreateChannelTexture(channel, width, height);
        }
        catch (VkException) when (HasBeforeChannels)
        {
            ReleaseBeforeChannels();
            CreateChannelTexture(channel, width, height);
        }
    }

    /// <summary>
    /// Uploads 512 R32F histogram bins into histogram slot <paramref name="channel"/> (0-based).
    /// The data is stored as a 512×1 2D texture.
    /// </summary>
    public void UploadHistogramTexture(ReadOnlySpan<float> data, int channel)
    {
        if ((uint)channel >= ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel));

        var byteSize = (ulong)(data.Length * sizeof(float));
        EnsureStagingBuffer(byteSize);
        CopyToStaging(data, byteSize);
        UploadToImage(_histImages[channel], HistogramBins, 1, byteSize, VkFormat.R32Sfloat);
    }

    /// <summary>
    /// Writes all stretch parameters into the persistently-mapped stretch UBO.
    /// The <paramref name="cmd"/> parameter is unused (coherent memory, no flush needed) but
    /// kept for API symmetry with future non-coherent implementations.
    /// </summary>
    /// <summary>Image source mode for the fragment shader.</summary>
    public enum ImageSource
    {
        /// <summary>Pre-debayered channels (existing path: 1-3 separate R32F textures).</summary>
        ProcessedChannels = 0,
        /// <summary>Raw mono: single R32F texture, no debayer needed.</summary>
        RawMono = 1,
        /// <summary>Raw Bayer mosaic: single R32F texture, bilinear debayer in shader.</summary>
        RawBayer = 2,
    }

    /// <summary>
    /// Diagnostic: copies the entire stretch UBO contents into <paramref name="destination"/>.
    /// The UBO is host-visible host-coherent, so this is just a host-to-host memcpy --
    /// it reflects exactly what the GPU will read at draw time.
    /// </summary>
    public unsafe void ReadStretchUboBytes(Span<byte> destination)
    {
        new ReadOnlySpan<byte>(_stretchUboMapped, Math.Min(destination.Length, StretchUboSize)).CopyTo(destination);
    }

    public void UpdateStretchUBO(
        VkCommandBuffer cmd,
        int channelCount, int stretchMode, float normFactor,
        float curvesBoost, float curvesMidpoint, float hdrAmount, float hdrKnee,
        (float R, float G, float B) pedestal,
        (float R, float G, float B) shadows,
        (float R, float G, float B) midtones,
        (float R, float G, float B) highlights,
        (float R, float G, float B) rescale,
        bool gridEnabled, float gridSpacingRA, float gridSpacingDec, float gridLineWidth,
        float imageW, float imageH, float crPix1, float crPix2,
        float crValRA, float crValDec,
        ReadOnlySpan<float> cdMatrix,
        (float R, float G, float B) whiteBalance = default,
        (float R, float G, float B) bgNeutralization = default,
        int curvesMode = 0,
        ReadOnlySpan<float> curveData = default,
        ImageSource imageSource = ImageSource.ProcessedChannels,
        int bayerOffsetX = 0, int bayerOffsetY = 0,
        (float R, float G, float B) lumaWeights = default,
        (float Shadow, float Midtones, float Rescale) lumaStretch = default,
        float lumaBlend = 1f,
        float normalizeScale = 1f,
        int debayerMode = 1,
        int slot = UboSlotPrimary)
    {
        if ((uint)slot >= StretchUboSlots)
            throw new ArgumentOutOfRangeException(nameof(slot));

        var p = _stretchUboMapped + slot * _stretchUboSlotStride;

        WriteInt(p, 0, channelCount);
        WriteInt(p, 4, stretchMode);
        WriteFloat(p, 8, normFactor);
        WriteFloat(p, 12, curvesBoost);
        WriteFloat(p, 16, curvesMidpoint);
        WriteFloat(p, 20, hdrAmount);
        WriteFloat(p, 24, hdrKnee);
        WriteInt(p, 28, curvesMode);

        // pedestal (vec4 at offset 32)
        WriteFloat(p, 32, pedestal.R);
        WriteFloat(p, 36, pedestal.G);
        WriteFloat(p, 40, pedestal.B);
        WriteFloat(p, 44, 0f);

        // shadows (vec4 at offset 48)
        WriteFloat(p, 48, shadows.R);
        WriteFloat(p, 52, shadows.G);
        WriteFloat(p, 56, shadows.B);
        WriteFloat(p, 60, 0f);

        // midtones (vec4 at offset 64)
        WriteFloat(p, 64, midtones.R);
        WriteFloat(p, 68, midtones.G);
        WriteFloat(p, 72, midtones.B);
        WriteFloat(p, 76, 0f);

        // highlights (vec4 at offset 80)
        WriteFloat(p, 80, highlights.R);
        WriteFloat(p, 84, highlights.G);
        WriteFloat(p, 88, highlights.B);
        WriteFloat(p, 92, 0f);

        // rescale (vec4 at offset 96)
        WriteFloat(p, 96, rescale.R);
        WriteFloat(p, 100, rescale.G);
        WriteFloat(p, 104, rescale.B);
        WriteFloat(p, 108, 0f);

        WriteInt(p, 112, gridEnabled ? 1 : 0);
        WriteFloat(p, 116, gridSpacingRA);
        WriteFloat(p, 120, gridSpacingDec);
        WriteFloat(p, 124, gridLineWidth);

        // imageSize (vec2 at offset 128)
        WriteFloat(p, 128, imageW);
        WriteFloat(p, 132, imageH);

        // crPix (vec2 at offset 136)
        WriteFloat(p, 136, crPix1);
        WriteFloat(p, 140, crPix2);

        // crVal (vec2 at offset 144)
        WriteFloat(p, 144, crValRA);
        WriteFloat(p, 148, crValDec);

        WriteInt(p, 152, (int)imageSource);
        WriteInt(p, 156, bayerOffsetX + bayerOffsetY * 65536);

        // cdMatrix stored col-major as 2 vec4s:
        // cdCol0 at offset 160: (cd[0,0], cd[1,0], 0, 0)
        // cdCol1 at offset 176: (cd[0,1], cd[1,1], 0, 0)
        var cd00 = cdMatrix.Length > 0 ? cdMatrix[0] : 0f;
        var cd10 = cdMatrix.Length > 1 ? cdMatrix[1] : 0f;
        var cd01 = cdMatrix.Length > 2 ? cdMatrix[2] : 0f;
        var cd11 = cdMatrix.Length > 3 ? cdMatrix[3] : 0f;

        WriteFloat(p, 160, cd00);
        WriteFloat(p, 164, cd10);
        WriteFloat(p, 168, 0f);
        WriteFloat(p, 172, 0f);

        WriteFloat(p, 176, cd01);
        WriteFloat(p, 180, cd11);
        WriteFloat(p, 184, 0f);
        WriteFloat(p, 188, 0f);

        // whiteBalance (vec4 at offset 192)
        WriteFloat(p, 192, whiteBalance.R);
        WriteFloat(p, 196, whiteBalance.G);
        WriteFloat(p, 200, whiteBalance.B);
        WriteFloat(p, 204, 0f);

        // bgNeutralization (vec4 at offset 208)
        WriteFloat(p, 208, bgNeutralization.R);
        WriteFloat(p, 212, bgNeutralization.G);
        WriteFloat(p, 216, bgNeutralization.B);
        WriteFloat(p, 220, 0f);

        // curveData[9] = 33 knots packed into 9 std140 vec4 slots. Trailing 3 floats stay
        // zero (UBO is zero-initialized in CreateMappedBuffer) and are never read by the shader.
        for (var i = 0; i < 33 && i < curveData.Length; i++)
        {
            WriteFloat(p, 224 + i * 4, curveData[i]);
        }

        // lumaWeights (vec4 at offset 368). Caller passes the (R,G,B) triple from
        // StretchUniforms.LumaWeights; default = (0,0,0) flags the shader to use
        // a tuple shipped by callers that haven't migrated yet -- treat as Rec.709.
        var (lwR, lwG, lwB) = (lumaWeights.R != 0f || lumaWeights.G != 0f || lumaWeights.B != 0f)
            ? lumaWeights
            : LumaWeighting.Rec709.Weights;
        WriteFloat(p, 368, lwR);
        WriteFloat(p, 372, lwG);
        WriteFloat(p, 376, lwB);
        WriteFloat(p, 380, 0f);

        // lumaStretch (vec4 at offset 384) -- scalar Luma MTF params (shadow, midtones, rescale).
        // Producer leaves at zero when Mode is not Luma; the shader only reads these in the
        // Luma branch so the zeros are inert outside it.
        WriteFloat(p, 384, lumaStretch.Shadow);
        WriteFloat(p, 388, lumaStretch.Midtones);
        WriteFloat(p, 392, lumaStretch.Rescale);
        WriteFloat(p, 396, 0f);

        // stretchBlend (vec4 at offset 400) -- (lumaBlend, normalizeScale, debayerMode, pad).
        // debayerMode (z) selects the in-shader Bayer demosaic for the RawBayer path: 1 = MHC, else bilinear.
        WriteFloat(p, 400, lumaBlend);
        WriteFloat(p, 404, normalizeScale);
        WriteFloat(p, 408, debayerMode);
        WriteFloat(p, 412, 0f);
    }

    /// <summary>
    /// Writes histogram parameters into the persistently-mapped histogram UBO.
    /// </summary>
    public void UpdateHistogramUBO(
        VkCommandBuffer cmd,
        int channelCount, float logPeak, float linearPeak, bool logScale)
    {
        var p = _histogramUboMapped;
        WriteInt(p, 0, channelCount);
        WriteFloat(p, 4, logPeak);
        WriteFloat(p, 8, linearPeak);
        WriteInt(p, 12, logScale ? 1 : 0);
    }

    /// <summary>
    /// Records the image quad draw into <paramref name="cmd"/>.
    /// Binds the image pipeline, descriptor sets, push constants, quad vertices, and calls vkCmdDraw.
    /// </summary>
    /// <param name="uboSlot">Which stretch-UBO slot supplies the display parameters:
    /// <see cref="UboSlotPrimary"/> for the live rendition, <see cref="UboSlotComparison"/> for the
    /// before/after split's comparison rendition.</param>
    /// <param name="sampleBeforeChannels">Sample the retained pre-enhance textures instead of the live
    /// ones. Ignored (falls back to the live textures) when nothing is retained, so a caller can never
    /// bind a set whose views were freed underneath it.</param>
    /// <remarks>
    /// This method does NOT touch the scissor. The split's two halves are clipped by the caller through
    /// DIR.Lib's clip stack, which owns the intersect-with-parent rule and the restore -- a scissor set
    /// here would REPLACE the enclosing clip rather than narrow it, and nothing would put it back.
    /// </remarks>
    public void RecordImageDraw(
        VkCommandBuffer cmd,
        VulkanContext ctx,
        float left, float top, float right, float bottom,
        float projW, float projH,
        int uboSlot = UboSlotPrimary,
        bool sampleBeforeChannels = false)
    {
        if ((uint)uboSlot >= StretchUboSlots)
            throw new ArgumentOutOfRangeException(nameof(uboSlot));

        var api = ctx.DeviceApi;

        api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _imagePipeline);

        // Bind set 0 (UBO) and set 1 (samplers)
        var uboSet = uboSlot == UboSlotComparison ? _imageUboSetComparison : _imageUboSet;
        var samplerSet = sampleBeforeChannels && HasBeforeChannels ? _beforeSamplerSet : _imageSamplerSet;
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &uboSet, 0, null);
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pipelineLayout,
            1, 1, &samplerSet, 0, null);

        PushProjectionAndDraw(cmd, ctx, left, top, right, bottom, projW, projH);
    }

    /// <summary>
    /// Records the histogram quad draw into <paramref name="cmd"/>.
    /// </summary>
    public void RecordHistogramDraw(
        VkCommandBuffer cmd,
        VulkanContext ctx,
        float left, float top, float right, float bottom,
        float projW, float projH)
    {
        var api = ctx.DeviceApi;

        api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _histogramPipeline);

        var uboSet = _histogramUboSet;
        var samplerSet = _histogramSamplerSet;
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &uboSet, 0, null);
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _pipelineLayout,
            1, 1, &samplerSet, 0, null);

        PushProjectionAndDraw(cmd, ctx, left, top, right, bottom, projW, projH);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var api = _ctx.DeviceApi;

        // Skip the pre-teardown drain when the GPU is known wedged; an unbounded wait on a stuck
        // device would hang Dispose (matches the renderer's recovery/teardown guards).
        if (!_ctx.IsGpuStuck)
        {
            api.vkDeviceWaitIdle();
        }

        // Pipelines
        if (_imagePipeline != VkPipeline.Null)
            api.vkDestroyPipeline(_imagePipeline);
        if (_histogramPipeline != VkPipeline.Null)
            api.vkDestroyPipeline(_histogramPipeline);

        // Pipeline layout
        if (_pipelineLayout != VkPipelineLayout.Null)
            api.vkDestroyPipelineLayout(_pipelineLayout);

        // Descriptor set layouts
        if (_uboSetLayout != VkDescriptorSetLayout.Null)
            api.vkDestroyDescriptorSetLayout(_uboSetLayout);
        if (_samplerSetLayout != VkDescriptorSetLayout.Null)
            api.vkDestroyDescriptorSetLayout(_samplerSetLayout);

        // Descriptor pool
        if (_descriptorPool != VkDescriptorPool.Null)
            api.vkDestroyDescriptorPool(_descriptorPool);

        // Sampler
        if (_linearSampler != VkSampler.Null)
            api.vkDestroySampler(_linearSampler);

        // Channel textures (incl. any retained before set)
        ReleaseBeforeChannels();
        for (var i = 0; i < ChannelCount; i++)
        {
            DestroyChannelTexture(i);
            DestroyHistogramTexture(i);
        }

        // UBO buffers
        if (_stretchUboBuffer != VkBuffer.Null)
        {
            api.vkUnmapMemory(_stretchUboMemory);
            api.vkDestroyBuffer(_stretchUboBuffer);
            api.vkFreeMemory(_stretchUboMemory);
        }
        if (_histogramUboBuffer != VkBuffer.Null)
        {
            api.vkUnmapMemory(_histogramUboMemory);
            api.vkDestroyBuffer(_histogramUboBuffer);
            api.vkFreeMemory(_histogramUboMemory);
        }

        // Staging buffer
        if (_stagingBuffer != VkBuffer.Null)
        {
            api.vkDestroyBuffer(_stagingBuffer);
            api.vkFreeMemory(_stagingMemory);
        }
    }

    // ------------------------------------------------------------------ Private helpers

    private void CreateDescriptorSetLayouts()
    {
        var api = _ctx.DeviceApi;

        // Set 0: single UBO binding (vertex + fragment)
        VkDescriptorSetLayoutBinding uboBinding = new()
        {
            binding = 0,
            descriptorType = VkDescriptorType.UniformBuffer,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment
        };
        VkDescriptorSetLayoutCreateInfo uboLayoutCI = new()
        {
            bindingCount = 1,
            pBindings = &uboBinding
        };
        api.vkCreateDescriptorSetLayout(&uboLayoutCI, null, out _uboSetLayout).CheckResult();

        // Set 1: 3x combined image sampler bindings (fragment only)
        var samplerBindings = stackalloc VkDescriptorSetLayoutBinding[ChannelCount];
        for (uint i = 0; i < ChannelCount; i++)
        {
            samplerBindings[i] = new VkDescriptorSetLayoutBinding
            {
                binding = i,
                descriptorType = VkDescriptorType.CombinedImageSampler,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Fragment
            };
        }
        VkDescriptorSetLayoutCreateInfo samplerLayoutCI = new()
        {
            bindingCount = ChannelCount,
            pBindings = samplerBindings
        };
        api.vkCreateDescriptorSetLayout(&samplerLayoutCI, null, out _samplerSetLayout).CheckResult();
    }

    private void CreateDescriptorPool()
    {
        var api = _ctx.DeviceApi;

        // 3 UBO descriptors (image slot 0 + image slot 1 + histogram) + 9 sampler descriptors
        // (3 image + 3 before-image + 3 histogram).
        var poolSizes = stackalloc VkDescriptorPoolSize[2];
        poolSizes[0] = new VkDescriptorPoolSize
        {
            type = VkDescriptorType.UniformBuffer,
            descriptorCount = 3
        };
        poolSizes[1] = new VkDescriptorPoolSize
        {
            type = VkDescriptorType.CombinedImageSampler,
            descriptorCount = 3 * ChannelCount
        };

        VkDescriptorPoolCreateInfo dpCI = new()
        {
            maxSets = 6, // imageUBO + imageUBO(comparison) + histUBO + imageSamplers + beforeSamplers + histSamplers
            poolSizeCount = 2,
            pPoolSizes = poolSizes
        };
        api.vkCreateDescriptorPool(&dpCI, null, out _descriptorPool).CheckResult();
    }

    private void AllocateDescriptorSets()
    {
        var api = _ctx.DeviceApi;

        // Allocate all 6 sets at once
        const int SetCount = 6;
        var layouts = stackalloc VkDescriptorSetLayout[SetCount];
        layouts[0] = _uboSetLayout;       // image UBO, slot 0 (live)
        layouts[1] = _uboSetLayout;       // image UBO, slot 1 (comparison)
        layouts[2] = _uboSetLayout;       // histogram UBO
        layouts[3] = _samplerSetLayout;   // image samplers
        layouts[4] = _samplerSetLayout;   // before-image samplers
        layouts[5] = _samplerSetLayout;   // histogram samplers

        var sets = stackalloc VkDescriptorSet[SetCount];
        VkDescriptorSetAllocateInfo dsAI = new()
        {
            descriptorPool = _descriptorPool,
            descriptorSetCount = SetCount,
            pSetLayouts = layouts
        };
        api.vkAllocateDescriptorSets(&dsAI, sets).CheckResult();

        _imageUboSet = sets[0];
        _imageUboSetComparison = sets[1];
        _histogramUboSet = sets[2];
        _imageSamplerSet = sets[3];
        _beforeSamplerSet = sets[4];
        _histogramSamplerSet = sets[5];
    }

    private void CreatePipelineLayout()
    {
        var api = _ctx.DeviceApi;

        // Push constant: mat4 (64 bytes), vertex stage only
        VkPushConstantRange pushRange = new()
        {
            stageFlags = VkShaderStageFlags.Vertex,
            offset = 0,
            size = 64
        };

        var setLayouts = stackalloc VkDescriptorSetLayout[2];
        setLayouts[0] = _uboSetLayout;
        setLayouts[1] = _samplerSetLayout;

        VkPipelineLayoutCreateInfo plCI = new()
        {
            setLayoutCount = 2,
            pSetLayouts = setLayouts,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushRange
        };
        api.vkCreatePipelineLayout(&plCI, null, out _pipelineLayout).CheckResult();
    }

    private void CreateSampler()
    {
        // R32_SFLOAT is NOT mandatorily filterable per the Vulkan spec -- the
        // `SAMPLED_IMAGE_FILTER_LINEAR_BIT` feature flag is optional for 32-bit float
        // formats. Hardware desktop GPUs (NVIDIA/AMD/Intel) advertise it; Mesa lavapipe
        // (software rasterizer, used in CI without a GPU) does not. Sampling with a
        // linear filter on a format that doesn't support it is undefined behavior, and on
        // lavapipe the sampler silently returns 0 -- which surfaces as a fully-black
        // viewer instead of a stretched FITS image. Query and adapt at sampler creation
        // time.
        _ctx.InstanceApi.vkGetPhysicalDeviceFormatProperties(
            _ctx.PhysicalDevice, VkFormat.R32Sfloat, out var floatProps);
        var linearSupported = (floatProps.optimalTilingFeatures
            & VkFormatFeatureFlags.SampledImageFilterLinear) != 0;
        // Exposed via the public property below so tests can verify which branch was taken.
        _r32SfloatOptimalTilingFeatures = floatProps.optimalTilingFeatures;
        _r32SfloatLinearFilterSupported = linearSupported;
        var minFilter = linearSupported ? VkFilter.Linear : VkFilter.Nearest;
        var mipmapMode = linearSupported ? VkSamplerMipmapMode.Linear : VkSamplerMipmapMode.Nearest;

        VkSamplerCreateInfo samplerCI = new()
        {
            magFilter = VkFilter.Nearest,
            minFilter = minFilter,
            addressModeU = VkSamplerAddressMode.ClampToEdge,
            addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge,
            mipmapMode = mipmapMode,
            maxLod = 1.0f
        };
        _ctx.DeviceApi.vkCreateSampler(&samplerCI, null, out _linearSampler).CheckResult();
    }

    private void CreateUboBuffers()
    {
        // A descriptor addresses a slot by byte offset, and Vulkan requires that offset to be a
        // multiple of minUniformBufferOffsetAlignment (256 on plenty of real hardware), so the slot
        // stride is the UBO size rounded UP to that -- never StretchUboSize itself.
        _ctx.InstanceApi.vkGetPhysicalDeviceProperties(_ctx.PhysicalDevice, out var deviceProps);
        var alignment = (int)Math.Max(1UL, deviceProps.limits.minUniformBufferOffsetAlignment);
        _stretchUboSlotStride = (StretchUboSize + alignment - 1) / alignment * alignment;

        CreateMappedBuffer(_stretchUboSlotStride * StretchUboSlots,
            out _stretchUboBuffer, out _stretchUboMemory, out _stretchUboMapped);
        CreateMappedBuffer(HistogramUboSize, out _histogramUboBuffer, out _histogramUboMemory, out _histogramUboMapped);

        // Write initial UBO descriptors. The two image sets differ ONLY in which slot of the same
        // buffer they address; `range` stays StretchUboSize because that is what the shader declares.
        BindUboDescriptor(_imageUboSet, _stretchUboBuffer, StretchUboSize,
            offset: UboSlotPrimary * _stretchUboSlotStride);
        BindUboDescriptor(_imageUboSetComparison, _stretchUboBuffer, StretchUboSize,
            offset: UboSlotComparison * _stretchUboSlotStride);
        BindUboDescriptor(_histogramUboSet, _histogramUboBuffer, HistogramUboSize);
    }

    private void CreateMappedBuffer(int size, out VkBuffer buffer, out VkDeviceMemory memory, out byte* mapped)
    {
        var api = _ctx.DeviceApi;
        var usize = (ulong)size;

        VkBufferCreateInfo bufCI = new()
        {
            size = usize,
            usage = VkBufferUsageFlags.UniformBuffer,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out buffer).CheckResult();

        api.vkGetBufferMemoryRequirements(buffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        api.vkAllocateMemory(&allocInfo, null, out memory).CheckResult();
        api.vkBindBufferMemory(buffer, memory, 0);

        void* ptr;
        api.vkMapMemory(memory, 0, usize, 0, &ptr);
        mapped = (byte*)ptr;

        // Zero-initialise
        new Span<byte>(ptr, size).Clear();
    }

    private void BindUboDescriptor(VkDescriptorSet set, VkBuffer buffer, int size, int offset = 0)
    {
        var api = _ctx.DeviceApi;
        VkDescriptorBufferInfo bufInfo = new()
        {
            buffer = buffer,
            offset = (ulong)offset,
            range = (ulong)size
        };
        VkWriteDescriptorSet write = new()
        {
            dstSet = set,
            dstBinding = 0,
            dstArrayElement = 0,
            descriptorType = VkDescriptorType.UniformBuffer,
            descriptorCount = 1,
            pBufferInfo = &bufInfo
        };
        api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    private void CreatePlaceholderTextures()
    {
        // Create 1×1 placeholder images so descriptor sets are always valid
        var placeholder = new float[] { 0f };
        var byteSize = (ulong)(placeholder.Length * sizeof(float));
        EnsureStagingBuffer(byteSize);
        CopyToStaging(placeholder.AsSpan(), byteSize);

        for (var i = 0; i < ChannelCount; i++)
        {
            CreateChannelTexture(i, 1, 1);
            UploadToImage(_channelImages[i], 1, 1, byteSize, VkFormat.R32Sfloat);
            _channelWidth[i] = 1;
            _channelHeight[i] = 1;
            BindChannelSampler(i, _channelViews[i], _imageSamplerSet);
            // The before set must hold VALID descriptors from the start: it is a bindable set, and
            // Vulkan forbids binding one that references a destroyed view even if the shader never
            // samples it. With no before retained it simply mirrors the live views.
            BindChannelSampler(i, _channelViews[i], _beforeSamplerSet);
        }

        // The histogram placeholders are FULL-WIDTH (HistogramBins x 1), unlike the 1x1 channel
        // placeholders above, so they need their own staging of HistogramBins zero floats -- staged
        // once, uploaded three times (UploadToImage is synchronous, so the reuse is safe). Reusing
        // the 1-float staging recorded a 2048-byte copy against a 4-byte buffer on every startup: a
        // GPU read 2044 bytes past the end of the allocation, once per channel
        // (VUID-vkCmdCopyBufferToImage-pRegions-00171), which a desktop driver absorbs silently and
        // a mobile part is entitled to fault on.
        var histPlaceholder = new float[HistogramBins];
        var histByteSize = (ulong)(histPlaceholder.Length * sizeof(float));
        EnsureStagingBuffer(histByteSize);
        CopyToStaging(histPlaceholder.AsSpan(), histByteSize);

        for (var i = 0; i < ChannelCount; i++)
        {
            CreateHistogramTexture(i);
            UploadToImage(_histImages[i], HistogramBins, 1, histByteSize, VkFormat.R32Sfloat);
            BindChannelSampler(i, _histViews[i], _histogramSamplerSet);
        }
    }

    private void CreateChannelTexture(int channel, int width, int height)
    {
        var api = _ctx.DeviceApi;

        VkImageCreateInfo imageCI = new()
        {
            imageType = VkImageType.Image2D,
            format = VkFormat.R32Sfloat,
            extent = new VkExtent3D((uint)width, (uint)height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            // TransferDst -- needed for vkCmdCopyBufferToImage uploads.
            // Sampled     -- needed for the fragment shader to read the channel.
            // TransferSrc -- needed for ReadbackChannelFirstFloats (test diagnostic) AND for
            //                lavapipe to honour the ShaderReadOnlyOptimal -> TransferSrcOptimal
            //                barrier without leaving the image contents undefined. Without
            //                it, validation flags VUID-VkImageMemoryBarrier-oldLayout-01212
            //                and lavapipe returns 0 from subsequent shader samples.
            usage = VkImageUsageFlags.TransferDst | VkImageUsageFlags.TransferSrc | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined
        };
        api.vkCreateImage(&imageCI, null, out _channelImages[channel]).CheckResult();

        api.vkGetImageMemoryRequirements(_channelImages[channel], out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        api.vkAllocateMemory(&allocInfo, null, out _channelMemories[channel]).CheckResult();
        api.vkBindImageMemory(_channelImages[channel], _channelMemories[channel], 0);

        _ctx.ExecuteOneShot(cmd =>
            TransitionImageLayout(cmd, _channelImages[channel],
                VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal));

        var viewCI = new VkImageViewCreateInfo(
            _channelImages[channel], VkImageViewType.Image2D, VkFormat.R32Sfloat,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        api.vkCreateImageView(&viewCI, null, out _channelViews[channel]).CheckResult();
    }

    private void CreateHistogramTexture(int channel)
    {
        var api = _ctx.DeviceApi;

        VkImageCreateInfo imageCI = new()
        {
            imageType = VkImageType.Image2D,
            format = VkFormat.R32Sfloat,
            extent = new VkExtent3D(HistogramBins, 1, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined
        };
        api.vkCreateImage(&imageCI, null, out _histImages[channel]).CheckResult();

        api.vkGetImageMemoryRequirements(_histImages[channel], out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        api.vkAllocateMemory(&allocInfo, null, out _histMemories[channel]).CheckResult();
        api.vkBindImageMemory(_histImages[channel], _histMemories[channel], 0);

        _ctx.ExecuteOneShot(cmd =>
            TransitionImageLayout(cmd, _histImages[channel],
                VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal));

        var viewCI = new VkImageViewCreateInfo(
            _histImages[channel], VkImageViewType.Image2D, VkFormat.R32Sfloat,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        api.vkCreateImageView(&viewCI, null, out _histViews[channel]).CheckResult();
    }

    private void DestroyChannelTexture(int channel)
    {
        var api = _ctx.DeviceApi;
        if (_channelViews[channel] != VkImageView.Null)
        {
            api.vkDestroyImageView(_channelViews[channel]);
            _channelViews[channel] = VkImageView.Null;
        }
        if (_channelImages[channel] != VkImage.Null)
        {
            api.vkDestroyImage(_channelImages[channel]);
            _channelImages[channel] = VkImage.Null;
        }
        if (_channelMemories[channel] != VkDeviceMemory.Null)
        {
            api.vkFreeMemory(_channelMemories[channel]);
            _channelMemories[channel] = VkDeviceMemory.Null;
        }
        _channelWidth[channel] = 0;
        _channelHeight[channel] = 0;
    }

    private void DestroyHistogramTexture(int channel)
    {
        var api = _ctx.DeviceApi;
        if (_histViews[channel] != VkImageView.Null)
        {
            api.vkDestroyImageView(_histViews[channel]);
            _histViews[channel] = VkImageView.Null;
        }
        if (_histImages[channel] != VkImage.Null)
        {
            api.vkDestroyImage(_histImages[channel]);
            _histImages[channel] = VkImage.Null;
        }
        if (_histMemories[channel] != VkDeviceMemory.Null)
        {
            api.vkFreeMemory(_histMemories[channel]);
            _histMemories[channel] = VkDeviceMemory.Null;
        }
    }

    private void BindChannelSampler(int channel, VkImageView view, VkDescriptorSet set)
    {
        var api = _ctx.DeviceApi;
        VkDescriptorImageInfo imageInfo = new()
        {
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
            imageView = view,
            sampler = _linearSampler
        };
        VkWriteDescriptorSet write = new()
        {
            dstSet = set,
            dstBinding = (uint)channel,
            dstArrayElement = 0,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            descriptorCount = 1,
            pImageInfo = &imageInfo
        };
        api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    /// <summary>
    /// The host-visible staging buffer's current size in bytes, 0 when none is allocated. Exposed so a
    /// headless test can assert the trim happened -- a <c>VkBuffer</c> is invisible to the GC, so there
    /// is nothing else to observe it by, which is also why this cost went unnoticed for so long.
    ///
    /// <para>Public rather than internal because this assembly grants no <c>InternalsVisibleTo</c>, and
    /// the neighbouring test-facing members (<see cref="R32SfloatOptimalTilingFeatures"/>,
    /// <see cref="ReadbackChannelFirstFloats"/>) are public for the same reason.</para>
    /// </summary>
    public ulong StagingBufferSize => _stagingSize;

    /// <summary>
    /// Releases the staging buffer, so the high-water mark of one large upload stops following the
    /// process around.
    /// </summary>
    /// <remarks>
    /// <para><see cref="EnsureStagingBuffer"/> is grow-only and was freed ONLY on dispose, so one channel
    /// of a big document pinned that much host-visible memory for the process lifetime: a 13228x9354
    /// document measures 472 MiB, and every small FITS opened afterwards still carried it. Nothing
    /// reported it either -- a Vulkan buffer is not managed memory, so it does not even show as GC
    /// pressure.</para>
    ///
    /// <para><b>This is deliberately NOT done inside <see cref="UploadChannelTexture"/>.</b> That is the
    /// obvious shape and it is wrong: the live preview path uploads a channel PER FRAME, and a colour
    /// sensor's mosaic is one large channel (an ASI2600 frame is ~104 MB), so freeing after every upload
    /// would turn a stable allocation into an alloc/free per frame on the imaging hot path. Only the
    /// caller knows a burst of uploads has ended, which is why this is an explicit trim.</para>
    ///
    /// <para>A size cap ("release anything over 32 MiB") was considered and rejected for the same reason:
    /// it cannot tell a document load from a large live frame, so it would churn exactly the path that
    /// needs the buffer retained.</para>
    ///
    /// <para>Needs no fence: <see cref="UploadToImage"/> is synchronous, so by the time a caller can ask
    /// for this the copy has already completed.</para>
    /// </remarks>
    public void TrimStagingBuffer()
    {
        if (_stagingBuffer == VkBuffer.Null)
        {
            return;
        }

        var api = _ctx.DeviceApi;
        api.vkDestroyBuffer(_stagingBuffer);
        api.vkFreeMemory(_stagingMemory);
        _stagingBuffer = VkBuffer.Null;
        _stagingMemory = VkDeviceMemory.Null;
        _stagingSize = 0;
    }

    private void EnsureStagingBuffer(ulong size)
    {
        if (_stagingBuffer != VkBuffer.Null && _stagingSize >= size)
            return;

        var api = _ctx.DeviceApi;

        if (_stagingBuffer != VkBuffer.Null)
        {
            api.vkDestroyBuffer(_stagingBuffer);
            api.vkFreeMemory(_stagingMemory);
        }

        VkBufferCreateInfo bufCI = new()
        {
            size = size,
            usage = VkBufferUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out _stagingBuffer).CheckResult();

        api.vkGetBufferMemoryRequirements(_stagingBuffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        api.vkAllocateMemory(&allocInfo, null, out _stagingMemory).CheckResult();
        api.vkBindBufferMemory(_stagingBuffer, _stagingMemory, 0);
        _stagingSize = size;
    }

    private void CopyToStaging(ReadOnlySpan<float> data, ulong byteSize)
    {
        var api = _ctx.DeviceApi;
        void* mapped;
        api.vkMapMemory(_stagingMemory, 0, byteSize, 0, &mapped);
        fixed (float* pSrc = data)
            Buffer.MemoryCopy(pSrc, mapped, (long)byteSize, (long)byteSize);
        api.vkUnmapMemory(_stagingMemory);
    }

    private void UploadToImage(VkImage image, uint width, uint height, ulong byteSize, VkFormat format)
    {
        _ctx.ExecuteOneShot(cmd =>
        {
            TransitionImageLayout(cmd, image,
                VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferDstOptimal);

            VkBufferImageCopy region = new()
            {
                bufferOffset = 0,
                bufferRowLength = 0,
                bufferImageHeight = 0,
                imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
                imageOffset = new VkOffset3D(0, 0, 0),
                imageExtent = new VkExtent3D(width, height, 1)
            };
            _ctx.DeviceApi.vkCmdCopyBufferToImage(cmd, _stagingBuffer, image,
                VkImageLayout.TransferDstOptimal, 1, &region);

            TransitionImageLayout(cmd, image,
                VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);
        });
    }

    private void TransitionImageLayout(VkCommandBuffer cmd, VkImage image,
        VkImageLayout oldLayout, VkImageLayout newLayout)
    {
        VkImageMemoryBarrier barrier = new()
        {
            oldLayout = oldLayout,
            newLayout = newLayout,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = image,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1)
        };

        VkPipelineStageFlags srcStage, dstStage;

        if (oldLayout == VkImageLayout.Undefined && newLayout == VkImageLayout.TransferDstOptimal)
        {
            barrier.srcAccessMask = 0;
            barrier.dstAccessMask = VkAccessFlags.TransferWrite;
            srcStage = VkPipelineStageFlags.TopOfPipe;
            dstStage = VkPipelineStageFlags.Transfer;
        }
        else if (oldLayout == VkImageLayout.TransferDstOptimal && newLayout == VkImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.srcAccessMask = VkAccessFlags.TransferWrite;
            barrier.dstAccessMask = VkAccessFlags.ShaderRead;
            srcStage = VkPipelineStageFlags.Transfer;
            dstStage = VkPipelineStageFlags.FragmentShader;
        }
        else if (oldLayout == VkImageLayout.ShaderReadOnlyOptimal && newLayout == VkImageLayout.TransferDstOptimal)
        {
            barrier.srcAccessMask = VkAccessFlags.ShaderRead;
            barrier.dstAccessMask = VkAccessFlags.TransferWrite;
            srcStage = VkPipelineStageFlags.FragmentShader;
            dstStage = VkPipelineStageFlags.Transfer;
        }
        else if (oldLayout == VkImageLayout.Undefined && newLayout == VkImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.srcAccessMask = 0;
            barrier.dstAccessMask = VkAccessFlags.ShaderRead;
            srcStage = VkPipelineStageFlags.TopOfPipe;
            dstStage = VkPipelineStageFlags.FragmentShader;
        }
        else if (oldLayout == VkImageLayout.ShaderReadOnlyOptimal && newLayout == VkImageLayout.TransferSrcOptimal)
        {
            barrier.srcAccessMask = VkAccessFlags.ShaderRead;
            barrier.dstAccessMask = VkAccessFlags.TransferRead;
            srcStage = VkPipelineStageFlags.FragmentShader;
            dstStage = VkPipelineStageFlags.Transfer;
        }
        else if (oldLayout == VkImageLayout.TransferSrcOptimal && newLayout == VkImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.srcAccessMask = VkAccessFlags.TransferRead;
            barrier.dstAccessMask = VkAccessFlags.ShaderRead;
            srcStage = VkPipelineStageFlags.Transfer;
            dstStage = VkPipelineStageFlags.FragmentShader;
        }
        else
        {
            throw new ArgumentException($"Unsupported layout transition: {oldLayout} -> {newLayout}");
        }

        _ctx.DeviceApi.vkCmdPipelineBarrier(cmd, srcStage, dstStage, 0,
            0, null, 0, null, 1, &barrier);
    }

    private void CreatePipelines()
    {

        var vertModule = LoadShaderModule("quad.vert");
        var imageFragModule = LoadShaderModule("image.frag");
        var histFragModule = LoadShaderModule("histogram.frag");

        try
        {
            VkVertexInputBindingDescription binding = new(4 * sizeof(float));
            var attrs = stackalloc VkVertexInputAttributeDescription[2];
            attrs[0] = new VkVertexInputAttributeDescription(0, VkFormat.R32G32Sfloat, 0);
            attrs[1] = new VkVertexInputAttributeDescription(1, VkFormat.R32G32Sfloat, 2 * sizeof(float));

            _imagePipeline = CreateGraphicsPipeline(
                vertModule, imageFragModule, &binding, 1, attrs, 2);
            _histogramPipeline = CreateGraphicsPipeline(
                vertModule, histFragModule, &binding, 1, attrs, 2);
        }
        finally
        {
            var api = _ctx.DeviceApi;
            api.vkDestroyShaderModule(vertModule);
            api.vkDestroyShaderModule(imageFragModule);
            api.vkDestroyShaderModule(histFragModule);
        }
    }

    private VkPipeline CreateGraphicsPipeline(
        VkShaderModule vertModule, VkShaderModule fragModule,
        VkVertexInputBindingDescription* bindings, uint bindingCount,
        VkVertexInputAttributeDescription* attributes, uint attributeCount)
    {
        var api = _ctx.DeviceApi;
        VkUtf8ReadOnlyString entryPoint = "main"u8;

        var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0] = new VkPipelineShaderStageCreateInfo
        {
            stage = VkShaderStageFlags.Vertex,
            module = vertModule,
            pName = entryPoint
        };
        stages[1] = new VkPipelineShaderStageCreateInfo
        {
            stage = VkShaderStageFlags.Fragment,
            module = fragModule,
            pName = entryPoint
        };

        VkPipelineVertexInputStateCreateInfo vertexInput = new()
        {
            vertexBindingDescriptionCount = bindingCount,
            pVertexBindingDescriptions = bindings,
            vertexAttributeDescriptionCount = attributeCount,
            pVertexAttributeDescriptions = attributes
        };

        VkPipelineInputAssemblyStateCreateInfo inputAssembly = new(VkPrimitiveTopology.TriangleList);
        VkPipelineViewportStateCreateInfo viewportState = new(1, 1);

        VkPipelineRasterizationStateCreateInfo rasterizer = new()
        {
            polygonMode = VkPolygonMode.Fill,
            lineWidth = 1.0f,
            cullMode = VkCullModeFlags.None,
            frontFace = VkFrontFace.Clockwise
        };

        VkPipelineMultisampleStateCreateInfo multisample = VkPipelineMultisampleStateCreateInfo.Default;

        // Use stackalloc with an explicit lifetime spanning vkCreateGraphicsPipeline. The
        // Vortice.Vulkan single-attachment ctor `new VkPipelineColorBlendStateCreateInfo(blendAttachment)`
        // stores pAttachments pointing at its own stack frame, which is reclaimed when the
        // ctor returns -- the subsequent vkCreateGraphicsPipeline then reads garbage blend
        // state. Same bug pattern as SdlVulkan.Renderer/VkPipelineSet.cs fixed in 3.4.471.
        var blendAttachments = stackalloc VkPipelineColorBlendAttachmentState[1];
        blendAttachments[0] = new VkPipelineColorBlendAttachmentState
        {
            colorWriteMask = VkColorComponentFlags.All,
            blendEnable = true,
            srcColorBlendFactor = VkBlendFactor.SrcAlpha,
            dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
            colorBlendOp = VkBlendOp.Add,
            srcAlphaBlendFactor = VkBlendFactor.One,
            dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
            alphaBlendOp = VkBlendOp.Add
        };

        VkPipelineColorBlendStateCreateInfo colorBlend = new()
        {
            attachmentCount = 1,
            pAttachments = blendAttachments
        };

        var dynamicStates = stackalloc VkDynamicState[2];
        dynamicStates[0] = VkDynamicState.Viewport;
        dynamicStates[1] = VkDynamicState.Scissor;
        VkPipelineDynamicStateCreateInfo dynamicState = new()
        {
            dynamicStateCount = 2,
            pDynamicStates = dynamicStates
        };

        VkGraphicsPipelineCreateInfo pipelineCI = new()
        {
            stageCount = 2,
            pStages = stages,
            pVertexInputState = &vertexInput,
            pInputAssemblyState = &inputAssembly,
            pViewportState = &viewportState,
            pRasterizationState = &rasterizer,
            pMultisampleState = &multisample,
            pColorBlendState = &colorBlend,
            pDynamicState = &dynamicState,
            layout = _pipelineLayout,
            renderPass = _ctx.RenderPass,
            subpass = 0
        };

        api.vkCreateGraphicsPipeline(pipelineCI, out var pipeline).CheckResult();
        return pipeline;
    }

    // Loads a pre-baked SPIR-V shader (Shaders/spirv/<shaderName>.spv, embedded by the csproj) and
    // creates a VkShaderModule. GLSL is compiled to SPIR-V at build time by tools/BakeShaders, so there
    // is no runtime shaderc (SdlVulkan.Renderer 6.23 dropped the transitive dependency; baking is what
    // makes an Android build possible - shaderc ships no android RID - and trims AOT/first-frame cost).
    // Re-bake and commit the .spv on a shader edit (the TWSH0001 build warning flags a stale bake).
    private VkShaderModule LoadShaderModule(string shaderName)
    {
        var api = _ctx.DeviceApi;
        var resource = $"TianWen.UI.Shared.Shaders.{shaderName}.spv";
        using var stream = typeof(VkFitsImagePipeline).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Embedded shader '{resource}' not found -- run tools/BakeShaders and commit Shaders/spirv/*.spv.");

        var spirv = new byte[stream.Length];
        stream.ReadExactly(spirv);
        fixed (byte* pSpirv = spirv)
        {
            VkShaderModuleCreateInfo createInfo = new()
            {
                codeSize = (nuint)spirv.Length,
                pCode = (uint*)pSpirv
            };
            api.vkCreateShaderModule(&createInfo, null, out var module).CheckResult();
            return module;
        }
    }

    private void PushProjectionAndDraw(
        VkCommandBuffer cmd,
        VulkanContext ctx,
        float left, float top, float right, float bottom,
        float projW, float projH)
    {
        var api = ctx.DeviceApi;

        // Build orthographic projection matrix (column-major, Y-down Vulkan convention)
        // proj[col][row] layout used by vkCmdPushConstants (row-major float[16])
        var proj = stackalloc float[16];
        proj[0]  = 2f / projW;
        proj[1]  = 0f;
        proj[2]  = 0f;
        proj[3]  = 0f;
        proj[4]  = 0f;
        proj[5]  = 2f / projH;
        proj[6]  = 0f;
        proj[7]  = 0f;
        proj[8]  = 0f;
        proj[9]  = 0f;
        proj[10] = -1f;
        proj[11] = 0f;
        proj[12] = -1f;
        proj[13] = -1f;
        proj[14] = 0f;
        proj[15] = 1f;

        api.vkCmdPushConstants(cmd, _pipelineLayout,
            VkShaderStageFlags.Vertex, 0, 64, proj);

        // Quad vertices: 2 triangles, 6 vertices, each with vec2 pos + vec2 uv
        ReadOnlySpan<float> vertices =
        [
            left,  top,    0f, 0f,
            right, top,    1f, 0f,
            right, bottom, 1f, 1f,
            left,  top,    0f, 0f,
            right, bottom, 1f, 1f,
            left,  bottom, 0f, 1f
        ];

        var offset = ctx.WriteVertices(vertices);
        var vb = ctx.VertexBuffer;
        var vkOffset = (ulong)offset;
        api.vkCmdBindVertexBuffers(cmd, 0, 1, &vb, &vkOffset);
        api.vkCmdDraw(cmd, 6, 1, 0, 0);
    }

    // ------------------------------------------------------------------ Byte-level UBO write helpers

    private static void WriteInt(byte* base_, int offset, int value)
    {
        *(int*)(base_ + offset) = value;
    }

    private static void WriteFloat(byte* base_, int offset, float value)
    {
        *(float*)(base_ + offset) = value;
    }
}
