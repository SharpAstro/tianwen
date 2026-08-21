using System;
using System.IO;
using System.Threading.Tasks;
using SharpAstro.Tiff;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// M2 of <c>docs/plans/viewer-memory-footprint.md</c>: reading a TIFF used to assemble the whole
/// raster and then convert it, so the raster and the float planes were both fully resident. For the
/// plan's 13228x9354 RGB page that is 354 MiB of intermediate whose only purpose is to be read once.
/// <c>TiffReader.ReadInto</c> converts each strip as it arrives instead.
///
/// <para><b>Why this is an ALLOCATION test and not a working-set measurement.</b> I tried the obvious
/// thing first -- open the document in the viewer and read the process working set with and without
/// the change -- and it produced a confident, completely wrong answer: 1564 MB "off the peak". Then
/// the SAME unmodified build measured 3362 MB steady in one run and 2143 MB in another. Run-to-run
/// variance is over 1200 MB, which swamps the 371 MB this change is worth, so working set cannot
/// measure it at all; the first result was noise that happened to point the right way.</para>
///
/// <para><see cref="GC.GetAllocatedBytesForCurrentThread"/> has none of that problem. The decode is
/// synchronous on the calling thread, the raster is one big array, and the count is exact -- so
/// "was a raster allocated" becomes a question with a yes/no answer instead of a statistic.</para>
///
/// <para><b>D3' narrowed this invariant on purpose, and this test caught it doing so.</b> M2's rule
/// was "no raster-sized allocation beside the float planes" full stop, and D3' now retains the
/// 8-bit samples so the viewer can upload THOSE instead of the widened floats. So the rule is now
/// narrower and still meaningful: the intermediate that M2 removed was <b>4 B/px</b> of assembled
/// float raster whose only purpose was to be read once, and it is still gone; what exists now is
/// <b>1 B/px</b> that is kept because it saves 3 B/px of device memory. The bound below therefore
/// accounts for exactly one 8-bit raster and is paired with an assertion that the retention
/// actually happened -- otherwise the extra headroom would silently license the very allocation
/// the test exists to forbid.</para>
/// </summary>
[Collection("Imaging")]
public class TiffStreamingDecodeAllocationTests
{
    // Big enough that the raster dominates the noise floor, small enough to stay a unit test.
    private const int Width = 1200;
    private const int Height = 900;
    private const int Channels = 3;

    /// <summary>
    /// The float planes are unavoidable -- they are the output, and since D3' one 8-bit raster is
    /// deliberate. What must NOT appear is a SECOND raster-sized allocation: the assembled float
    /// intermediate M2 deleted, or the file slurped into a byte[]. The bound is generous on purpose:
    /// it only has to be tight enough to exclude those, and a bound tuned finer would fail on an
    /// unrelated allocation change and teach everyone to widen it.
    /// </summary>
    /// <remarks>No LZW: TiffWriter cannot ENCODE it, and until SharpAstro.Tiff 3.11 asking for it
    /// silently wrote raw bytes labelled LZW. That corrupt fixture is what surfaced the writer bug --
    /// this test read 50 where it had written 3.</remarks>
    [Theory]
    [InlineData(TiffCompression.Uncompressed)]
    [InlineData(TiffCompression.Deflate)]
    public async Task DecodingATiffDoesNotAllocateTheWholeRasterBesideTheFloatPlanes(TiffCompression compression)
    {
        var path = await WriteTiffAsync(compression);
        try
        {
            var floatPlaneBytes = (long)Width * Height * Channels * sizeof(float);
            var rasterBytes = (long)Width * Height * Channels;   // 8-bit samples
            // D3': an 8-bit page keeps its original samples for the R8Unorm upload path.
            var retainedRasterBytes = rasterBytes;

            // Warm the path once: first-touch statics, the file read buffer and any JIT allocations
            // would otherwise land in the measured window and make the bound meaningless.
            Image.TryReadTiff(path, out var warm).ShouldBeTrue();
            warm.ShouldNotBeNull();

            var before = GC.GetAllocatedBytesForCurrentThread();
            Image.TryReadTiff(path, out var image).ShouldBeTrue();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            image.ShouldNotBeNull();
            image.Width.ShouldBe(Width);
            image.Height.ShouldBe(Height);

            // The float planes are the ONLY large term left. This ceiling deliberately excludes the
            // file: it used to have to include it, because File.ReadAllBytes returned a byte[] as big
            // as the raster for an uncompressed page -- M2 removed the raster and simply traded it for
            // the file buffer. The file is now memory-mapped, so there is no array for it at all, and
            // the bound can say so.
            var fileBytes = new FileInfo(path).Length;

            // The retention is what the extra term below pays for, so assert it happened. Without
            // this the widened ceiling would quietly permit a stray raster instead of accounting
            // for a chosen one.
            image.HasSourceRaster.ShouldBeTrue(
                "an 8-bit page must retain its samples, which is what the raster term in the ceiling is");

            // Half a raster of headroom above the two terms we expect: comfortably clear of the
            // per-strip scratch, comfortably below either "a SECOND raster got allocated" or "the
            // file got slurped".
            var ceiling = floatPlaneBytes + retainedRasterBytes + rasterBytes / 2;
            allocated.ShouldBeLessThan(ceiling,
                $"allocated {allocated:N0} B; expected the float planes ({floatPlaneBytes:N0} B) plus " +
                $"one retained 8-bit raster ({retainedRasterBytes:N0} B) and nothing else large -- a " +
                $"second raster would add {rasterBytes:N0} B, slurping the file would add " +
                $"{fileBytes:N0} B");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The streaming conversion must produce the same pixels as the whole-raster one did -- strip
    /// offsets are the obvious thing to get wrong, and a row written at the wrong y is a picture that
    /// still looks like a picture. Compared against the buffering reader, which is the behaviour being
    /// preserved rather than a value I chose.
    /// </summary>
    [Theory]
    [InlineData(TiffCompression.Uncompressed)]
    [InlineData(TiffCompression.Deflate)]
    public async Task TheStreamedDecodeMatchesTheBufferedRasterPixelForPixel(TiffCompression compression)
    {
        var path = await WriteTiffAsync(compression);
        try
        {
            Image.TryReadTiff(path, out var image).ShouldBeTrue();
            image.ShouldNotBeNull();

            // The oracle: decode the same file the old way and convert it here, in the test.
            var expected = TiffReader.Read(File.ReadAllBytes(path)).Pages[0];
            expected.Pixels.Length.ShouldBe(Width * Height * Channels);

            var span = image.GetChannelSpan(0);
            const float inv = 1f / 255f;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var fromRaster = expected.Pixels[(y * Width + x) * Channels] * inv;
                    span[y * Width + x].ShouldBe(fromRaster, 1e-6f,
                        $"channel 0 at ({x}, {y}) -- a mismatch confined to whole rows means a strip " +
                        "landed at the wrong y");
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A gradient in both axes plus a per-channel offset, so a strip written at the wrong row, or
    /// channels transposed, changes the VALUES rather than producing a differently-plausible image.
    /// </summary>
    private static async Task<string> WriteTiffAsync(TiffCompression compression)
    {
        var pixels = new byte[Width * Height * Channels];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var p = (y * Width + x) * Channels;
                pixels[p] = (byte)(x * 3 + y * 5);
                pixels[p + 1] = (byte)(x * 7 + y * 11 + 40);
                pixels[p + 2] = (byte)(x * 13 + y * 17 + 80);
            }
        }

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".tif");
        using (var fs = File.Create(path))
        await using (var writer = TiffWriter.Create(fs))
        {
            await writer.AddPageAsync(pixels, Width, Height, new TiffPageOptions
            {
                SamplesPerPixel = Channels,
                BitsPerSample = 8,
                Photometric = TiffPhotometric.Rgb,
                SampleFormat = TiffSampleFormat.Uint,
                Compression = compression,
            });
            await writer.FlushAsync();
        }
        return path;
    }
}
