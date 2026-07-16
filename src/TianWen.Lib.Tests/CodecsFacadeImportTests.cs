using SharpAstro.Png;
using Shouldly;
using System;
using System.IO;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Covers the <see cref="Image.TryReadImageFile"/> fallback onto the SharpAstro.Codecs
/// facade — the raster formats tianwen writes (PNG previews, EXR/JXR masters) but had no
/// bespoke reader for. These previously returned <c>false</c>; the facade now decodes them.
/// PNG is the exercised codec here because the test project already references SharpAstro.Png
/// to encode the fixture in-memory (no on-disk binary fixture needed).
/// </summary>
[Collection("Imaging")]
public class CodecsFacadeImportTests(ITestOutputHelper testOutput)
{
    [Fact]
    public void GivenRgb8PngWhenReadViaFacadeThenPixelsRoundTrip()
    {
        // given — a known 8-bit RGBA gradient (alpha opaque, must be dropped on read)
        const int w = 8, h = 6;
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            rgba[i] = (byte)(x * 30);     // R ramps with x
            rgba[i + 1] = (byte)(y * 40); // G ramps with y
            rgba[i + 2] = 100;            // B constant
            rgba[i + 3] = 255;            // A opaque
        }
        var png = PngWriter.Encode(rgba, w, h);

        var path = Path.Combine(SharedTestData.CreateTempTestOutputDir(), "facade_rgb8.png");
        File.WriteAllBytes(path, png);

        // when
        var ok = Image.TryReadImageFile(path, out var image);

        // then — a PNG that TryReadImageFile used to reject now loads as a 3-channel RGB image
        ok.ShouldBeTrue("PNG should decode through the SharpAstro.Codecs facade");
        image.ShouldNotBeNull();
        image.Width.ShouldBe(w);
        image.Height.ShouldBe(h);
        image.ChannelCount.ShouldBe(3); // alpha dropped

        var r = image.GetChannelSpan(0);
        var g = image.GetChannelSpan(1);
        var b = image.GetChannelSpan(2);
        const float tol = 1f / 255f;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var p = y * w + x;
            r[p].ShouldBe((x * 30) / 255f, tol);
            g[p].ShouldBe((y * 40) / 255f, tol);
            b[p].ShouldBe(100 / 255f, tol);
        }
        testOutput.WriteLine($"8-bit RGB PNG round-tripped: {w}x{h}, 3 channels");
    }

    [Fact]
    public void GivenGray16PngWhenReadViaFacadeThenMonoRoundTrips()
    {
        // given — a known 16-bit grayscale ramp (exercises the UInt16 -> [0,1] widening)
        const int w = 8, h = 6;
        var gray = new ushort[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            gray[y * w + x] = (ushort)((x + y) * 1000);
        var png = PngWriter.EncodeGray16(gray, w, h);

        var path = Path.Combine(SharedTestData.CreateTempTestOutputDir(), "facade_gray16.png");
        File.WriteAllBytes(path, png);

        // when
        var ok = Image.TryReadImageFile(path, out var image);

        // then — grayscale decodes to a single-channel mono image
        ok.ShouldBeTrue("16-bit gray PNG should decode through the facade");
        image.ShouldNotBeNull();
        image.Width.ShouldBe(w);
        image.Height.ShouldBe(h);
        image.ChannelCount.ShouldBe(1);

        var m = image.GetChannelSpan(0);
        const float tol = 1f / 65535f;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var p = y * w + x;
            m[p].ShouldBe(((x + y) * 1000) / 65535f, tol);
        }
        testOutput.WriteLine($"16-bit gray PNG round-tripped: {w}x{h}, 1 channel");
    }

    [Fact]
    public void GivenRgb8RasterBytesWhenDecodedInMemoryThenThreeChannelUnitRangeImage()
    {
        // Pins the Canon Live View decode contract: the EVF path decodes each JPEG frame straight from the
        // SDK byte[] via Image.TryDecodeRaster (no temp-file round-trip). Exercised losslessly with PNG here
        // (format sniffing is SharpAstro.Codecs' job); a camera-processed frame is demosaiced RGB, so the
        // decoded Image must be 3-channel and normalised to [0,1].
        const int w = 8, h = 6;
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            rgba[i] = (byte)(x * 30);     // R ramps with x
            rgba[i + 1] = (byte)(y * 40); // G ramps with y
            rgba[i + 2] = 100;            // B constant
            rgba[i + 3] = 255;            // A opaque, dropped on decode
        }
        var bytes = PngWriter.Encode(rgba, w, h);

        // when — decoded from the in-memory buffer, NOT a file (the EVF-frame path)
        var ok = Image.TryDecodeRaster(bytes, out var image);

        // then
        ok.ShouldBeTrue("an in-memory raster buffer should decode through the facade");
        image.ShouldNotBeNull();
        image.Width.ShouldBe(w);
        image.Height.ShouldBe(h);
        image.ChannelCount.ShouldBe(3); // alpha dropped -> colour master

        var r = image.GetChannelSpan(0);
        var g = image.GetChannelSpan(1);
        var b = image.GetChannelSpan(2);
        const float tol = 1f / 255f;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var p = y * w + x;
            r[p].ShouldBeInRange(0f, 1f);
            r[p].ShouldBe((x * 30) / 255f, tol);
            g[p].ShouldBe((y * 40) / 255f, tol);
            b[p].ShouldBe(100 / 255f, tol);
        }
        testOutput.WriteLine($"in-memory RGB raster decoded: {w}x{h}, 3 channels, [0,1]");
    }

    [Fact]
    public void GivenGarbageBytesWhenDecodedInMemoryThenReturnsFalse()
    {
        // The EVF path relies on TryDecodeRaster failing softly (no throw) on a malformed frame so the
        // capture loop can back off and keep streaming rather than tear down on one bad JPEG.
        Image.TryDecodeRaster([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07], out var image).ShouldBeFalse();
        image.ShouldBeNull();
    }

    [Fact]
    public void GivenUndecodableContentWhenReadViaFacadeThenReturnsFalse()
    {
        // A .png the facade routes to but cannot sniff/decode returns false (no throw,
        // no Magick.NET fallback) — exercises TryReadViaCodecs' failure path.
        var path = Path.Combine(SharedTestData.CreateTempTestOutputDir(), "garbage.png");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 });

        Image.TryReadImageFile(path, out var image).ShouldBeFalse();
        image.ShouldBeNull();
    }
}
