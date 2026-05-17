using System;
using System.Buffers.Binary;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Round-trip the IMX533 fixture through both readers and assert
/// pixel-for-pixel equality. Guards against header-parse bugs, endian
/// confusion, and BZERO/BSCALE misapplication in <see cref="PartialFitsReader"/>.
/// </summary>
[Collection("Imaging")]
public class PartialFitsReaderTests
{
    private const string FixtureName = "2026-02-15_00-56-23__-5.00_60.00s_0058";

    [Fact]
    public async Task PartialReader_HeaderMatchesFullReader()
    {
        using var fixture = await ExtractFixtureToTempAsync();
        Image.TryReadFitsFile(fixture.Path, out var fullImage).ShouldBeTrue();
        fullImage!.Shape.Width.ShouldBe(3008);
        fullImage.Shape.Height.ShouldBe(3008);

        using var partial = new PartialFitsReader(fixture.Path);
        partial.Width.ShouldBe(3008);
        partial.Height.ShouldBe(3008);
        partial.BitPix.ShouldBe(16);
        partial.BytesPerPixel.ShouldBe(2);
        partial.BZero.ShouldBe(32768.0);
        partial.BScale.ShouldBe(1.0);
        partial.DataOffset.ShouldBeGreaterThan(0);
        // Header is multiple-of-2880; data starts on a block boundary.
        (partial.DataOffset % PartialFitsReader.BlockSize).ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 0, 32, 32)]            // top-left corner
    [InlineData(1500, 1500, 64, 64)]       // middle
    [InlineData(3000 - 16, 3000 - 16, 16, 16)] // bottom-right corner
    [InlineData(0, 1000, 3008, 1)]         // full-width single row
    [InlineData(1000, 0, 1, 3008)]         // full-height single column
    public async Task PartialReader_ReadRegion_MatchesFullReaderPixels(int x, int y, int w, int h)
    {
        using var fixture = await ExtractFixtureToTempAsync();
        Image.TryReadFitsFile(fixture.Path, out var fullImage).ShouldBeTrue();
        using var partial = new PartialFitsReader(fixture.Path);

        var buf = new float[w * h];
        partial.ReadRegion(new Rectangle(x, y, w, h), buf);

        var fullChannel = fullImage!.GetChannelArray(0);
        // Both readers return physical pixel values (post-BZERO+BSCALE);
        // Image.MaxValue tells the downstream pipeline the dynamic-range
        // ceiling but doesn't pre-scale the stored channel. So partial and
        // full should match bit-for-bit on a lossless 16-bit fixture.
        for (var r = 0; r < h; r++)
        {
            for (var c = 0; c < w; c++)
            {
                var partialVal = buf[r * w + c];
                var fullVal = fullChannel[y + r, x + c];
                fullVal.ShouldBe(partialVal, tolerance: 0.5f,
                    $"mismatch at ({x + c}, {y + r}): partial={partialVal}, full={fullVal}");
            }
        }
    }

    [Fact]
    public async Task PartialReader_OutOfBoundsRegion_Throws()
    {
        using var fixture = await ExtractFixtureToTempAsync();
        using var partial = new PartialFitsReader(fixture.Path);

        var buf = new float[16];
        Should.Throw<ArgumentOutOfRangeException>(() =>
            partial.ReadRegion(new Rectangle(-1, 0, 4, 4), buf));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            partial.ReadRegion(new Rectangle(3000, 0, 16, 4), buf));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            partial.ReadRegion(new Rectangle(0, 0, 0, 4), buf));
    }

    [Fact]
    public async Task PartialReader_DestTooSmall_Throws()
    {
        using var fixture = await ExtractFixtureToTempAsync();
        using var partial = new PartialFitsReader(fixture.Path);

        var tooSmall = new float[10];
        Should.Throw<ArgumentException>(() =>
            partial.ReadRegion(new Rectangle(0, 0, 16, 16), tooSmall));
    }

    /// <summary>
    /// Performance smoke: reading 100 random 256x256 tiles from one open
    /// reader should complete in under a second. Memory-mapped access plus
    /// OS page cache keeps individual reads cheap.
    /// </summary>
    [Fact]
    public async Task PartialReader_ManyRegions_FastEnoughForTilePipeline()
    {
        using var fixture = await ExtractFixtureToTempAsync();
        using var partial = new PartialFitsReader(fixture.Path);

        var rng = new Random(42);
        var buf = new float[256 * 256];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            var rx = rng.Next(0, partial.Width - 256);
            var ry = rng.Next(0, partial.Height - 256);
            partial.ReadRegion(new Rectangle(rx, ry, 256, 256), buf);
        }
        sw.Stop();
        sw.ElapsedMilliseconds.ShouldBeLessThan(1000,
            $"100x 256x256 reads took {sw.ElapsedMilliseconds} ms; should be << 1s");
    }

    // Cover the BITPIX=32 (int) + BITPIX=-32 (float) SIMD code paths -- the
    // production-data fixture is BITPIX=16, so these synthetic files are the
    // only thing exercising the 4-byte byte-swap loops end-to-end. Region
    // sizes deliberately mix "fits SIMD chunks exactly" with "needs a scalar
    // tail" to catch boundary bugs.
    [Theory]
    [InlineData(32, 0, 0, 17, 13)]      // BITPIX=32 int, prime sizes -> tail loop
    [InlineData(32, 5, 7, 32, 16)]      // aligned vector chunks (32 % 4 == 0)
    [InlineData(32, 3, 11, 33, 9)]      // 33 % 4 = 1 -> mix vector + tail
    [InlineData(-32, 0, 0, 17, 13)]
    [InlineData(-32, 5, 7, 32, 16)]
    [InlineData(-32, 3, 11, 33, 9)]
    public void PartialReader_SimdPaths_MatchScalarForCrafted32Bit(int bitpix, int rx, int ry, int rw, int rh)
    {
        const int width = 64;
        const int height = 32;
        var path = Path.Combine(Path.GetTempPath(),
            $"TianWen.Lib.Tests_PartialFitsReader_synthetic_b{bitpix:+0;-0}_{width}x{height}.fits");
        if (File.Exists(path)) File.Delete(path);
        // Integer-valued ramp so BITPIX=32 (truncating to int32) and BITPIX=-32
        // (exact float32) both round-trip without loss.
        var values = new float[width * height];
        for (var i = 0; i < values.Length; i++) values[i] = i - 100f;
        WriteSyntheticFits(path, width, height, bitpix, values);

        using var partial = new PartialFitsReader(path);
        partial.BitPix.ShouldBe(bitpix);
        partial.Width.ShouldBe(width);
        partial.Height.ShouldBe(height);

        var dest = new float[rw * rh];
        partial.ReadRegion(new Rectangle(rx, ry, rw, rh), dest);

        for (var r = 0; r < rh; r++)
        {
            for (var c = 0; c < rw; c++)
            {
                var expected = values[(ry + r) * width + (rx + c)];
                dest[r * rw + c].ShouldBe(expected, tolerance: 1e-4f,
                    $"bitpix={bitpix} mismatch at ({rx + c}, {ry + r})");
            }
        }
    }

    // Minimal single-HDU FITS writer for tests: int32 (BITPIX=32) and
    // float32 (BITPIX=-32) only. Header is the bare set PartialFitsReader
    // parses (BITPIX, NAXIS, NAXIS1, NAXIS2). BZERO/BSCALE omitted -> default
    // 0/1 so values round-trip 1:1.
    private static void WriteSyntheticFits(string path, int width, int height, int bitpix, float[] values)
    {
        using var fs = File.Create(path);
        var header = new StringBuilder();
        void Card(string s) => header.Append(s.PadRight(80));
        Card("SIMPLE  =                    T");
        Card($"BITPIX  = {bitpix,20}");
        Card("NAXIS   =                    2");
        Card($"NAXIS1  = {width,20}");
        Card($"NAXIS2  = {height,20}");
        Card("END");
        // Pad header to 2880-byte block.
        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        var pad = (2880 - headerBytes.Length % 2880) % 2880;
        fs.Write(headerBytes);
        if (pad > 0) fs.Write(new byte[pad]);

        // Pixel data, big-endian.
        var bytesPerPixel = Math.Abs(bitpix) / 8;
        var data = new byte[values.Length * bytesPerPixel];
        for (var i = 0; i < values.Length; i++)
        {
            if (bitpix == 32)
            {
                BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(i * 4, 4), (int)values[i]);
            }
            else if (bitpix == -32)
            {
                var bits = BitConverter.SingleToInt32Bits(values[i]);
                BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(i * 4, 4), bits);
            }
            else
            {
                throw new ArgumentException($"unsupported test bitpix={bitpix}");
            }
        }
        fs.Write(data);
        // Pad data to 2880-byte block.
        var dataPad = (2880 - data.Length % 2880) % 2880;
        if (dataPad > 0) fs.Write(new byte[dataPad]);
    }

    private static async Task<TempFixture> ExtractFixtureToTempAsync()
    {
        await using var inStream = SharedTestData.OpenEmbeddedFileStream($"{FixtureName}.fits.gz")
            ?? throw new InvalidDataException($"Missing embedded fixture {FixtureName}.fits.gz");
        var tempPath = Path.Combine(Path.GetTempPath(), $"TianWen.Lib.Tests_PartialFitsReader_{FixtureName}.fits");
        if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
        {
            await using var gz = new GZipStream(inStream, CompressionMode.Decompress);
            await using var outStream = File.Create(tempPath);
            await gz.CopyToAsync(outStream);
        }
        return new TempFixture(tempPath);
    }

    private sealed class TempFixture(string path) : IDisposable
    {
        public string Path { get; } = path;
        public void Dispose() { /* leave the temp file behind; reused across tests */ }
    }
}
