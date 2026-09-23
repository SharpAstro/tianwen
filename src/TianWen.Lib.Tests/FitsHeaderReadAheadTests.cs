using Shouldly;
using System;
using System.IO;
using TianWen.Lib.Imaging;
using Xunit;
using nom.tam.fits;
using nom.tam.util;

namespace TianWen.Lib.Tests;

/// <summary>
/// A header-only open reads a header-sized read-ahead rather than a frame-sized one (#307 <c>#97</c>),
/// and that must not cost a header that is LARGER than the read-ahead anything: it is read whole,
/// one more fill at a time, including the cards past the first fill.
/// </summary>
[Collection("Imaging")]
public class FitsHeaderReadAheadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fitshdr-" + Guid.NewGuid().ToString("N")[..8]);

    public FitsHeaderReadAheadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A 64x48 frame whose header carries <paramref name="fillerCards"/> cards BEFORE the
    /// ones the scan needs, so those land wherever the filler pushes them.</summary>
    private string WriteFrame(int fillerCards)
    {
        var path = Path.Combine(_dir, $"frame-{fillerCards}.fits");
        var hdu = FitsFactory.HDUFactory(new short[48, 64]);
        for (var i = 0; i < fillerCards; i++)
        {
            hdu.AddValue($"FILL{i:D4}", i, "filler, to grow the header");
        }
        hdu.AddValue("IMAGETYP", "Dark", "");
        hdu.AddValue("EXPTIME", 120.0, "");
        hdu.AddValue("GAIN", 121, "");
        hdu.AddValue("CCD-TEMP", -10.0, "");
        var fits = new Fits();
        fits.AddHDU(hdu);
        using (var bf = new BufferedFile(path, FileAccess.ReadWrite, FileShare.None))
        {
            fits.Write(bf);
            bf.Flush();
        }
        return path;
    }

    [Fact]
    public void AHeaderLargerThanTheReadAhead_IsReadWhole()
    {
        // 1,200 filler cards is 96,000 bytes of header, 34 blocks against a 22-block read-ahead, so
        // every card the scan reads sits past the first fill.
        var path = WriteFrame(fillerCards: 1200);
        new FileInfo(path).Length.ShouldBeGreaterThan(Image.HeaderReadAheadBytes + (48 * 64 * 2),
            "the fixture must put the header past the read-ahead, or this test proves nothing");

        Image.TryReadFitsHeader(path, out var header).ShouldBeTrue();
        Image.TryReadFitsFile(path, out var image).ShouldBeTrue();

        header.Width.ShouldBe(64);
        header.Height.ShouldBe(48);
        header.Meta.ExposureDuration.ShouldBe(TimeSpan.FromSeconds(120));
        header.Meta.Gain.ShouldBe((short)121);
        header.Meta.CCDTemperature.ShouldBe(-10f);
        header.Meta.FrameType.ShouldBe(image.ImageMeta.FrameType);
        header.Meta.ExposureDuration.ShouldBe(image.ImageMeta.ExposureDuration);
    }

    [Fact]
    public void TheHeaderOpener_ReadsTheSameHeaderAsTheFrameOpener()
    {
        var path = WriteFrame(fillerCards: 1200);

        using var small = Image.OpenFitsHeader(path);
        using var large = Image.OpenFits(path);
        var viaHeader = small.ReadFirstImageHduHeaderOnly()?.Header;
        var viaFrame = large.ReadFirstImageHduHeaderOnly()?.Header;

        viaHeader.ShouldNotBeNull();
        viaFrame.ShouldNotBeNull();
        viaHeader.NumberOfCards.ShouldBe(viaFrame.NumberOfCards);
        viaHeader.GetIntValue("FILL1199").ShouldBe(1199);
        viaHeader.GetIntValue("GAIN").ShouldBe(viaFrame.GetIntValue("GAIN"));
    }
}
