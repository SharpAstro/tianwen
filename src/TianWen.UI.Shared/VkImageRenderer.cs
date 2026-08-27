using System;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Shared;

/// <summary>
/// Vulkan implementation of the FITS image viewer.
/// Extends <see cref="ImageRendererBase{TSurface}"/> with Vulkan-specific GPU rendering
/// via <see cref="VkFitsImagePipeline"/>.
/// </summary>
public class VkImageRenderer : ImageRendererBase<VulkanContext>, IDisposable
{
    private readonly VkRenderer _renderer;
    private readonly VkFitsImagePipeline _fitsPipeline;

    private HistogramDisplay? _histogramDisplay;
    private StretchMode? _histogramLastStretchMode;
    private float _histogramLastNormFactor;

    /// <summary>
    /// Grid spacing options in arcseconds, from fine to coarse.
    /// Must match the base class for WCS grid shader parameters.
    /// </summary>
    private static readonly double[] GridSpacingsArcsec =
    [
        1, 2, 5, 10, 15, 30,                           // sub-arcminute
        60, 120, 300, 600, 900, 1800,                   // arcminutes
        3600, 7200, 18000, 36000, 90000, 180000,        // degrees
    ];

    public VkImageRenderer(VkRenderer renderer, uint width, uint height) : base(renderer)
    {
        _renderer = renderer;
        Width = width;
        Height = height;
        _fitsPipeline = new VkFitsImagePipeline(renderer.Surface);
        ResolveFontPath();
    }

    protected override void OnResize(uint width, uint height)
    {
        _renderer.Resize(width, height);

        // The layer targets are a fixed capacity that must not be reallocated mid-frame, so a
        // resize drops them and the next frame re-ensures them at the new pane size.
        if (_renderer.CachedLayerTargetReady)
        {
            _renderer.ReleaseCachedLayerTargets();
        }
        _layerCapacityW = 0;
        _layerCapacityH = 0;
    }

    // ---- cached image layer (see ImageRendererBase.CachedLayer.cs) ----

    /// <summary>The size the targets were allocated at. The renderer allocates once and answers any
    /// smaller request out of the same texture, so the request that first succeeds IS the capacity
    /// until <see cref="OnResize"/> releases the targets; the renderer itself does not expose it.</summary>
    private int _layerCapacityW;
    private int _layerCapacityH;

    /// <inheritdoc/>
    protected override int CachedLayerSlotCount => _renderer.CachedLayerSlotCount;

    /// <inheritdoc/>
    protected override int CachedLayerSlotIndex => _renderer.CachedLayerSlot;

    /// <inheritdoc/>
    protected override bool TryEnsureCachedLayerTargets(int width, int height, out int capacityWidth, out int capacityHeight)
    {
        var fresh = !_renderer.CachedLayerTargetReady;
        var ok = _renderer.EnsureCachedLayerTargets((uint)width, (uint)height);
        if (ok && fresh)
        {
            _layerCapacityW = width;
            _layerCapacityH = height;
        }
        capacityWidth = _layerCapacityW;
        capacityHeight = _layerCapacityH;
        return ok;
    }

    /// <inheritdoc/>
    protected override bool TryBeginCachedLayerPass(int width, int height)
        => _renderer.BeginCachedLayer((uint)width, (uint)height, new RGBAColor32(0, 0, 0, 255));

    /// <inheritdoc/>
    protected override void EndCachedLayerPass() => _renderer.EndCachedLayer();

    /// <inheritdoc/>
    protected override bool TryDrawCachedLayer(int slot, float x, float y, float w, float h,
        float u0, float v0, float u1, float v1)
    {
        if (!_renderer.IsCachedLayerSlotRendered(slot))
        {
            return false;
        }

        _renderer.DrawTextureRegion(_renderer.CachedLayerDescriptorSet(slot), x, y, w, h, u0, v0, u1, v1);
        return true;
    }

    public override void UploadImageTexture(ReadOnlySpan<float> data, int channel, int imageWidth, int imageHeight)
    {
        _fitsPipeline.UploadChannelTexture(data, channel, imageWidth, imageHeight);
    }

    /// <summary>The pipeline carries a per-channel texel format and recreates on a change, so an 8-bit
    /// channel becomes an <c>R8Unorm</c> texture at a quarter of the device memory. Overriding this at
    /// all is what declares the capability, so there is no flag to keep in step with it.</summary>
    protected override bool TryUploadImageTexture(ReadOnlySpan<byte> data, int channel, int imageWidth, int imageHeight)
    {
        _fitsPipeline.UploadChannelTexture(data, channel, imageWidth, imageHeight);
        return true;
    }

    protected override void ReleaseUnusedChannelTextures(int liveSlotCount)
    {
        _fitsPipeline.ReleaseChannelTexturesFrom(liveSlotCount);
    }

    /// <summary>Hands the pipeline's staging buffer back after a document load. See
    /// <see cref="VkFitsImagePipeline.TrimStagingBuffer"/> for why this is caller-driven.</summary>
    protected override void TrimUploadScratch()
    {
        _fitsPipeline.TrimStagingBuffer();
    }

    public override void UploadHistogramData(IPreviewSource source)
    {
        var stats = source.ChannelStatistics;
        var channels = Math.Min(stats.Length, 3);
        var rawBins = channels > 0 ? stats[0].Histogram.Length : 0;

        // Recycle to avoid per-frame GC pressure: only (re)allocate when the geometry changes (a new
        // file with different channel/bin counts). For a multi-frame sequence, refresh the existing
        // display's raw bins IN PLACE from the current frame so the histogram tracks playback while the
        // cached stretch stats stay fixed -- per-frame-accurate, zero allocation. A still image keeps
        // its frame-0 bins (re-binning the same pixels would be wasted work).
        if (_histogramDisplay is null || _histogramDisplay.ChannelCount != channels || _histogramDisplay.RawBinCount != rawBins)
        {
            _histogramDisplay = new HistogramDisplay(stats);
        }
        else if (source.FrameCount > 1)
        {
            var n = Math.Min(_histogramDisplay.ChannelCount, source.ChannelCount);
            for (var c = 0; c < n; c++)
            {
                _histogramDisplay.UpdateRawBins(c, source.GetChannelData(c));
            }
        }

        _histogramLastStretchMode = null; // force re-upload on next render
    }

    protected override HistogramDisplay? GetHistogramDisplay() => _histogramDisplay;

    /// <summary>
    /// Recomputes display bins via <see cref="HistogramDisplay"/> and uploads to Vulkan textures.
    /// </summary>
    private void UpdateHistogramTextures(StretchUniforms stretch)
    {
        if (_histogramDisplay is null) return;

        _histogramLastStretchMode = stretch.Mode;
        _histogramLastNormFactor = stretch.NormFactor;

        _histogramDisplay.Recompute(
            stretch.Mode, stretch.NormFactor,
            stretch.Pedestal, stretch.Shadows, stretch.Midtones, stretch.Rescale);

        for (var c = 0; c < _histogramDisplay.ChannelCount; c++)
        {
            _fitsPipeline.UploadHistogramTexture(_histogramDisplay.GetDisplayBins(c), c);
        }
    }

    protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
        in DisplayRendition rendition, WCS? gridWcs,
        float left, float top, float right, float bottom, uint projW, uint projH,
        RenditionSlot slot, bool sampleBeforeChannels)
    {
        WriteImageUniforms(source, state, rendition, gridWcs, slot);

        _fitsPipeline.RecordImageDraw(
            _renderer.CurrentCommandBuffer,
            _renderer.Surface,
            left: left,
            top: top,
            right: right,
            bottom: bottom,
            projW: projW,
            projH: projH,
            uboSlot: (int)slot,
            sampleBeforeChannels: sampleBeforeChannels);
    }

    /// <summary>
    /// The uniform half of <see cref="RenderImageQuad"/> on its own, so the cached-layer decision
    /// can ask whether the shader input moved BEFORE committing to a draw. Writing the UBO is a
    /// memcpy into mapped memory; it records no GPU work, so an extra write costs nothing.
    /// </summary>
    protected override bool TryWriteImageUniforms(IPreviewSource? source, ViewerState state,
        in DisplayRendition rendition, WCS? gridWcs, RenditionSlot slot)
    {
        WriteImageUniforms(source, state, rendition, gridWcs, slot);
        return true;
    }

    /// <inheritdoc/>
    protected override bool ImageShaderInputChanged(RenditionSlot slot)
        => _fitsPipeline.StretchUboChanged((int)slot);

    private void WriteImageUniforms(IPreviewSource? source, ViewerState state,
        in DisplayRendition rendition, WCS? gridWcs, RenditionSlot slot)
    {
        // Everything below reads the RENDITION, never state.Curves*/Hdr* directly: the split's
        // comparison half is a pinned snapshot of those dials, so reading state here would leak the
        // live values into the pinned half and the two would differ only in stretch.
        var stretch = rendition.Stretch;
        var bgLevel = source is not null
            ? stretch.ComputePostStretchBackground(source.PerChannelBackground, source.LumaBackground)
            : 0.15f;

        // WCS grid parameters
        bool gridEnabled = gridWcs is not null;
        float gridSpacingRA = 0f, gridSpacingDec = 0f, gridLineWidth = 0f;
        float crPix1 = 0f, crPix2 = 0f, crValRA = 0f, crValDec = 0f;
        Span<float> cdMatrix = stackalloc float[4];

        if (gridWcs is { } gw)
        {
            var pixelScaleArcsec = gw.PixelScaleArcsec;

            // The pane comes from the ONE arranged layout. This used to re-derive it as projW/projH
            // minus the chrome widths, which duplicated what ComputeLayout already knows and was wrong
            // three ways: it subtracts a toolbar and status bar even under HideChrome, where neither is
            // drawn (every embedded preview in the GUI), it ignores the histogram and SER transport
            // strips that also shrink the pane, and it quietly tied grid spacing to the PROJECTION dims
            // -- so drawing this same quad into a differently sized target would change the spacing for
            // no reason the caller could see.
            var pane = ImageAreaRect;
            var viewImagePixels = MathF.Min(pane.Width, pane.Height) / state.Zoom;
            var viewArcsec = viewImagePixels * pixelScaleArcsec;
            var spacingArcsec = GridSpacingsArcsec[^1];
            foreach (var candidate in GridSpacingsArcsec)
            {
                if (candidate >= viewArcsec / 8.0)
                {
                    spacingArcsec = candidate;
                    break;
                }
            }

            var spacingRad = (float)(spacingArcsec / 3600.0 * (Math.PI / 180.0));
            var spacingRArad = (float)(spacingArcsec / 3600.0 / 15.0 * (Math.PI / 12.0));
            gridSpacingRA = spacingRArad;
            gridSpacingDec = spacingRad;
            gridLineWidth = (float)(1.5 * pixelScaleArcsec / state.Zoom / 3600.0 * (Math.PI / 180.0));

            crPix1 = (float)gw.CRPix1;
            crPix2 = (float)gw.CRPix2;
            crValRA = (float)(gw.CenterRA * (Math.PI / 12.0));
            crValDec = (float)(gw.CenterDec * (Math.PI / 180.0));

            var degToRad = (float)(Math.PI / 180.0);
            cdMatrix[0] = (float)gw.CD1_1 * degToRad;
            cdMatrix[1] = (float)gw.CD2_1 * degToRad;
            cdMatrix[2] = (float)gw.CD1_2 * degToRad;
            cdMatrix[3] = (float)gw.CD2_2 * degToRad;
        }

        var cmd = _renderer.CurrentCommandBuffer;

        _fitsPipeline.UpdateStretchUBO(
            cmd,
            channelCount: ChannelTextureCount,
            stretchMode: (int)stretch.Mode,
            normFactor: stretch.NormFactor,
            curvesBoost: rendition.CurvesBoost,
            curvesMidpoint: bgLevel,
            hdrAmount: rendition.HdrAmount,
            hdrKnee: rendition.HdrKnee,
            pedestal: (stretch.Pedestal.R, stretch.Pedestal.G, stretch.Pedestal.B),
            shadows: (stretch.Shadows.R, stretch.Shadows.G, stretch.Shadows.B),
            midtones: (stretch.Midtones.R, stretch.Midtones.G, stretch.Midtones.B),
            highlights: (stretch.Highlights.R, stretch.Highlights.G, stretch.Highlights.B),
            rescale: (stretch.Rescale.R, stretch.Rescale.G, stretch.Rescale.B),
            whiteBalance: (stretch.WhiteBalance.R, stretch.WhiteBalance.G, stretch.WhiteBalance.B),
            bgNeutralization: (stretch.BackgroundNeutralization.R, stretch.BackgroundNeutralization.G, stretch.BackgroundNeutralization.B),
            curvesMode: rendition.CurvesMode,
            curveData: rendition.CurveSpan,
            gridEnabled: gridEnabled,
            gridSpacingRA: gridSpacingRA,
            gridSpacingDec: gridSpacingDec,
            gridLineWidth: gridLineWidth,
            imageW: ImageWidth,
            imageH: ImageHeight,
            crPix1: crPix1,
            crPix2: crPix2,
            crValRA: crValRA,
            crValDec: crValDec,
            cdMatrix: cdMatrix,
            imageSource: (VkFitsImagePipeline.ImageSource)ImageSourceMode,
            bayerOffsetX: BayerOffsetX,
            bayerOffsetY: BayerOffsetY,
            lumaWeights: (stretch.LumaWeights.R, stretch.LumaWeights.G, stretch.LumaWeights.B),
            lumaStretch: (stretch.LumaStretch.Shadow, stretch.LumaStretch.Midtones, stretch.LumaStretch.Rescale),
            lumaBlend: stretch.LumaBlend,
            normalizeScale: stretch.NormalizeScale,
            debayerMode: RawBayerDebayerMode,
            slot: (int)slot);
    }

    /// <inheritdoc/>
    public override bool HasBeforeImageTextures => _fitsPipeline.HasBeforeChannels;

    /// <inheritdoc/>
    public override long BeforeImageTextureBytes => _fitsPipeline.BeforeChannelBytes;

    /// <inheritdoc/>
    public override bool TryRetainImageTexturesAsBefore() => _fitsPipeline.TryRetainChannelsAsBefore();

    /// <inheritdoc/>
    public override void ReleaseBeforeImageTextures() => _fitsPipeline.ReleaseBeforeChannels();

    protected override void RenderHistogramQuad(StretchUniforms stretch,
        HistogramDisplay histogram, ViewerState state,
        float left, float top, float right, float bottom, uint projW, uint projH)
    {
        // Recompute histogram textures when stretch mode changes
        if (stretch.Mode != _histogramLastStretchMode || stretch.NormFactor != _histogramLastNormFactor)
        {
            UpdateHistogramTextures(stretch);
        }

        var cmd = _renderer.CurrentCommandBuffer;

        _fitsPipeline.UpdateHistogramUBO(
            cmd,
            channelCount: histogram.ChannelCount,
            logPeak: histogram.LogPeak,
            linearPeak: histogram.LinearPeak,
            logScale: state.HistogramLogScale);

        _fitsPipeline.RecordHistogramDraw(
            cmd,
            _renderer.Surface,
            left: left,
            top: top,
            right: right,
            bottom: bottom,
            projW: projW,
            projH: projH);
    }

    protected override void DrawEllipseOverlay(float cx, float cy,
        float semiMajor, float semiMinor, float angleRad, RGBAColor32 color, float thickness)
        => VkOverlayShapes.DrawEllipse(_renderer, DpiScale, cx, cy, semiMajor, semiMinor, angleRad, color, thickness);

    protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color)
        => VkOverlayShapes.DrawCross(_renderer, DpiScale, cx, cy, armLength, color);

    protected override void DrawLineOverlay(float x0, float y0, float x1, float y1, RGBAColor32 color, float thickness)
        => _renderer.DrawLine(x0, y0, x1, y1, color, Math.Max(1, (int)(thickness * DpiScale)));

    public void Dispose()
    {
        _fitsPipeline.Dispose();
    }
}
