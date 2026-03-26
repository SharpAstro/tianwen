using System;
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

    private AstroImageDocument? _document;
    private Image? _pendingImage;
    private Task<AstroImageDocument>? _pendingDoc;
    private int _uploadedImageWidth;
    private int _uploadedImageHeight;
    private int _uploadedChannelCount;

    public VkMiniViewerWidget(VkRenderer renderer)
    {
        _renderer = renderer;
        _fitsPipeline = new VkFitsImagePipeline(renderer.Surface);
    }

    public bool HasImage => _document is not null || _pendingImage is not null;

    public MiniViewerState State { get; } = new MiniViewerState();

    public void QueueImage(Image image)
    {
        _pendingImage = image;
    }

    /// <summary>Processes any queued image: computes stretch stats and uploads textures.</summary>
    private void ProcessPendingImage()
    {
        // Check if the async stats computation completed
        if (_pendingDoc is { IsCompleted: true } task)
        {
            _pendingDoc = null;
            if (task.IsCompletedSuccessfully)
            {
                var doc = task.Result;
                var img = doc.UnstretchedImage;
                for (var c = 0; c < img.ChannelCount; c++)
                {
                    _fitsPipeline.UploadChannelTexture(img.GetChannelSpan(c), c, img.Width, img.Height);
                }

                _uploadedImageWidth = img.Width;
                _uploadedImageHeight = img.Height;
                _uploadedChannelCount = img.ChannelCount;
                _document = doc;
            }
        }

        // Kick off async stats computation for newly queued image
        if (_pendingImage is { } image && _pendingDoc is null)
        {
            _pendingImage = null;
            _pendingDoc = AstroImageDocument.CreateFromImageAsync(image);

            // For small images (guide camera), the task may complete synchronously —
            // check immediately to avoid a 500ms delay before the image appears
            if (_pendingDoc is { IsCompleted: true } newTask)
            {
                _pendingDoc = null;
                if (newTask.IsCompletedSuccessfully)
                {
                    var doc = newTask.Result;
                    var img = doc.UnstretchedImage;
                    for (var c = 0; c < img.ChannelCount; c++)
                    {
                        _fitsPipeline.UploadChannelTexture(img.GetChannelSpan(c), c, img.Width, img.Height);
                    }
                    _uploadedImageWidth = img.Width;
                    _uploadedImageHeight = img.Height;
                    _uploadedChannelCount = img.ChannelCount;
                    _document = doc;
                }
            }
        }
    }

    public void Render(RectF32 rect, uint windowWidth, uint windowHeight)
    {
        ProcessPendingImage();

        if (_document is not { } doc || _uploadedImageWidth <= 0 || _uploadedImageHeight <= 0)
        {
            return;
        }

        // Compute stretch from state
        var stretch = doc.ComputeStretchUniforms(State.StretchMode, State.StretchParameters);

        // Background level for boost midpoint
        var bgLevel = doc.PerChannelBackground.Length > 0 ? doc.PerChannelBackground[0] : 0.25f;

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
            cdMatrix: ReadOnlySpan<float>.Empty);

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
