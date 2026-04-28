using System;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry;
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
    /// <summary>
    /// Grid spacing options in arcseconds, fine to coarse. Mirrors the table in
    /// <c>ImageRendererBase</c> so the live preview's grid uses the same tick
    /// cadence as the FITS viewer.
    /// </summary>
    private static readonly double[] GridSpacingsArcsec =
    [
        1, 2, 5, 10, 15, 30,
        60, 120, 300, 600, 900, 1800,
        3600, 7200, 18000, 36000, 90000, 180000,
    ];

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

    public WCS? Wcs { get; set; }

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

        // Compute the effective draw rect first so the grid spacing heuristic
        // can use the visible image-pixel range (= rect / zoom). Pulled out of the
        // per-mode branches below.
        float drawX, drawY, drawW, drawH;
        if (State.ZoomToFit)
        {
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
            drawW = _uploadedImageWidth * State.Zoom;
            drawH = _uploadedImageHeight * State.Zoom;
            drawX = rect.X + (rect.Width - drawW) / 2 + State.PanOffset.X;
            drawY = rect.Y + (rect.Height - drawH) / 2 + State.PanOffset.Y;
        }

        // WCS grid uniforms -- mirrors VkImageRenderer.RenderImageQuad's grid path.
        // Picks a tick spacing that keeps ~3-8 lines on screen and converts the
        // CD matrix into the radian-scaled form the shader expects.
        // NB: PolarAlignSparseGrid was originally going to override the
        // shader's RA spacing here so polar mode showed only a sparse grid,
        // but the polar-overlay's own ring/cross primitives don't render
        // until the live-solve WCS gets bound to viewer.Wcs in
        // LiveSessionTab (which currently doesn't happen during refine).
        // Until that wiring lands, keep the shader grid as the user's only
        // visible reference and leave the flag dormant.
        bool gridEnabled = State.ShowGrid && Wcs is not null;
        float gridSpacingRA = 0f, gridSpacingDec = 0f, gridLineWidth = 0f;
        float crPix1 = 0f, crPix2 = 0f, crValRA = 0f, crValDec = 0f;
        Span<float> cdMatrix = stackalloc float[4];

        if (gridEnabled && Wcs is { } gw)
        {
            var pixelScaleArcsec = gw.PixelScaleArcsec;
            var effectiveZoom = drawW / _uploadedImageWidth;
            var viewImagePixels = MathF.Min(rect.Width, rect.Height) / Math.Max(effectiveZoom, 0.0001f);
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
            // RA spacing is in radians of RA. Near the poles, an arc of S arcsec on
            // the sky spans ~S/cos(dec) of RA, so without this 1/cos(dec) scaling
            // we render meridians ~100x too dense at e.g. dec = -89.4 deg and the
            // image becomes a green moire pattern. Floor at cos = 0.05 (= dec ~87
            // deg) so we don't blow up exactly at the pole.
            var cosCenterDec = Math.Max(Math.Cos(gw.CenterDec * Math.PI / 180.0), 0.05);
            // Polar-align mode: lock RA step to 12h (= pi rad) regardless of
            // FOV / cos(dec) compensation. With cos(dec)~0 near the pole the
            // autoscaled meridians otherwise pile into a green moire that
            // hides the overlay rings; pi rad gives 2 meridians (0h / 12h)
            // forming a single diameter line, providing rotation-orientation
            // context without overwhelming the alignment cues. Dec auto-scale
            // is preserved -- those concentric circles are useful here.
            gridSpacingRA = State.PolarAlignSparseGrid
                ? (float)Math.PI
                : spacingRArad / (float)cosCenterDec;
            gridSpacingDec = spacingRad;
            gridLineWidth = (float)(1.5 * pixelScaleArcsec / Math.Max(effectiveZoom, 0.0001f) / 3600.0 * (Math.PI / 180.0));

            crPix1 = (float)gw.CRPix1;
            crPix2 = (float)gw.CRPix2;
            crValRA = (float)(gw.CenterRA * (Math.PI / 12.0));
            crValDec = (float)(gw.CenterDec * (Math.PI / 180.0));

            // Decompose the CD matrix into a clean (rotation, isotropic scale)
            // form before handing to the shader. Plate solvers fit a free 2x2
            // affine, so the raw CD almost always carries a few percent of
            // anisotropic scale + skew baked in from least-squares noise.
            // Per-pixel inverse projection through that skewed CD renders
            // constant-Dec lines as visible ellipses (not circles) and the
            // grid wobbles solve-to-solve as the noise components fluctuate.
            // Polar-rotation factor M = U V^T from the SVD UΣV^T is the
            // closest pure rotation to CD; geometric-mean scale = sqrt|det CD|
            // (= PixelScaleArcsec). Recombining gives an orthogonal+isotropic
            // CD' that produces concentric grid circles and stable rendering.
            var degToRad = (float)(Math.PI / 180.0);
            var pxScaleDeg = pixelScaleArcsec / 3600.0;
            var (cd11Clean, cd12Clean, cd21Clean, cd22Clean) =
                DecomposeToCleanCD(gw.CD1_1, gw.CD1_2, gw.CD2_1, gw.CD2_2, pxScaleDeg);
            cdMatrix[0] = (float)cd11Clean * degToRad;
            cdMatrix[1] = (float)cd21Clean * degToRad;
            cdMatrix[2] = (float)cd12Clean * degToRad;
            cdMatrix[3] = (float)cd22Clean * degToRad;
        }

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
            gridEnabled: gridEnabled,
            gridSpacingRA: gridSpacingRA,
            gridSpacingDec: gridSpacingDec,
            gridLineWidth: gridLineWidth,
            imageW: _uploadedImageWidth,
            imageH: _uploadedImageHeight,
            crPix1: crPix1,
            crPix2: crPix2,
            crValRA: crValRA,
            crValDec: crValDec,
            cdMatrix: cdMatrix,
            imageSource: _imageSource,
            bayerOffsetX: _bayerOffsetX,
            bayerOffsetY: _bayerOffsetY);

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

    /// <summary>
    /// Strip skew + anisotropic scale from a plate-solved CD matrix and return
    /// the closest pure-rotation x isotropic-scale matrix. Uses the polar
    /// decomposition: any 2x2 M can be written as Q*S where Q is orthogonal
    /// and S is symmetric positive-definite. We discard S (the scale/skew
    /// part) and replace it with isotropic scale = sqrt|det M|, so the result
    /// is geomScale * Q -- ortho rotation at the same overall pixel scale as
    /// the original. The orthogonal factor of the polar decomposition for a
    /// 2x2 matrix [a b; c d] is found via the sum a+d (for sign-correct
    /// rotation) and angle atan2(c-b, a+d).
    /// </summary>
    internal static (double CD11, double CD12, double CD21, double CD22)
        DecomposeToCleanCD(double cd11, double cd12, double cd21, double cd22, double geomScaleDeg)
    {
        var det = cd11 * cd22 - cd12 * cd21;
        var sign = det >= 0 ? 1.0 : -1.0;
        // Effective rotation angle (atan2 form is robust through the wraps).
        // For determinant > 0 the orthogonal factor is a pure rotation.
        // For determinant < 0 the image is mirrored; we apply the sign to CD22
        // so the reconstructed matrix matches the original orientation/parity.
        var theta = Math.Atan2(cd21 - sign * cd12, cd11 + sign * cd22);
        var c = Math.Cos(theta);
        var s = Math.Sin(theta);
        return (
            CD11: geomScaleDeg * c,
            CD12: -geomScaleDeg * s * sign,
            CD21: geomScaleDeg * s,
            CD22: geomScaleDeg * c * sign);
    }

    public void Dispose()
    {
        _fitsPipeline.Dispose();
    }
}
