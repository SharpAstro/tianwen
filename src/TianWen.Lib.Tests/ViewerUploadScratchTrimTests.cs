using System;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The host-visible staging buffer the texture uploads go through is grow-only and used to be freed
/// only on dispose, so one channel of a large document pinned that much memory for the process
/// lifetime -- 472 MiB for the 13228x9354 case measured in
/// <c>docs/plans/viewer-memory-footprint.md</c>, still resident while every subsequent small FITS was
/// viewed. M1 of that plan releases it after a document load.
///
/// <para><b>What these tests are really guarding is the GATE, not the release.</b> Trimming is one
/// line; trimming at the wrong moment is a performance regression on the imaging hot path, because
/// this same upload method runs PER FRAME for a live camera feed and a colour sensor's mosaic is one
/// large channel (~104 MB on an ASI2600). Freeing there would turn a stable allocation into an
/// alloc/free every frame. So the interesting assertions below are the ones that require NO trim.</para>
///
/// <para>A <c>VkBuffer</c> is invisible to the GC, which is why the cost went unnoticed for so long
/// and why the release itself is asserted on the Vulkan pipeline (see
/// <c>GpuStretchPipelineTests</c>) rather than by watching managed memory.</para>
/// </summary>
public class ViewerUploadScratchTrimTests
{
    [Fact]
    public void ADocumentLoadTrimsTheUploadScratchOnce()
    {
        using var renderer = new RgbaImageRenderer(800, 600);
        var viewer = new TrimCountingViewer(renderer);
        var state = new ViewerState();

        // What ViewerController does on every source replacement, and the only thing that marks an
        // upload as belonging to a NEW document.
        state.NotifySourceReplaced();
        viewer.UploadDocumentTextures(new StubSource(), state);

        viewer.TrimCalls.ShouldBe(1);
    }

    /// <summary>
    /// The regression this gate exists for. A live camera feed re-uploads through the same method every
    /// frame against ONE source, so its generation never moves and the scratch must be retained. Ten
    /// uploads stand in for ten frames; the count that matters is zero.
    /// </summary>
    [Fact]
    public void RepeatedLiveFrameUploadsNeverTrim()
    {
        using var renderer = new RgbaImageRenderer(800, 600);
        var viewer = new TrimCountingViewer(renderer);
        var state = new ViewerState();
        var source = new StubSource();

        for (var frame = 0; frame < 10; frame++)
        {
            state.NeedsTextureUpdate = true;
            viewer.UploadDocumentTextures(source, state);
        }

        viewer.TrimCalls.ShouldBe(0);
    }

    /// <summary>
    /// Re-uploads of the SAME document -- a channel switch, a debayer change, a stretch that needs new
    /// textures -- are not a new burst either. Only the load is.
    /// </summary>
    [Fact]
    public void ReUploadingTheSameDocumentTrimsOnlyOnTheLoad()
    {
        using var renderer = new RgbaImageRenderer(800, 600);
        var viewer = new TrimCountingViewer(renderer);
        var state = new ViewerState();
        var source = new StubSource();

        state.NotifySourceReplaced();
        viewer.UploadDocumentTextures(source, state);
        viewer.UploadDocumentTextures(source, state);
        viewer.UploadDocumentTextures(source, state);

        viewer.TrimCalls.ShouldBe(1);
    }

    /// <summary>Opening a second file is a second burst, so it trims again -- otherwise the buffer would
    /// only ever be released once per process and a later, larger document would re-establish the
    /// high-water mark permanently.</summary>
    [Fact]
    public void EachDocumentLoadTrimsAgain()
    {
        using var renderer = new RgbaImageRenderer(800, 600);
        var viewer = new TrimCountingViewer(renderer);
        var state = new ViewerState();
        var source = new StubSource();

        for (var document = 0; document < 3; document++)
        {
            state.NotifySourceReplaced();
            viewer.UploadDocumentTextures(source, state);
        }

        viewer.TrimCalls.ShouldBe(3);
    }

    private sealed class TrimCountingViewer : ImageRendererBase<RgbaImage>
    {
        public TrimCountingViewer(RgbaImageRenderer renderer) : base(renderer)
        {
            Width = renderer.Width;
            Height = renderer.Height;
        }

        public int TrimCalls { get; private set; }

        protected override void TrimUploadScratch() => TrimCalls++;

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

        public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
            int width, int height) { }

        public override void UploadHistogramData(IPreviewSource source) { }

        protected override HistogramDisplay? GetHistogramDisplay() => null;
    }

    /// <summary>Geometry and channel count are all <c>UploadDocumentTextures</c> needs to pick its
    /// path; nothing here reads pixels.</summary>
    private sealed class StubSource : IPreviewSource
    {
        public int Width => 400;
        public int Height => 300;
        public int ChannelCount => 1;
        public SensorType SensorType => SensorType.Monochrome;
        public int BayerOffsetX => 0;
        public int BayerOffsetY => 0;
        public int FrameCount => 1;
        public int FrameIndex => 0;
        public float[] PerChannelBackground => [0f];
        public float LumaBackground => 0f;
        public ImageHistogram[] ChannelStatistics => [];
        public ReadOnlySpan<float> GetChannelData(int channel) => default;
        public bool SelectFrame(int index) => false;
        public bool HasTimestamps => false;
        public DateTimeOffset TimestampOf(int index) => DateTimeOffset.MinValue;

        public StretchUniforms ComputeStretchUniforms(
            StretchMode mode, StretchParameters parameters,
            LumaWeighting weighting = LumaWeighting.Rec709, float lumaBlend = 1f, bool normalize = false,
            int curvesMode = 0, ReadOnlySpan<float> curveLut = default, float curvesBoost = 0f,
            float curvesMidpoint = 0.25f, float hdrAmount = 0f, float hdrKnee = 0.8f,
            float bgNeutralizationStrength = 1f, (float R, float G, float B)? manualWhiteBalance = null)
            => new StretchUniforms(mode, 1f, default, default, default, default, default);
    }
}
