using System;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.Tests;

/// <summary>
/// The viewer's chrome with every GPU-drawn layer (the image quad, the histogram quad, the overlays) a no-op, drawn on
/// any CPU <see cref="Renderer{TSurface}"/>: the one viewer the layout sweeps share (<c>LayoutRectSweep</c>,
/// <c>WireframeSweepTests</c>).
/// </summary>
internal sealed class SweepViewer : ImageRendererBase<RgbaImage>
{
    private HistogramDisplay? _histogram;

    public SweepViewer(Renderer<RgbaImage> renderer, float dpiScale) : base(renderer)
    {
        Bus = new SignalBus();
        Width = renderer.Width;
        Height = renderer.Height;
        DpiScale = dpiScale;
        FontPath = AppFace.Text;
    }

    protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
        in DisplayRendition rendition, WCS? wcs,
        float left, float top, float right, float bottom, uint projW, uint projH,
        RenditionSlot slot, bool sampleBeforeChannels) { }

    protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
        ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

    protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
        float rotationRad, RGBAColor32 color, float thickness) { }

    protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

    protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
        RGBAColor32 color, float thickness) { }

    protected override void OnResize(uint width, uint height) { }

    public override void UploadImageTexture(ReadOnlySpan<float> data, int channel, int width, int height) { }

    public override void UploadHistogramData(IPreviewSource source) => _histogram = new HistogramDisplay(source.ChannelStatistics);

    protected override HistogramDisplay? GetHistogramDisplay() => _histogram;
}
