using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tone's Saturation dial asks the picture ON SHOW how many colour channels it has, not the document (#1880): a SER has no
/// document, its raw frame, live stack and full stack are the source.
/// </summary>
[Collection("UI")]
public class ToneSaturationTests
{
    private const string Reason = "needs a colour frame";

    private sealed class ShownSource(int channels, SensorType sensor) : IPreviewSource
    {
        public int Width => 8;
        public int Height => 6;
        public int ChannelCount => channels;
        public SensorType SensorType => sensor;
        public int BayerOffsetX => 0;
        public int BayerOffsetY => 0;
        public int FrameCount => 1;
        public int FrameIndex => 0;
        public float[] PerChannelBackground => new float[channels];
        public float LumaBackground => 0f;
        public ImageHistogram[] ChannelStatistics => [];
        public ReadOnlySpan<float> GetChannelData(int channel) => new float[Width * Height];
        public bool SelectFrame(int index) => false;
        public bool HasTimestamps => false;
        public DateTimeOffset TimestampOf(int index) => DateTimeOffset.MinValue;

        public StretchUniforms ComputeStretchUniforms(
            StretchMode mode, StretchParameters parameters,
            LumaWeighting weighting = LumaWeighting.Rec709, float lumaBlend = 1f, bool normalize = false,
            int curvesMode = 0, ReadOnlySpan<float> curveLut = default, float curvesBoost = 0f,
            float curvesMidpoint = 0.25f, float hdrAmount = 0f, float hdrKnee = 0.8f,
            float bgNeutralizationStrength = 1f, (float R, float G, float B)? manualWhiteBalance = null,
            bool applyColorCalibration = true)
            => new StretchUniforms(mode, 1f, default, default, default, default, default);
    }

    private sealed class Viewer : ImageRendererBase<RgbaImage>
    {
        public Viewer(RgbaImageRenderer renderer, float dpiScale) : base(renderer)
        {
            Bus = new SignalBus();
            Width = renderer.Width;
            Height = renderer.Height;
            DpiScale = dpiScale;
            FontPath = FontResolver.ResolveSystemFont();
        }

        protected override void RenderImageQuad(IPreviewSource? source, ViewerState state, in DisplayRendition rendition,
            WCS? wcs, float left, float top, float right, float bottom, uint projW, uint projH,
            RenditionSlot slot, bool sampleBeforeChannels) { }
        protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram, ViewerState state,
            float left, float top, float right, float bottom, uint projW, uint projH) { }
        protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
            float rotationRad, RGBAColor32 color, float thickness) { }
        protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }
        protected override void DrawLineOverlay(float x0, float y0, float x1, float y1, RGBAColor32 color, float thickness) { }
        protected override void OnResize(uint width, uint height) { }
        public override void UploadImageTexture(ReadOnlySpan<float> data, int channel, int width, int height) { }
        public override void UploadHistogramData(IPreviewSource source) { }
        protected override HistogramDisplay? GetHistogramDisplay() => null;

        public SliderState Saturation => ToneSaturationSliderState;
        public ImmutableArray<Layout.ArrangedNode<float>> Arranged => ToneLayoutForTest;
    }

    private static (bool Enabled, bool ReasonShown) Open(IPreviewSource source, float dpiScale)
    {
        using var renderer = new RgbaImageRenderer(1800, 1400);
        var viewer = new Viewer(renderer, dpiScale);
        viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, source.Width, source.Height);
        var state = new ViewerState { ShowFileList = false, ShowHistogram = false, StretchMode = StretchMode.None };
        viewer.Render(source, state);
        viewer.TryGetPaintedToolbarRect(ToolbarAction.Tone, out var button).ShouldBeTrue();
        var x = button.X + (button.Width / 2f);
        var y = button.Y + (button.Height / 2f);
        UiRouting.Route(viewer, new InputEvent.MouseDown(x, y), new InputEvent.MouseUp(x, y));
        state.TonePopover.IsOpen.ShouldBeTrue();
        viewer.Render(source, state);
        var reason = viewer.Arranged
            .Any(n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == Reason);
        return (viewer.Saturation.Enabled, reason);
    }

    [Theory]
    [InlineData(1, SensorType.RGGB, true, 1f)]        // a colour SER's raw frame: a mosaic the GPU debayers
    [InlineData(1, SensorType.RGGB, true, 1.5f)]
    [InlineData(3, SensorType.Color, true, 1f)]       // its live stack and full stack's master
    [InlineData(3, SensorType.Color, true, 1.5f)]
    [InlineData(1, SensorType.Monochrome, false, 1f)] // a mono SER, and a mono still
    [InlineData(1, SensorType.Monochrome, false, 1.5f)]
    public Task TheDialFollowsTheColourOfThePictureOnShow(int channels, SensorType sensor, bool colour, float dpi)
    {
        var (enabled, reason) = Open(new ShownSource(channels, sensor), dpi);
        enabled.ShouldBe(colour);
        reason.ShouldBe(!colour, "the dim dial says why");
        return Task.CompletedTask;
    }
}
