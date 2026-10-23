using System;
using System.Collections.Concurrent;
using TianWen.Lib.Geometry;
using System.IO;
using System.Threading.Tasks;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The ANNOTATED save (P22): the display raster with the viewer's overlays drawn over it, at the
    /// image's own resolution.
    /// </summary>
    /// <remarks>
    /// <para>Two claims are worth pinning, and they pull in opposite directions. The pixels UNDER the
    /// annotation must be the clean raster exactly -- otherwise the two saves are two pictures and the
    /// annotated one is not "the same thing with marks on it". And the marks must actually reach the
    /// file -- otherwise the feature is an expensive copy of the clean save. So one test asserts
    /// byte-equality with the clean raster when nothing is switched on, and the next asserts a
    /// difference, in the right place, when something is.</para>
    /// <para>The export runs off the render thread against the LIVE state, so the third test is that
    /// it leaves that state alone: the snapshot exists because flipping zoom and chrome on the real
    /// instance would race the frame in flight and visibly jump the window to 1:1.</para>
    /// </remarks>
    public sealed class AnnotatedRasterExportTests
    {
        private const int ImageW = 96;
        private const int ImageH = 64;

        [Fact]
        public async Task WithNoOverlaysOn_ItIsTheCleanRasterPixelForPixel()
        {
            var document = await NewDocumentAsync();
            var state = NewState();

            var annotated = await ExportAnnotatedAsync(document, state);
            var clean = await ExportCleanAsync(document, state);

            annotated.Width.ShouldBe(ImageW, "the export is the IMAGE's size, not a window's");
            annotated.Height.ShouldBe(ImageH);
            annotated.Pixels.ShouldBe(clean.Pixels,
                "with nothing switched on, the annotated save must BE the clean save -- the two files "
                + "are of one picture, and an annotation is the only thing allowed to differ");
        }

        /// <summary>
        /// An enhanced frame's star split and the Tone finish reach the plain save as they reach the screen and the
        /// annotated save (2026-10-23: the plain save of the enhanced Sculptor in Luma came out pale, the recombined frame
        /// through the picture's curve with no finish, while the screen showed the split and the saturation).
        /// </summary>
        [Fact]
        public async Task AStarSplitAndTheFinishReachThePlainSaveAsTheyReachTheScreen()
        {
            var document = await NewColourDocumentAsync();
            var stars = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                stars[c] = new float[ImageH, ImageW];
                stars[c][20, 30] = 0.02f * (c + 1);
                stars[c][40, 70] = 0.01f * (3 - c);
            }
            document.AttachStarSplit(new StarSplit(document.UnstretchedImage, new Image(stars, BitDepth.Float32, 1f, 0f, 0f,
                document.UnstretchedImage.ImageMeta)));

            var state = NewState();
            state.StretchMode = StretchMode.Luma;
            state.FinishSaturation = 1.8f;
            state.FinishContrast = 0.4f;
            state.StarStretch = 120f;

            var annotated = await ExportAnnotatedAsync(document, state);
            var clean = await ExportCleanAsync(document, state);
            clean.Pixels.ShouldBe(annotated.Pixels, "the plain save is not the picture on screen");

            // And the split and the finish are IN it: without them the same save is a different picture.
            var bare = NewState();
            bare.StretchMode = StretchMode.Luma;
            var withoutFinish = await ExportCleanAsync(document, bare);
            withoutFinish.Pixels.ShouldNotBe(clean.Pixels, "the finish and the star dials changed nothing in the plain save");
        }

        [Fact]
        public async Task TheStarOverlayReachesTheFile()
        {
            var document = await NewDocumentAsync();
            document.RecordStars(new StarList(
            [
                new ImagedStar(HFD: 8f, StarFWHM: 8f, SNR: 100f, Flux: 1000f,
                    XCentroid: 48f, YCentroid: 32f, Ellipticity: 0f),
            ]));

            var plain = NewState();
            var withStars = NewState();
            withStars.ShowStarOverlay = true;

            var before = await ExportAnnotatedAsync(document, plain);
            var after = await ExportAnnotatedAsync(document, withStars);

            after.Pixels.ShouldNotBe(before.Pixels, "the star marker never reached the file");

            // And it landed ON the star rather than somewhere else: every changed pixel is inside the
            // marker's own box. A marker drawn at the wrong scale or the wrong origin still changes
            // pixels, so "it differs" on its own would pass a transposed overlay.
            var (minX, minY, maxX, maxY) = ChangedBounds(before, after);
            minX.ShouldBeGreaterThanOrEqualTo(48 - 12);
            maxX.ShouldBeLessThanOrEqualTo(48 + 12);
            minY.ShouldBeGreaterThanOrEqualTo(32 - 12);
            maxY.ShouldBeLessThanOrEqualTo(32 + 12);
        }

        [Fact]
        public async Task TheLiveStateIsNotTouched()
        {
            var document = await NewDocumentAsync();
            var state = NewState();
            state.Zoom = 3.5f;
            state.PanOffset = (17f, 23f);
            state.ShowInfoPanel = true;
            state.ShowFileList = true;
            state.HideChrome = false;

            await ExportAnnotatedAsync(document, state);

            // The export renders at 1:1 with no chrome. If it did that by mutating this instance, the
            // window would jump -- and on a background thread it would race the frame in flight.
            state.Zoom.ShouldBe(3.5f);
            state.PanOffset.ShouldBe((17f, 23f));
            state.ShowInfoPanel.ShouldBeTrue();
            state.ShowFileList.ShouldBeTrue();
            state.HideChrome.ShouldBeFalse();
        }

        /// <summary>
        /// A view crop reaches the annotated file too, and every annotation stays exactly where it was.
        /// </summary>
        /// <remarks>
        /// This path crops the finished RASTER rather than the pixels going in, and the parity half is
        /// what says so. Annotations are placed through the document's WCS in FULL-frame coordinates, so
        /// an implementation that cropped the input instead would shift every marker by the crop's
        /// origin while the sky it points at stayed put: the file would still be the right SIZE, and
        /// every label would be in the wrong place.
        /// </remarks>
        [Fact]
        public async Task TheAnnotatedCropIsASubRectangleWithTheAnnotationsUnmoved()
        {
            var document = await NewDocumentAsync();
            var state = NewState();

            var full = await ExportAnnotatedAsync(document, state);

            // Asymmetric on both axes, so a transposed crop and an origin-ignoring one are different
            // failures rather than the same one.
            var region = new PixelRect(3, 2, full.Width - 7, full.Height - 5);
            state.DisplayCrop = region;
            var cropped = await ExportAnnotatedAsync(document, state);

            cropped.Width.ShouldBe(region.Width);
            cropped.Height.ShouldBe(region.Height);

            var samples = SamplesPerPixel(full);
            SamplesPerPixel(cropped).ShouldBe(samples);

            for (var y = 0; y < region.Height; y++)
            {
                for (var x = 0; x < region.Width; x++)
                {
                    for (var c = 0; c < samples; c++)
                    {
                        var from = ((((y + region.Y) * full.Width) + x + region.X) * samples) + c;
                        var to = (((y * region.Width) + x) * samples) + c;
                        cropped.Pixels[to].ShouldBe(full.Pixels[from],
                            $"pixel ({x},{y}) sample {c} moved, so an annotation did not stay put");
                    }
                }
            }
        }

        private static (int MinX, int MinY, int MaxX, int MaxY) ChangedBounds(PngImage a, PngImage b)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            var stride = a.Width * SamplesPerPixel(a);
            for (var y = 0; y < a.Height; y++)
            {
                for (var x = 0; x < a.Width; x++)
                {
                    var at = (y * stride) + (x * SamplesPerPixel(a));
                    if (a.Pixels[at] == b.Pixels[at]
                        && a.Pixels[at + 1] == b.Pixels[at + 1]
                        && a.Pixels[at + 2] == b.Pixels[at + 2])
                    {
                        continue;
                    }

                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            minX.ShouldNotBe(int.MaxValue, "nothing changed at all");
            return (minX, minY, maxX, maxY);
        }

        private static int SamplesPerPixel(PngImage png) => png.ColorType switch
        {
            6 => 4,
            2 => 3,
            _ => 1,
        };

        private static async Task<PngImage> ExportAnnotatedAsync(AstroImageDocument document, ViewerState state)
        {
            var path = Path.Combine(Path.GetTempPath(), $"annotated-{Guid.NewGuid():N}.png");
            try
            {
                await AnnotatedRasterExport.WriteAsync(document, state, path, AnnotatedRasterFormat.Png);
                return PngReader.Decode(await File.ReadAllBytesAsync(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// The clean raster through the path the Save button takes, so the comparison above is against
        /// what the viewer actually writes rather than against a second render written here.
        /// </summary>
        private static async Task<PngImage> ExportCleanAsync(AstroImageDocument document, ViewerState state)
        {
            var path = Path.Combine(Path.GetTempPath(), $"clean-{Guid.NewGuid():N}.png");
            try
            {
                await ViewerController.WriteDisplayedRasterAsync(document, state, path, DisplayRasterFormat.Png8,
                    TestContext.Current.CancellationToken);

                return PngReader.Decode(await File.ReadAllBytesAsync(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static ViewerState NewState() => new ViewerState
        {
            StretchMode = StretchMode.None,
            ShowGrid = false,
            ShowOverlays = false,
            ShowStarOverlay = false,
        };

        private static async Task<AstroImageDocument> NewColourDocumentAsync()
        {
            var planes = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                planes[c] = new float[ImageH, ImageW];
                for (var y = 0; y < ImageH; y++)
                {
                    for (var x = 0; x < ImageW; x++)
                    {
                        // A sky with a gradient and a colourful blob, so a saturation and a contrast have something to move.
                        var blob = MathF.Exp(-(((x - 48) * (x - 48)) + ((y - 32) * (y - 32))) / 200f);
                        planes[c][y, x] = 0.01f + (y * 0.0001f) + (x * 0.00005f) + (blob * 0.05f * (1f + (0.4f * (c - 1))));
                    }
                }
            }

            return await AstroImageDocument.AdoptImageAsync(
                new Image(planes, BitDepth.Float32, 1f, 0f, 0f,
                    new ImageMeta("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                        0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Color, 0, 0,
                        RowOrder.TopDown, float.NaN, float.NaN)),
                DebayerAlgorithm.None);
        }

        private static async Task<AstroImageDocument> NewDocumentAsync()
        {
            var plane = new float[ImageH, ImageW];
            for (var y = 0; y < ImageH; y++)
            {
                for (var x = 0; x < ImageW; x++)
                {
                    // Two axes, so a transposed or mirrored render is a different picture rather than
                    // an equal one.
                    plane[y, x] = 1000f + (y * 37) + (x * 7);
                }
            }

            return await AstroImageDocument.AdoptImageAsync(
                new Image([plane], BitDepth.Int16, 65535f, 0f, 0f,
                    new ImageMeta("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                        0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                        RowOrder.TopDown, float.NaN, float.NaN)),
                DebayerAlgorithm.None);
        }
    }
}
