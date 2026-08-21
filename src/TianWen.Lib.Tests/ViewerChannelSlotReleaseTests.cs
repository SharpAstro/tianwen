using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// An upload pass reports how many texture SLOTS it filled, so a backend can give back the rest.
    /// </summary>
    /// <remarks>
    /// <para>The wiring half of the release; the device half (that the memory is actually reclaimed and
    /// the live channel survives) is pinned against a real driver by <c>GpuChannelFormatTests</c>.
    /// Separate because the routing is surface-agnostic and this needs no GPU.</para>
    /// <para><b>The raw-Bayer case is why this suite exists.</b> That path sets
    /// <c>ChannelTextureCount = 3</c> -- the shader's output arity, since it demosaics -- while filling
    /// ONE slot. Passing that uniform to the release would keep two stale full-size textures alive on
    /// exactly the path that needs one, and nothing observable would say so.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerChannelSlotReleaseTests
    {
        private const int Width = 8;
        private const int Height = 6;

        [Fact]
        public async Task ACompositeViewOfAThreeChannelImageFillsEverySlot()
        {
            var document = await NewColourDocumentAsync();
            var viewer = NewViewer();

            viewer.UploadDocumentTextures(document, NewState(ChannelView.Composite));

            viewer.ReleaseCalls.ShouldBe([3]);
            viewer.ChannelTextureCount.ShouldBe(3);
        }

        [Fact]
        public async Task ASingleChannelViewFillsOneSlotAndReleasesTheRest()
        {
            var document = await NewColourDocumentAsync();
            var viewer = NewViewer();

            viewer.UploadDocumentTextures(document, NewState(ChannelView.Blue));

            viewer.ReleaseCalls.ShouldBe([1], "two slots are no longer sampled and must be reclaimable");
            viewer.ChannelTextureCount.ShouldBe(1);
        }

        /// <summary>
        /// Raw Bayer: three shader channels out of ONE uploaded mosaic. The two numbers differ here and
        /// nowhere else, which is the whole reason the release takes a slot count rather than the uniform.
        /// </summary>
        [Fact]
        public async Task RawBayerFillsOneSlotWhileTheShaderStillProducesThree()
        {
            var document = await NewBayerDocumentAsync();
            var viewer = NewViewer();

            viewer.UploadDocumentTextures(document, NewState(ChannelView.Composite));

            viewer.ChannelTextureCount.ShouldBe(3, "the shader demosaics, so it outputs RGB");
            viewer.ReleaseCalls.ShouldBe([1], "but only the mosaic slot was filled");
        }

        /// <summary>
        /// Cycling to a channel and back must re-fill the slots it released, or the composite comes back
        /// missing two channels.
        /// </summary>
        [Fact]
        public async Task CyclingBackToCompositeRefillsEverySlot()
        {
            var document = await NewColourDocumentAsync();
            var viewer = NewViewer();
            var state = NewState(ChannelView.Composite);

            viewer.UploadDocumentTextures(document, state);
            state.ChannelView = ChannelView.Green;
            viewer.UploadDocumentTextures(document, state);
            state.ChannelView = ChannelView.Composite;
            viewer.UploadDocumentTextures(document, state);

            viewer.ReleaseCalls.ShouldBe([3, 1, 3]);
        }

        /// <summary>Records the slot count each upload pass reported. Everything above is shipped code.</summary>
        private sealed class RecordingReleaseViewer : ImageRendererBase<RgbaImage>
        {
            public RecordingReleaseViewer(RgbaImageRenderer renderer) : base(renderer)
            {
                Width = renderer.Width;
                Height = renderer.Height;
            }

            public List<int> ReleaseCalls { get; } = [];

            protected override void ReleaseUnusedChannelTextures(int liveSlotCount)
                => ReleaseCalls.Add(liveSlotCount);

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int imageWidth, int imageHeight) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;

            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels) { }

            protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
                ViewerState state, float left, float top, float right, float bottom,
                uint projW, uint projH) { }

            protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
                float angleRad, RGBAColor32 color, float thickness) { }

            protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

            protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
                RGBAColor32 color, float thickness) { }

            protected override void OnResize(uint width, uint height) { }
        }

        private static RecordingReleaseViewer NewViewer()
            => new RecordingReleaseViewer(new RgbaImageRenderer(400, 300));

        private static ViewerState NewState(ChannelView view) => new ViewerState
        {
            HideChrome = true,
            ShowFileList = false,
            ShowInfoPanel = false,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
            ChannelView = view,
        };

        private static Task<AstroImageDocument> NewColourDocumentAsync()
        {
            var planes = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                planes[c] = Ramp(1000f + c * 5000f);
            }

            return AstroImageDocument.AdoptImageAsync(
                new Image(planes, BitDepth.Int16, 65535f, 0f, 0f, Meta(SensorType.Color)),
                DebayerAlgorithm.None);
        }

        // RGGB with the default algorithm keeps ONE channel: AdoptImageAsync deliberately does not CPU
        // debayer, because the GPU shader does it. That is what puts the upload on the raw-Bayer path.
        private static Task<AstroImageDocument> NewBayerDocumentAsync()
            => AstroImageDocument.AdoptImageAsync(
                new Image([Ramp(1000f)], BitDepth.Int16, 65535f, 0f, 0f, Meta(SensorType.RGGB)),
                DebayerAlgorithm.AHD);

        private static float[,] Ramp(float baseValue)
        {
            var plane = new float[Height, Width];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    plane[y, x] = baseValue + y * Width + x;
                }
            }
            return plane;
        }

        private static ImageMeta Meta(SensorType sensorType)
            => new("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, sensorType, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN);
    }
}
