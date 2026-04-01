using System;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;
using Vortice.Vulkan;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-backed mini viewer widget for the live session tab.
/// Renders the last captured frame with auto-stretch into an arbitrary rect.
/// </summary>
public sealed unsafe class VkMiniViewerWidget : IMiniViewerWidget, IDisposable
{
    private readonly VkRenderer _renderer;
    private readonly VkFitsImagePipeline _fitsPipeline;

    private volatile Image? _pendingImage;
    private Image? _currentImage;
    private int _uploadedImageWidth;
    private int _uploadedImageHeight;
    private int _uploadedChannelCount;
    private VkFitsImagePipeline.ImageSource _imageSource;
    private int _bayerOffsetX;
    private int _bayerOffsetY;

    // Cached stretch stats — recomputed only when image dimensions change
    private ChannelStretchStats[]? _cachedStretchStats;

    public VkMiniViewerWidget(VkRenderer renderer)
    {
        _renderer = renderer;
        _fitsPipeline = new VkFitsImagePipeline(renderer.Surface);
    }

    public bool HasImage => _currentImage is not null || _pendingImage is not null;

    public MiniViewerState State { get; } = new MiniViewerState();

    public void QueueImage(Image image)
    {
        _pendingImage = image;
    }

    /// <summary>
    /// Uploads raw image data to GPU. For Bayer images, uploads the single-channel mosaic
    /// and lets the fragment shader do bilinear debayer + normalization + stretch.
    /// For mono images, uploads single channel. For pre-debayered RGB, uploads all channels.
    /// </summary>
    private void ProcessPendingImage()
    {
        if (_pendingImage is not { } image)
        {
            return;
        }
        _pendingImage = null;

        var sensorType = image.ImageMeta.SensorType;

        if (sensorType is TianWen.Lib.Imaging.SensorType.Monochrome)
        {
            // Mono: upload single raw channel, GPU normalizes via normFactor
            _fitsPipeline.UploadChannelTexture(image.GetChannelSpan(0), 0, image.Width, image.Height);
            _uploadedChannelCount = 1;
            _imageSource = VkFitsImagePipeline.ImageSource.RawMono;
            _bayerOffsetX = 0;
            _bayerOffsetY = 0;
        }
        else if (sensorType is TianWen.Lib.Imaging.SensorType.RGGB && image.ChannelCount == 1)
        {
            // Raw Bayer mosaic: upload single channel, shader debayers
            _fitsPipeline.UploadChannelTexture(image.GetChannelSpan(0), 0, image.Width, image.Height);
            _uploadedChannelCount = 3; // shader produces RGB
            _imageSource = VkFitsImagePipeline.ImageSource.RawBayer;
            _bayerOffsetX = image.ImageMeta.BayerOffsetX;
            _bayerOffsetY = image.ImageMeta.BayerOffsetY;
        }
        else
        {
            // Pre-debayered RGB or multi-channel: upload all channels
            var channelCount = image.ChannelCount;
            for (var c = 0; c < channelCount; c++)
            {
                _fitsPipeline.UploadChannelTexture(image.GetChannelSpan(c), c, image.Width, image.Height);
            }
            _uploadedChannelCount = channelCount;
            _imageSource = VkFitsImagePipeline.ImageSource.ProcessedChannels;
            _bayerOffsetX = 0;
            _bayerOffsetY = 0;
        }

        _uploadedImageWidth = image.Width;
        _uploadedImageHeight = image.Height;

        // Compute stretch stats from channel 0 (works for raw and debayered)
        if (_cachedStretchStats is null || _cachedStretchStats.Length != _uploadedChannelCount)
        {
            _cachedStretchStats = new ChannelStretchStats[_uploadedChannelCount];
        }
        var (ped, med, mad) = image.GetPedestralMedianAndMADScaledToUnit(0);
        for (var c = 0; c < _cachedStretchStats.Length; c++)
        {
            _cachedStretchStats[c] = new ChannelStretchStats(ped, med, mad);
        }

        _currentImage = image;
    }

    public void Render(RectF32 rect, uint windowWidth, uint windowHeight)
    {
        ProcessPendingImage();

        if (_cachedStretchStats is not { } stretchStats || _uploadedImageWidth <= 0 || _uploadedImageHeight <= 0)
        {
            return;
        }

        // Compute stretch from cached stats — no AstroImageDocument needed
        var maxValue = _currentImage?.MaxValue ?? 1f;
        var stretch = AstroImageDocument.ComputeStretchUniforms(State.StretchMode, State.StretchParameters, stretchStats, null, maxValue);

        // Use pedestal as background estimate for boost midpoint
        var bgLevel = stretchStats.Length > 0 ? stretchStats[0].Pedestal : 0.25f;

        var cmd = _renderer.CurrentCommandBuffer;

        _fitsPipeline.UpdateStretchUBO(
            cmd,
            channelCount: _uploadedChannelCount,
            stretchMode: (int)stretch.Mode,
            normFactor: stretch.NormFactor,
            curvesBoost: State.CurvesBoost,
            curvesMidpoint: bgLevel,
            hdrAmount: 0f,
            hdrKnee: 0.5f,
            pedestal: (stretch.Pedestal.R, stretch.Pedestal.G, stretch.Pedestal.B),
            shadows: (stretch.Shadows.R, stretch.Shadows.G, stretch.Shadows.B),
            midtones: (stretch.Midtones.R, stretch.Midtones.G, stretch.Midtones.B),
            highlights: (stretch.Highlights.R, stretch.Highlights.G, stretch.Highlights.B),
            rescale: (stretch.Rescale.R, stretch.Rescale.G, stretch.Rescale.B),
            gridEnabled: false,
            gridSpacingRA: 0f,
            gridSpacingDec: 0f,
            gridLineWidth: 0f,
            imageW: _uploadedImageWidth,
            imageH: _uploadedImageHeight,
            crPix1: 0f,
            crPix2: 0f,
            crValRA: 0f,
            crValDec: 0f,
            cdMatrix: ReadOnlySpan<float>.Empty,
            imageSource: _imageSource,
            bayerOffsetX: _bayerOffsetX,
            bayerOffsetY: _bayerOffsetY);

        // Compute draw rect based on zoom mode
        float drawX, drawY, drawW, drawH;

        if (State.ZoomToFit)
        {
            // Fit to rect preserving aspect ratio
            var imgAspect = (float)_uploadedImageWidth / _uploadedImageHeight;
            var rectAspect = rect.Width / rect.Height;

            if (imgAspect > rectAspect)
            {
                drawW = rect.Width;
                drawH = rect.Width / imgAspect;
            }
            else
            {
                drawH = rect.Height;
                drawW = rect.Height * imgAspect;
            }

            drawX = rect.X + (rect.Width - drawW) / 2;
            drawY = rect.Y + (rect.Height - drawH) / 2;
        }
        else
        {
            // 1:1 or custom zoom — image pixels × zoom, centered + pan
            drawW = _uploadedImageWidth * State.Zoom;
            drawH = _uploadedImageHeight * State.Zoom;
            drawX = rect.X + (rect.Width - drawW) / 2 + State.PanOffset.X;
            drawY = rect.Y + (rect.Height - drawH) / 2 + State.PanOffset.Y;
        }

        // Set scissor to clip image to the viewport rect
        var api = _renderer.Surface.DeviceApi;
        var scissor = new VkRect2D
        {
            offset = new VkOffset2D { x = (int)rect.X, y = (int)rect.Y },
            extent = new VkExtent2D { width = (uint)rect.Width, height = (uint)rect.Height }
        };
        api.vkCmdSetScissor(cmd, 0, 1, &scissor);

        _fitsPipeline.RecordImageDraw(
            cmd,
            _renderer.Surface,
            left: drawX,
            top: drawY,
            right: drawX + drawW,
            bottom: drawY + drawH,
            projW: windowWidth,
            projH: windowHeight);

        // Restore full-window scissor
        var fullScissor = new VkRect2D
        {
            offset = default,
            extent = new VkExtent2D { width = windowWidth, height = windowHeight }
        };
        api.vkCmdSetScissor(cmd, 0, 1, &fullScissor);
    }

    public void Dispose()
    {
        _fitsPipeline.Dispose();
    }
}
