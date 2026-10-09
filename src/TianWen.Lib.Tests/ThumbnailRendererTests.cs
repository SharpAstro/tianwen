using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The Explorer thumbnail path, driven exactly as the shell drives it: a stream and a size, nothing
    /// else. The container is sniffed, so the same entry point takes a plain FITS, a tile-compressed
    /// <c>.fz</c> and a SER capture. Geometry rules (fit inside the edge, keep the aspect, never
    /// upscale) are the shell's contract and are pinned per container.
    /// </summary>
    [Collection("Imaging")]
    public class ThumbnailRendererTests
    {
        [Fact]
        public async Task AMonoFitsFitsInsideTheEdgeAndKeepsItsAspect()
        {
            using var fits = OpenFits(SharedTestData.PlateSolveTestFile); // 1280 x 960

            var thumb = await ThumbnailRenderer.RenderAsync(fits, 256, TestContext.Current.CancellationToken);

            thumb.Width.ShouldBe(256);
            thumb.Height.ShouldBe(192);
            thumb.Rgba.Length.ShouldBe(256 * 192 * 4);
            ShouldBeOpaqueAndNotFlat(thumb);
        }

        [Fact]
        public async Task TheRequestedEdgeBoundsTheOutput()
        {
            using var fits = OpenFits(SharedTestData.PlateSolveTestFile);

            var thumb = await ThumbnailRenderer.RenderAsync(fits, 96, TestContext.Current.CancellationToken);

            thumb.Width.ShouldBe(96);
            thumb.Height.ShouldBe(72);
        }

        [Fact]
        public async Task AnRggbFitsRendersThroughTheDebayer()
        {
            using var fits = OpenFits("RGGB_frame_bx0_by0_top_down");

            var thumb = await ThumbnailRenderer.RenderAsync(fits, 256, TestContext.Current.CancellationToken);

            Math.Max(thumb.Width, thumb.Height).ShouldBe(256);
            Math.Min(thumb.Width, thumb.Height).ShouldBeGreaterThan(0);
            ShouldBeOpaqueAndNotFlat(thumb);
        }

        [Fact]
        public async Task ATileCompressedFzIsSniffedAsFits()
        {
            // The pixels sit in an extension HDU behind an empty primary; the sniff sees SIMPLE and the
            // FITS reader walks to the image, so .fz needs no case of its own.
            using var source = SharedTestData.OpenEmbeddedFileStream("tilecompressed.fz")
                ?? throw new InvalidOperationException("Missing test data tilecompressed.fz");
            using var buffered = new MemoryStream();
            await source.CopyToAsync(buffered, TestContext.Current.CancellationToken);
            buffered.Position = 0;

            var thumb = await ThumbnailRenderer.RenderAsync(buffered, 256, TestContext.Current.CancellationToken);

            Math.Max(thumb.Width, thumb.Height).ShouldBeLessThanOrEqualTo(256);
            ShouldBeOpaqueAndNotFlat(thumb);
        }

        [Fact]
        public async Task ASerCaptureRendersItsFirstFrameAndIsNeverUpscaled()
        {
            // 64 x 48 is far below the requested edge: the output must be the frame's own size.
            using var ser = BuildSer(SerColorId.Mono, 64, 48, pixelDepth: 16, (x, y) => (ushort)(x * 1000 + y * 20 + 500));

            var thumb = await ThumbnailRenderer.RenderAsync(ser, 256, TestContext.Current.CancellationToken);

            thumb.Width.ShouldBe(64);
            thumb.Height.ShouldBe(48);
            ShouldBeOpaqueAndNotFlat(thumb);
        }

        [Fact]
        public async Task ABayerSerCaptureRendersInColour()
        {
            // An RGGB mosaic with a star-like bump: three channels come out of the debayer, so the frame
            // takes the colour (Unlinked) branch of the Auto resolution rather than the mono one.
            using var ser = BuildSer(SerColorId.BayerRGGB, 96, 64, pixelDepth: 8, (x, y) =>
            {
                var dx = x - 48;
                var dy = y - 32;
                var star = 200.0 * Math.Exp(-(dx * dx + dy * dy) / 18.0);
                return (ushort)Math.Min(255, 20 + star);
            });

            var thumb = await ThumbnailRenderer.RenderAsync(ser, 256, TestContext.Current.CancellationToken);

            thumb.Width.ShouldBe(96);
            thumb.Height.ShouldBe(64);
            ShouldBeOpaqueAndNotFlat(thumb);
        }

        [Fact]
        public async Task An8BitSerIsWidenedLikeTheReaderDoes()
        {
            using var ser = BuildSer(SerColorId.Mono, 32, 32, pixelDepth: 8, (x, y) => (ushort)(x * 8));

            var thumb = await ThumbnailRenderer.RenderAsync(ser, 256, TestContext.Current.CancellationToken);

            thumb.Width.ShouldBe(32);
            thumb.Height.ShouldBe(32);
            ShouldBeOpaqueAndNotFlat(thumb);
        }

        [Fact]
        public async Task AnUnknownContainerIsRefused()
        {
            var noise = new byte[4096];
            new Random(42).NextBytes(noise);
            using var stream = new MemoryStream(noise);

            await Should.ThrowAsync<InvalidDataException>(
                () => ThumbnailRenderer.RenderAsync(stream, 256, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ASerWithNoFramesIsRefused()
        {
            var header = SerHeader.Create(SerColorId.Mono, 16, 16, 16, frameCount: 0);
            var bytes = new byte[SerHeader.Size];
            header.Write(bytes);
            using var stream = new MemoryStream(bytes);

            await Should.ThrowAsync<InvalidDataException>(
                () => ThumbnailRenderer.RenderAsync(stream, 256, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task APlanetsMasterIsRenderedInThePlanetaryStretchAsTheViewerOpensIt()
        {
            // A planetary master: a sky at 0.066 with noise of a few 1e-4, a textured disk up to about 0.3, OBJECT the planet. The
            // deep-sky auto-stretch lifts that sky to a quarter as grain (reported in the viewer, 2026-10-02); the thumbnail must show
            // what the viewer opens it in (StretchMode.ForFrame): planetary stack's preview stretch, byte for byte.
            const int n = 64;
            var plane = new float[n, n];
            var random = new Random(3);
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var (dx, dy) = (x - 32, y - 32);
                    plane[y, x] = (dx * dx) + (dy * dy) < 18 * 18
                        ? 0.2f + (0.1f * MathF.Sin(y * 0.5f))
                        : 0.066f + (0.0003f * (float)(random.NextDouble() - 0.5));
                }
            }
            Image Master(string objectName) => new Image([(float[,])plane.Clone()], BitDepth.Float32, 0.3f, 0f, 0f,
                new ImageMeta("e2e", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), FrameType.Light, "",
                    0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                    RowOrder.TopDown, float.NaN, float.NaN, ObjectName: objectName));

            var planet = Master("Jupiter");
            StretchMode.ForFrame(planet.ImageMeta, StretchMode.Auto).ShouldBe(StretchMode.Planetary);
            var thumb = await ThumbnailRenderer.RenderAsync(planet, 256, TestContext.Current.CancellationToken);
            var expected = new byte[n * n * 4];
            planet.RenderStretchedRgba(planet.ComputePlanetaryStretchUniforms(), expected);
            thumb.Rgba.AsSpan().SequenceEqual(expected).ShouldBeTrue("the thumbnail is the planetary stretch's rendering");
            thumb.Rgba[0].ShouldBeLessThan((byte)10, "the sky is black, as in the preview");

            // Unnamed, the same pixels take the auto-stretch, which is what lifted the sky.
            var unnamed = Master("");
            StretchMode.ForFrame(unnamed.ImageMeta, StretchMode.Auto).ShouldBe(StretchMode.Auto);
            var deepSky = await ThumbnailRenderer.RenderAsync(unnamed, 256, TestContext.Current.CancellationToken);
            deepSky.Rgba[0].ShouldBeGreaterThan((byte)30, "the auto-stretch puts the sky's median at a quarter");
        }

        [Fact]
        public void TheShellIdentitiesAreTheDocumentedOnes()
        {
            // The handler id is the shell's well-known IThumbnailProvider GUID; the CLSID is ours and must
            // never move once shipped, because a changed CLSID is a handler Windows no longer finds.
            ThumbnailRenderer.ThumbnailProviderHandlerId.ShouldBe(new Guid("e357fccd-a995-4576-b01f-234630154e96"));
            ThumbnailRenderer.ShellExtensionClsid.ShouldBe(new Guid("bde44417-0b48-4d32-931e-8b3192e81be2"));
        }

        private static void ShouldBeOpaqueAndNotFlat(ThumbnailRaster thumb)
        {
            var rgba = thumb.Rgba;
            var first = (rgba[0], rgba[1], rgba[2]);
            var varies = false;
            for (var i = 0; i < rgba.Length; i += 4)
            {
                rgba[i + 3].ShouldBe((byte)255);
                varies |= (rgba[i], rgba[i + 1], rgba[i + 2]) != first;
            }

            varies.ShouldBeTrue("a rendered star field cannot be one flat colour");
        }

        private static MemoryStream OpenFits(string name)
        {
            using var gz = SharedTestData.OpenEmbeddedFileStream(name + ".fits.gz")
                ?? throw new InvalidOperationException($"Missing test data {name}");
            using var inflate = new GZipStream(gz, CompressionMode.Decompress);
            var buffered = new MemoryStream();
            inflate.CopyTo(buffered);
            buffered.Position = 0;
            return buffered;
        }

        /// <summary>A one-frame SER in memory: header, then one frame of samples in host (little-endian) order.</summary>
        private static MemoryStream BuildSer(SerColorId colorId, int width, int height, int pixelDepth, Func<int, int, ushort> sample)
        {
            var header = SerHeader.Create(colorId, width, height, pixelDepth, frameCount: 1);
            var bytes = new byte[SerHeader.Size + (int)header.FrameSizeBytes];
            header.Write(bytes.AsSpan(0, SerHeader.Size));

            var frame = bytes.AsSpan(SerHeader.Size);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width) + x;
                    var v = sample(x, y);
                    if (header.BytesPerSample == 1)
                    {
                        frame[i] = (byte)v;
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(i * 2, 2), v);
                    }
                }
            }

            return new MemoryStream(bytes);
        }
    }
}
