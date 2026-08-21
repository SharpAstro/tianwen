using System;
using System.IO;
using System.Threading.Tasks;
using SharpAstro.Png;
using SharpAstro.Tiff;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// D3' step 2: an importer whose source container was 8-bit keeps the ORIGINAL samples beside the
/// float planes, so the viewer can upload those instead (1 B/px held buys 3 B/px of device memory
/// back, and skips a re-quantise because the floats were derived from these very bytes).
/// </summary>
/// <remarks>
/// The interesting assertions here are the NEGATIVE ones. Retaining is an optimisation and a missing
/// raster costs only memory, but retaining a STALE one uploads a confidently wrong picture -- so what
/// needs pinning is that every path which rewrites pixels drops it.
/// </remarks>
[Collection("Imaging")]
public class SourceRasterRetentionTests
{
    private const int Width = 96;
    private const int Height = 64;

    [Fact]
    public async Task An8BitTiffKeepsSamplesThatExactlyReproduceTheFloatPlane()
    {
        var ct = TestContext.Current.CancellationToken;
        var pixels = Ramp();
        var path = await WriteGray8TiffAsync(pixels, ct);
        try
        {
            Image.TryReadImageFile(path, out var image).ShouldBeTrue();
            image.ShouldNotBeNull();
            image.BitDepth.ShouldBe(BitDepth.Int8);
            image.HasSourceRaster.ShouldBeTrue("an 8-bit TIFF is exactly the case D3' retains for");

            image.TryGetSourceRaster(0, out var raster).ShouldBeTrue();
            raster.Length.ShouldBe(Width * Height);

            // The float plane IS the byte / 255, so the retained bytes must reproduce it exactly --
            // that equivalence is what makes uploading them lossless rather than a quality trade.
            var floats = image.GetChannelSpan(0);
            for (var i = 0; i < raster.Length; i++)
            {
                floats[i].ShouldBe(raster[i] / 255f, 1e-7f, $"texel {i}");
            }
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    [Fact]
    public async Task ADocumentOpenStillCarriesIt()
    {
        // If the raster does not survive to the document, step 3 has nothing to upload.
        var ct = TestContext.Current.CancellationToken;
        var path = await WriteGray8TiffAsync(Ramp(), ct);
        try
        {
            var document = await AstroImageDocument.OpenAsync(path, cancellationToken: ct);
            document.ShouldNotBeNull();
            document.UnstretchedImage.HasSourceRaster.ShouldBeTrue(
                "AdoptImageAsync normalises, but an already-normalised image early-returns unchanged");
            document.UnstretchedImage.TryGetSourceRaster(0, out var raster).ShouldBeTrue();
            raster.Length.ShouldBe(Width * Height);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    /// <summary>
    /// A non-8-bit source keeps nothing: <c>R8Unorm</c> cannot hold its samples, so the float path
    /// stands.
    /// <para>Written with a FLOAT tiff rather than a 16-bit one purely for test-plumbing reasons --
    /// PNG itself is 8 OR 16 bit (<c>PngWriter.EncodeGray16</c> exists and is used elsewhere here),
    /// but <c>TiffWriter.AddPngPageAsync</c>, the cheapest route to a real TIFF from this test, only
    /// re-frames 8-bit PNG rows. The assertion is about the importer's gate, which keys on
    /// <see cref="BitDepth"/> and refuses Int16 and Float32 by the same rule.</para>
    /// <para><b>16-bit is not an oversight, it is arithmetic.</b> Per channel a float plane costs 4 B/px
    /// on the host and 4 on the device. Retaining 8-bit samples costs 1 and lets the texture be 1, so
    /// it nets -3. Retaining 16-bit would cost 2 and let the texture be <c>R16Unorm</c> at 2, so it
    /// nets ZERO -- it only starts paying once D1' stops keeping the float planes resident, and it
    /// belongs to that milestone rather than this one.</para>
    /// </summary>
    [Fact]
    public async Task AFloatTiffKeepsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var plane = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++) { plane[y, x] = (y * Width + x) / (float)(Width * Height); }
        }
        var source = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, Meta());
        var path = Path.Combine(Path.GetTempPath(), $"tw-rasterf-{Guid.NewGuid():N}.tif");
        try
        {
            await source.WriteStretchedTiffAsync(path, ct);

            Image.TryReadImageFile(path, out var image).ShouldBeTrue();
            image.ShouldNotBeNull();
            image.BitDepth.ShouldBe(BitDepth.Float32);
            image.HasSourceRaster.ShouldBeFalse();
            image.TryGetSourceRaster(0, out _).ShouldBeFalse();
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    [Fact]
    public void AnImageConstructedWithoutOneHasNone()
    {
        // Fail-closed is the whole safety argument: because the raster is an opt-in constructor
        // argument, every transform that builds a fresh image loses it without having to remember to.
        var plane = new float[4, 4];
        var image = new Image([plane], BitDepth.Int8, 1f, 0f, 0f, Meta());

        image.HasSourceRaster.ShouldBeFalse(
            "BitDepth.Int8 alone must not imply a raster -- only an importer that kept the bytes can");
        image.TryGetSourceRaster(0, out _).ShouldBeFalse();
    }

    [Fact]
    public void AShapeMismatchIsDeclinedRatherThanUploaded()
    {
        // The alternative to declining is a texture built from the wrong number of bytes, which draws
        // a plausible-looking wrong picture instead of failing.
        var plane = new float[4, 4];
        var wrongSize = new byte[9];
        var image = new Image([plane], BitDepth.Int8, 1f, 0f, 0f, Meta(),
            samplesAreUnitReferred: true, sourceRaster: [wrongSize]);

        image.TryGetSourceRaster(0, out _).ShouldBeFalse();
        image.TryGetSourceRaster(1, out _).ShouldBeFalse("out-of-range channel");
    }

    private static byte[] Ramp()
    {
        var pixels = new byte[Width * Height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 3 % 256);
        }
        return pixels;
    }

    private static async Task<string> WriteGray8TiffAsync(byte[] pixels, System.Threading.CancellationToken ct)
    {
        var png = PngWriter.EncodeGray8(pixels, Width, Height);
        var path = Path.Combine(Path.GetTempPath(), $"tw-raster8-{Guid.NewGuid():N}.tif");
        await using var writer = TiffWriter.Create(path);
        await writer.AddPngPageAsync(png, ct: ct);
        await writer.FlushAsync(ct);
        return path;
    }

    private static ImageMeta Meta()
        => new("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
            0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
            RowOrder.TopDown, float.NaN, float.NaN);
}
