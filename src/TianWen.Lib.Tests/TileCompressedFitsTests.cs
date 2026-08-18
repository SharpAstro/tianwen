using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;
using nom.tam.fits;
using nom.tam.util;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Reading tile-compressed (<c>.fz</c>) images, the form <c>fpack</c> writes and Siril,
    /// SharpCap and the survey archives emit.
    ///
    /// <para>The fixture's shape is the point of it: an EMPTY primary HDU with the pixels in the
    /// extension behind it. A binary table cannot be a primary HDU, so a compressed image is
    /// never in HDU 0 and never can be -- which is why the readers here walk to the first HDU
    /// that carries an image instead of assuming the first one does. That walk also fixes plain
    /// multi-extension FITS, whose image lives in an extension by choice rather than by
    /// necessity, and which was equally unreadable before.</para>
    /// </summary>
    [Collection("Imaging")]
    public class TileCompressedFitsTests : IDisposable
    {
        private const string Fixture = "tilecompressed.fz";

        private readonly string _dir;
        private readonly string _path;

        public TileCompressedFitsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "tianwen-fz-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            // The extension is load-bearing -- the reader dispatches on it -- so the fixture has
            // to reach disk under its real name.
            _path = Path.Combine(_dir, Fixture);
            using var source = SharedTestData.OpenEmbeddedFileStream(Fixture)
                ?? throw new InvalidOperationException($"Missing test data {Fixture}");
            using var target = File.Create(_path);
            source.CopyTo(target);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory left behind is not worth failing a test over.
            }
        }

        [Fact]
        public void GivenAFzFile_WhenReadByExtension_ThenTheImageIsDecompressed()
        {
            Image.TryReadImageFile(_path, out var image).ShouldBeTrue();
            image.ShouldNotBeNull();

            image.ChannelCount.ShouldBe(3);
            image.Height.ShouldBe(24);
            image.Width.ShouldBe(32);
            image.BitDepth.ShouldBe(BitDepth.Float32);
        }

        [Fact]
        public void GivenAFzFile_WhenRead_ThenTheHeaderMetadataSurvivesTheTranslation()
        {
            Image.TryReadFitsFile(_path, out var image, out var wcs).ShouldBeTrue();
            image.ShouldNotBeNull();

            // These cards live in the compressed HDU's header alongside the Z-prefixed
            // compression keywords; reading them proves the translation kept them.
            image.ImageMeta.ObjectName.ShouldBe("Bubble Nebula");
            image.ImageMeta.ExposureDuration.ShouldBe(TimeSpan.FromSeconds(150));
            image.ImageMeta.Instrument.ShouldBe("AA585CTEC");
            image.ImageMeta.Telescope.ShouldBe("200P");
            image.ImageMeta.RowOrder.ShouldBe(RowOrder.TopDown);
            image.ImageMeta.FocalLength.ShouldBe(1180);

            wcs.ShouldNotBeNull();
            wcs.Value.CenterRA.ShouldBe(350.185220211384 / 15.0, 1e-9);
            wcs.Value.CenterDec.ShouldBe(61.1914981059903, 1e-9);
        }

        [Fact]
        public void GivenAFzFile_ThenTheImageIsNotInTheFirstHdu()
        {
            // The premise the readers have to cope with, asserted rather than assumed: HDU 0
            // carries no array at all, so a reader that stops there finds nothing.
            using var reader = new BufferedFile(_path, FileAccess.Read, FileShare.Read);
            using var fits = new Fits(reader, false);

            var primary = fits.ReadHDU();
            primary.ShouldNotBeNull();
            primary.Axes.ShouldBeNull("fpack always writes an empty primary HDU");

            var extension = fits.ReadHDU();
            extension.ShouldNotBeNull();
            extension.ShouldBeAssignableTo<ImageHDU>();
            extension.Axes.ShouldBe(new[] { 3, 24, 32 });
        }

        [Fact]
        public void GivenAFzFile_WhenOnlyTheHeaderIsRead_ThenTheFrameIsDescribed()
        {
            // The folder-scan path: it must find the same image without reading any pixels.
            Image.TryReadFitsHeader(_path, out var frameInfo).ShouldBeTrue();
            frameInfo.ShouldNotBeNull();

            frameInfo.Width.ShouldBe(32);
            frameInfo.Height.ShouldBe(24);
            frameInfo.ChannelCount.ShouldBe(3);
            frameInfo.BitDepth.ShouldBe(BitDepth.Float32);
            frameInfo.Meta.ObjectName.ShouldBe("Bubble Nebula");
        }

        [Fact]
        public async Task GivenAFolderOfFzFiles_WhenScanned_ThenTheyAreEnumerated()
        {
            var source = new FitsFolderFrameSource(_dir);

            var frames = new List<FrameInfo>();
            await foreach (var frame in source.EnumerateAsync(TestContext.Current.CancellationToken))
            {
                frames.Add(frame);
            }

            frames.Count.ShouldBe(1);
            frames[0].Path.ShouldEndWith(".fz");
            frames[0].Width.ShouldBe(32);
            frames[0].ChannelCount.ShouldBe(3);
        }
    }
}
