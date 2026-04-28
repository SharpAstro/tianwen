using System;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned Viewer tab for the GUI. Reuses <see cref="ImageRendererBase{TSurface}"/>
/// with a <see cref="VkFitsImagePipeline"/> for GPU rendering.
/// </summary>
public sealed class VkViewerTab : ImageRendererBase<VulkanContext>, IDisposable
{
    private readonly VkRenderer _renderer;
    private readonly VkFitsImagePipeline _fitsPipeline;

    private HistogramDisplay? _histogramDisplay;
    private StretchMode? _histogramLastStretchMode;
    private float _histogramLastNormFactor;

    /// <summary>
    /// Grid spacing options in arcseconds, from fine to coarse.
    /// </summary>
    private static readonly double[] GridSpacingsArcsec =
    [
        1, 2, 5, 10, 15, 30,
        60, 120, 300, 600, 900, 1800,
        3600, 7200, 18000, 36000, 90000, 180000,
    ];

    public VkViewerTab(VkRenderer renderer, uint width, uint height) : base(renderer)
    {
        _renderer = renderer;
        Width = width;
        Height = height;
        _fitsPipeline = new VkFitsImagePipeline(renderer.Surface);
        ResolveFontPath();
    }

    protected override void OnResize(uint width, uint height)
    {
        // VkRenderer is resized by the main VkGuiRenderer; we only update our dimensions
    }

    public override void UploadImageTexture(ReadOnlySpan<float> data, int channel, int imageWidth, int imageHeight)
    {
        _fitsPipeline.UploadChannelTexture(data, channel, imageWidth, imageHeight);
    }

    public override void UploadHistogramData(AstroImageDocument document)
    {
        _histogramDisplay = new HistogramDisplay(document.ChannelStatistics);
        _histogramLastStretchMode = null;
    }

    protected override HistogramDisplay? GetHistogramDisplay() => _histogramDisplay;

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

    protected override void RenderImageQuad(AstroImageDocument? doc, ViewerState state,
        StretchUniforms stretch, WCS? gridWcs,
        float left, float top, float right, float bottom, uint projW, uint projH)
    {
        var bgLevel = doc is not null
            ? stretch.ComputePostStretchBackground(doc.PerChannelBackground, doc.LumaBackground)
            : 0.15f;

        bool gridEnabled = gridWcs is not null;
        float gridSpacingRA = 0f, gridSpacingDec = 0f, gridLineWidth = 0f;
        float crPix1 = 0f, crPix2 = 0f, crValRA = 0f, crValDec = 0f;
        Span<float> cdMatrix = stackalloc float[4];

        if (gridWcs is { } gw)
        {
            var pixelScaleArcsec = gw.PixelScaleArcsec;
            var fileListW = state.ShowFileList ? ScaledFileListWidth : 0;
            var panelW = state.ShowInfoPanel ? ScaledInfoPanelWidth : 0;
            var areaW = (float)(projW - fileListW - panelW);
            var areaH = (float)(projH - ScaledToolbarHeight - ScaledStatusBarHeight);
            var viewImagePixels = MathF.Min(areaW, areaH) / state.Zoom;
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
            curvesBoost: state.CurvesBoost,
            curvesMidpoint: bgLevel,
            hdrAmount: state.HdrAmount,
            hdrKnee: state.HdrKnee,
            pedestal: (stretch.Pedestal.R, stretch.Pedestal.G, stretch.Pedestal.B),
            shadows: (stretch.Shadows.R, stretch.Shadows.G, stretch.Shadows.B),
            midtones: (stretch.Midtones.R, stretch.Midtones.G, stretch.Midtones.B),
            highlights: (stretch.Highlights.R, stretch.Highlights.G, stretch.Highlights.B),
            rescale: (stretch.Rescale.R, stretch.Rescale.G, stretch.Rescale.B),
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
            bayerOffsetY: BayerOffsetY);

        _fitsPipeline.RecordImageDraw(
            cmd,
            _renderer.Surface,
            left: left,
            top: top,
            right: right,
            bottom: bottom,
            projW: projW,
            projH: projH);
    }

    protected override void RenderHistogramQuad(StretchUniforms stretch,
        HistogramDisplay histogram, ViewerState state,
        float left, float top, float right, float bottom, uint projW, uint projH)
    {
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
    {
        var cosA = MathF.Cos(angleRad);
        var sinA = MathF.Sin(angleRad);
        var bboxW = MathF.Sqrt(semiMajor * semiMajor * cosA * cosA + semiMinor * semiMinor * sinA * sinA);
        var bboxH = MathF.Sqrt(semiMajor * semiMajor * sinA * sinA + semiMinor * semiMinor * cosA * cosA);

        var rect = new RectInt(
            new PointInt((int)(cx + bboxW), (int)(cy + bboxH)),
            new PointInt((int)(cx - bboxW), (int)(cy - bboxH)));

        if (thickness > 0f)
        {
            _renderer.DrawEllipseOutline(rect, color, thickness * DpiScale);
        }
        else
        {
            _renderer.FillEllipse(rect, color);
        }
    }

    protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color)
    {
        var thickness = Math.Max(1, (int)DpiScale);

        var hRect = new RectInt(
            new PointInt((int)(cx + armLength), (int)(cy + thickness)),
            new PointInt((int)(cx - armLength), (int)(cy - thickness)));
        _renderer.FillRectangle(hRect, color);

        var vRect = new RectInt(
            new PointInt((int)(cx + thickness), (int)(cy + armLength)),
            new PointInt((int)(cx - thickness), (int)(cy - armLength)));
        _renderer.FillRectangle(vRect, color);
    }

    protected override void DrawLineOverlay(float x0, float y0, float x1, float y1, RGBAColor32 color, float thickness)
        => _renderer.DrawLine(x0, y0, x1, y1, color, Math.Max(1, (int)(thickness * DpiScale)));

    public void Dispose()
    {
        _fitsPipeline.Dispose();
    }
}
