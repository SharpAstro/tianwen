using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using SharpAstro.Tiff;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// D3' step 3 of <c>docs/plans/viewer-memory-footprint.md</c>: a document whose source container was
    /// 8-bit uploads its RETAINED samples instead of the floats they were widened into, so the channel
    /// texture is <c>R8Unorm</c> at a quarter of the device memory.
    /// </summary>
    /// <remarks>
    /// <para>Offline, over the CPU renderer, because the routing decision is surface-agnostic: which
    /// overload <c>UploadDocumentTextures</c> reaches is decided above the GPU seam. That the bytes then
    /// SAMPLE to the same [0,1] a float texture would is the GPU's half of the claim and is pinned
    /// separately by <c>GpuChannelFormatTests</c>, through a real driver readback.</para>
    /// <para>The third test is the one that protects everything else: four other suites derive their own
    /// viewers from <c>ImageRendererBase</c>, and they must keep taking the float path untouched. That is
    /// why the capability is a virtual returning false rather than a new abstract member.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerByteTextureUploadTests
    {
        private const int Width = 64;
        private const int Height = 48;

        /// <summary>Records WHICH overload the upload took, and what it carried. Everything above the
        /// seam is the real shipped code.</summary>
        private sealed class RecordingUploadViewer : ImageRendererBase<RgbaImage>
        {
            public RecordingUploadViewer(RgbaImageRenderer renderer) : base(renderer)
            {
                Width = renderer.Width;
                Height = renderer.Height;
            }

            /// <summary>Settable so one fixture can exercise both a backend that implements 8-bit
            /// textures and one that does not.</summary>
            public bool AdvertiseByteSupport { get; set; } = true;

            protected override bool SupportsByteChannelTextures => AdvertiseByteSupport;

            public List<(int Channel, int Length)> FloatUploads { get; } = [];
            public List<(int Channel, byte[] Data)> ByteUploads { get; } = [];

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int imageWidth, int imageHeight)
                => FloatUploads.Add((channel, data.Length));

            public override void UploadImageTexture(ReadOnlySpan<byte> data, int channel,
                int imageWidth, int imageHeight)
                => ByteUploads.Add((channel, data.ToArray()));

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

        [Fact]
        public async Task An8BitDocumentUploadsTheRetainedBytesInsteadOfTheWidenedFloats()
        {
            var ct = TestContext.Current.CancellationToken;
            var path = await WriteGray8TiffAsync(ct);
            try
            {
                var document = await AstroImageDocument.OpenAsync(path, cancellationToken: ct);
                document.ShouldNotBeNull();
                document.UnstretchedImage.HasSourceRaster.ShouldBeTrue(
                    "the fixture must actually be the retaining case, or this test proves nothing");

                var viewer = NewViewer();
                viewer.UploadDocumentTextures(document, NewState());

                viewer.ByteUploads.Count.ShouldBe(1, "one mono channel, uploaded as bytes");
                viewer.FloatUploads.ShouldBeEmpty("the float plane must not also be uploaded");

                var (channel, uploaded) = viewer.ByteUploads[0];
                channel.ShouldBe(0);
                uploaded.Length.ShouldBe(Width * Height);

                // The saving is only lossless if these bytes reproduce the floats they replaced, so
                // compare against what the float path WOULD have uploaded.
                var floats = document.UnstretchedImage.GetChannelSpan(0);
                for (var i = 0; i < uploaded.Length; i++)
                {
                    floats[i].ShouldBe(uploaded[i] / 255f, 1e-7f, $"texel {i}");
                }
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }

        [Fact]
        public async Task AFloatDocumentStillUploadsFloats()
        {
            var ct = TestContext.Current.CancellationToken;
            var path = await WriteFloatTiffAsync(ct);
            try
            {
                var document = await AstroImageDocument.OpenAsync(path, cancellationToken: ct);
                document.ShouldNotBeNull();
                document.UnstretchedImage.HasSourceRaster.ShouldBeFalse();

                var viewer = NewViewer();
                viewer.UploadDocumentTextures(document, NewState());

                viewer.FloatUploads.Count.ShouldBe(1);
                viewer.ByteUploads.ShouldBeEmpty("there are no 8-bit samples to upload");
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }

        [Fact]
        public async Task ABackendWithoutByteTexturesKeepsTakingTheFloatPath()
        {
            // The capability gate. Every other viewer test double inherits the false default, so this
            // is what says their behaviour is untouched -- and it must be checked on a document that
            // DOES have a raster, or it passes for the wrong reason.
            var ct = TestContext.Current.CancellationToken;
            var path = await WriteGray8TiffAsync(ct);
            try
            {
                var document = await AstroImageDocument.OpenAsync(path, cancellationToken: ct);
                document.ShouldNotBeNull();
                document.UnstretchedImage.HasSourceRaster.ShouldBeTrue();

                var viewer = NewViewer();
                viewer.AdvertiseByteSupport = false;
                viewer.UploadDocumentTextures(document, NewState());

                viewer.FloatUploads.Count.ShouldBe(1);
                viewer.ByteUploads.ShouldBeEmpty(
                    "a backend that does not advertise 8-bit textures must never be handed bytes");
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }

        private static RecordingUploadViewer NewViewer()
            => new RecordingUploadViewer(new RgbaImageRenderer(400, 300));

        // Chromeless, no panels, linear: the upload path is what is under test, not layout.
        private static ViewerState NewState() => new ViewerState
        {
            HideChrome = true,
            ShowFileList = false,
            ShowInfoPanel = false,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
        };

        private static async Task<string> WriteGray8TiffAsync(CancellationToken ct)
        {
            var pixels = new byte[Width * Height];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)(i * 7 % 256);
            }

            var png = PngWriter.EncodeGray8(pixels, Width, Height);
            var path = Path.Combine(Path.GetTempPath(), $"tw-upload8-{Guid.NewGuid():N}.tif");
            await using var writer = TiffWriter.Create(path);
            await writer.AddPngPageAsync(png, ct: ct);
            await writer.FlushAsync(ct);
            return path;
        }

        private static async Task<string> WriteFloatTiffAsync(CancellationToken ct)
        {
            var plane = new float[Height, Width];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    plane[y, x] = (y * Width + x) / (float)(Width * Height);
                }
            }

            var source = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, Meta());
            var path = Path.Combine(Path.GetTempPath(), $"tw-uploadf-{Guid.NewGuid():N}.tif");
            await source.WriteStretchedTiffAsync(path, ct);
            return path;
        }

        private static ImageMeta Meta()
            => new("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN);
    }
}
