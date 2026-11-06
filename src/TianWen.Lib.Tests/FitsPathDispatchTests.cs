using System;
using System.IO;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Which names <see cref="Image.TryReadImageFile(string, out Image?, out TianWen.Lib.Astrometry.WCS?)"/> reads as FITS.
/// </summary>
/// <remarks>
/// <c>tianwen solve</c> moved from <c>TryReadFitsFile</c>, which reads any name ending <c>.gz</c> as gzipped FITS, to
/// the format-agnostic reader so a PoleMaster PNG solves too; the dispatch had to keep what the old call read, and did
/// not for <c>x.fits.gz</c> (reported on PR #2036).
/// </remarks>
[Collection("Imaging")]
public class FitsPathDispatchTests : IDisposable
{
    private const int W = 24;
    private const int H = 16;

    private readonly TempFolders _temp = new TempFolders();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("a.fits", true)]
    [InlineData("a.FIT", true)]
    [InlineData("a.fts", true)]
    [InlineData("a.fz", true)]
    [InlineData("a.fit.fz", true)]
    [InlineData("a.fits.gz", true)]
    [InlineData("A.FITS.GZ", true)]
    [InlineData("a.png", false)]
    [InlineData("a.tif", false)]
    [InlineData("a.cr3", false)]
    [InlineData("a", false)]
    public void AFitsNameIsOneTheFitsReaderTakes(string name, bool isFits)
        => Image.IsFitsPath(name).ShouldBe(isFits);

    [Fact]
    public void AGzippedFitsFileReadsThroughTheFormatAgnosticReader()
    {
        var folder = _temp.Create("fits-gz").FullName;
        var plain = Path.Combine(folder, "frame.fits");
        var gzipped = Path.Combine(folder, "frame.fits" + Image.GzipSuffix);

        var plane = new float[H, W];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                plane[y, x] = 0.25f + (x * 0.01f) + (y * 0.001f);
            }
        }

        var image = new Image([plane], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
            new ImageMeta { SensorType = SensorType.Monochrome });
        image.WriteToFitsFile(plain);
        image.WriteToFitsFile(gzipped);

        // The compressed file really is gzip, or the test would pass over a plain file with a .gz name.
        using (var stream = File.OpenRead(gzipped))
        {
            stream.ReadByte().ShouldBe(0x1f);
            stream.ReadByte().ShouldBe(0x8b);
        }

        Image.TryReadImageFile(plain, out var fromPlain, out _).ShouldBeTrue("the plain file reads");
        Image.TryReadImageFile(gzipped, out var fromGzipped, out _).ShouldBeTrue("a .fits.gz reads as FITS, as it did through TryReadFitsFile");

        fromGzipped.Width.ShouldBe(W);
        fromGzipped.Height.ShouldBe(H);
        fromGzipped.GetChannelSpan(0).SequenceEqual(fromPlain.GetChannelSpan(0)).ShouldBeTrue("the same pixels as the uncompressed file");
    }
}
