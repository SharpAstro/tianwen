using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib.Tiff;
using ImageMagick;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Differential tests for the DIR.Lib <see cref="TiffWriter"/> output against Magick.NET as the
/// reference TIFF consumer. These guard the SampleFormat (tag 339) emission — without that tag,
/// float32 pixels would be silently misread as unsigned ints by libtiff. The "diff" tests write
/// the same content via both libraries and decode both via Magick.NET, so any Magick.NET-internal
/// pixel-range conventions are applied symmetrically and cancel out.
/// </summary>
[Collection("Imaging")]
public class TiffWriterMagickDiffTests(ITestOutputHelper testOutput)
{
    [Fact]
    public async Task Uint16Grayscale_DirLibOutput_RoundTripsViaMagickNet()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const int width = 8;
        const int height = 4;
        var input = BuildUint16Gradient(width, height);
        var bytes = PackUint16LittleEndian(input);

        var testDir = SharedTestData.CreateTempTestOutputDir();
        var tiffPath = Path.Combine(testDir, "dirlib_u16_gray.tiff");
        await WriteDirLibTiffAsync(tiffPath, bytes, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 1,
            BitsPerSample = 16,
            Photometric = TiffPhotometric.MinIsBlack,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = TiffCompression.Deflate,
        }, ct);

        using var image = new MagickImage(tiffPath);
        image.Width.ShouldBe((uint)width);
        image.Height.ShouldBe((uint)height);

        using var pixels = image.GetPixelsUnsafe();
        var decoded = pixels.GetArea(0, 0, (uint)width, (uint)height)
            ?? throw new InvalidOperationException("Magick.NET returned null pixel area.");
        var stride = image.HasAlpha ? 2 : 1;
        testOutput.WriteLine($"Magick.NET decoded {decoded.Length} samples, stride={stride}, colorSpace={image.ColorSpace}");

        // Q16 quantum-perfect round-trip: every input ushort comes back unchanged.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dec = decoded[(y * width + x) * stride];
            dec.ShouldBe(input[y * width + x], $"pixel ({x},{y})");
        }
    }

    [Fact]
    public async Task Float32Rgb_DirLibAndMagickNet_DecodeToEquivalentPixels()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const int width = 8;
        const int height = 4;
        // [0, 1] gradient — the convention Magick.NET assumes for float TIFFs that omit the
        // SMinSampleValue/SMaxSampleValue range tags. DIR.Lib's TiffWriter currently omits
        // those tags, so DIR.Lib outputs must be in [0, 1] to interop with Magick.NET.
        var inputUnit = BuildRgbGradient(width, height);
        var inputQuantum = new float[inputUnit.Length];
        for (var i = 0; i < inputUnit.Length; i++) inputQuantum[i] = inputUnit[i] * Quantum.Max;
        var bytes = PackFloatsLittleEndian(inputUnit);

        var testDir = SharedTestData.CreateTempTestOutputDir();

        // (A) DIR.Lib path: write IEEE-float bytes directly in [0, 1] file convention.
        var dirPath = Path.Combine(testDir, "dirlib_f32_rgb.tiff");
        await WriteDirLibTiffAsync(dirPath, bytes, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 3,
            BitsPerSample = 32,
            Photometric = TiffPhotometric.Rgb,
            SampleFormat = TiffSampleFormat.IeeeFloat,
            Compression = TiffCompression.Uncompressed,
        }, ct);

        // (B) Magick.NET path: in-memory pixels live in [0, Quantum.Max]; Magick.NET writes a
        // native float TIFF and (in Q16-HDRI) emits SMinSampleValue/SMaxSampleValue tags so the
        // file values still land in [0, 1].
        var magickPath = Path.Combine(testDir, "magick_f32_rgb.tiff");
        using (var image = new MagickImage(MagickColors.Black, (uint)width, (uint)height)
        {
            Format = MagickFormat.Tiff,
            Depth = 32,
            ColorType = ColorType.TrueColor,
        })
        {
            using (var pixels = image.GetPixelsUnsafe())
            {
                pixels.SetPixels(inputQuantum);
            }
            await image.WriteAsync(magickPath, MagickFormat.Tiff, ct);
        }
        testOutput.WriteLine($"DIR.Lib: {new FileInfo(dirPath).Length:N0} B   Magick.NET: {new FileInfo(magickPath).Length:N0} B");

        AssertReadEquivalent(dirPath, magickPath, width, height, tolerance: 2f);
    }

    [Fact]
    public async Task Float32Rgb_NormalisedFileWithSMinSMax_DecodesToQuantumRangeViaMagickNet()
    {
        // Magick.NET's own float TIFF write stores file values in [0, 1] and declares the
        // original dynamic range via SMinSampleValue=0 / SMaxSampleValue=Quantum.Max. On read,
        // libtiff multiplies file values by SMaxSampleValue, so the in-memory pixels come back
        // at [0, Quantum.Max].
        // This test verifies DIR.Lib can produce a byte-compatible output: normalised [0, 1]
        // file values + the same SMin/SMax tags → Magick.NET decodes to the input range.
        CancellationToken ct = TestContext.Current.CancellationToken;
        const int width = 8;
        const int height = 4;
        var quantumInput = new float[width * height * 3];
        var fileValues = BuildRgbGradient(width, height); // unit-range [0, 1]
        for (var i = 0; i < quantumInput.Length; i++) quantumInput[i] = fileValues[i] * Quantum.Max;
        var bytes = PackFloatsLittleEndian(fileValues);

        var testDir = SharedTestData.CreateTempTestOutputDir();
        var dirPath = Path.Combine(testDir, "dirlib_f32_rgb_quantum.tiff");
        await WriteDirLibTiffAsync(dirPath, bytes, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 3,
            BitsPerSample = 32,
            Photometric = TiffPhotometric.Rgb,
            SampleFormat = TiffSampleFormat.IeeeFloat,
            SMinSampleValue = 0f,
            SMaxSampleValue = Quantum.Max,
            Compression = TiffCompression.Deflate,
        }, ct);

        using var image = new MagickImage(dirPath);
        image.Width.ShouldBe((uint)width);
        image.Height.ShouldBe((uint)height);

        using var pixels = image.GetPixelsUnsafe();
        var decoded = pixels.GetArea(0, 0, (uint)width, (uint)height)
            ?? throw new InvalidOperationException("Magick.NET returned null pixel area.");
        var stride = image.HasAlpha ? 4 : 3;
        testOutput.WriteLine($"Magick.NET decoded {decoded.Length} samples, stride={stride}");

        var maxDiff = 0f;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            for (var c = 0; c < 3; c++)
            {
                var src = quantumInput[(y * width + x) * 3 + c];
                var dec = decoded[(y * width + x) * stride + c];
                var diff = MathF.Abs(src - dec);
                if (diff > maxDiff) maxDiff = diff;
            }
        }
        testOutput.WriteLine($"Max R/G/B pixel diff: {maxDiff:F3}");
        // 1 quantum tolerance for float rounding through libtiff's read path.
        maxDiff.ShouldBeLessThan(1f);
    }

    [Fact]
    public async Task Uint16Rgb_DirLibAndMagickNet_DecodeToEquivalentPixels()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const int width = 8;
        const int height = 4;
        var input = BuildRgbUint16Gradient(width, height);
        var bytes = PackUint16LittleEndian(input);

        var testDir = SharedTestData.CreateTempTestOutputDir();

        // (A) DIR.Lib path with default SampleFormat=Uint.
        var dirPath = Path.Combine(testDir, "dirlib_u16_rgb.tiff");
        await WriteDirLibTiffAsync(dirPath, bytes, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 3,
            BitsPerSample = 16,
            Photometric = TiffPhotometric.Rgb,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = TiffCompression.Deflate,
        }, ct);

        // (B) Magick.NET path: 16-bit native TIFF write.
        var magickPath = Path.Combine(testDir, "magick_u16_rgb.tiff");
        using (var image = new MagickImage(MagickColors.Black, (uint)width, (uint)height)
        {
            Format = MagickFormat.Tiff,
            Depth = 16,
            ColorType = ColorType.TrueColor,
        })
        {
            var asFloat = new float[input.Length];
            for (var i = 0; i < input.Length; i++) asFloat[i] = input[i];
            using (var pixels = image.GetPixelsUnsafe())
            {
                pixels.SetPixels(asFloat);
            }
            await image.WriteAsync(magickPath, MagickFormat.Tiff, ct);
        }

        AssertReadEquivalent(dirPath, magickPath, width, height, tolerance: 1f);
    }

    private void AssertReadEquivalent(string pathA, string pathB, int width, int height, float tolerance)
    {
        using var aImage = new MagickImage(pathA);
        using var bImage = new MagickImage(pathB);
        aImage.Width.ShouldBe(bImage.Width);
        aImage.Height.ShouldBe(bImage.Height);

        using var aPixels = aImage.GetPixelsUnsafe();
        using var bPixels = bImage.GetPixelsUnsafe();
        var aData = aPixels.GetArea(0, 0, (uint)width, (uint)height)
            ?? throw new InvalidOperationException("Magick.NET returned null pixel area for A.");
        var bData = bPixels.GetArea(0, 0, (uint)width, (uint)height)
            ?? throw new InvalidOperationException("Magick.NET returned null pixel area for B.");
        var aStride = aImage.HasAlpha ? 4 : 3;
        var bStride = bImage.HasAlpha ? 4 : 3;
        testOutput.WriteLine($"A: stride={aStride} length={aData.Length}   B: stride={bStride} length={bData.Length}");

        // Compare R, G, B components only — ignore alpha, which Magick.NET may attach asymmetrically.
        var maxDiff = 0f;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            for (var c = 0; c < 3; c++)
            {
                var av = aData[(y * width + x) * aStride + c];
                var bv = bData[(y * width + x) * bStride + c];
                var diff = MathF.Abs(av - bv);
                if (diff > maxDiff) maxDiff = diff;
            }
        }
        testOutput.WriteLine($"Max R/G/B pixel diff between DIR.Lib and Magick.NET: {maxDiff:F3}");
        maxDiff.ShouldBeLessThan(tolerance);
    }

    private static async Task WriteDirLibTiffAsync(string path, ReadOnlyMemory<byte> pixels, int width, int height,
        TiffPageOptions options, CancellationToken ct)
    {
        await using var fs = File.Create(path);
        await using var writer = TiffWriter.Create(fs);
        await writer.AddPageAsync(pixels, width, height, options, ct);
        await writer.FlushAsync(ct);
    }

    private static ushort[] BuildUint16Gradient(int w, int h)
    {
        var pixels = new ushort[w * h];
        var maxIdx = w * h - 1;
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (ushort)((long)i * ushort.MaxValue / maxIdx);
        return pixels;
    }

    private static float[] BuildRgbGradient(int w, int h)
    {
        // R = x-gradient, G = y-gradient, B = checker — three distinct patterns so any
        // channel-order mistake is immediately visible in the diff. Values are in [0,1]
        // because Magick.NET's float-TIFF reader assumes SMPTE/scene-linear [0,1] range
        // when SMinSampleValue/SMaxSampleValue (tags 340/341) are absent — which DIR.Lib's
        // TiffWriter does not yet emit.
        var pixels = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 3;
            pixels[i + 0] = (float)x / (w - 1);
            pixels[i + 1] = (float)y / (h - 1);
            pixels[i + 2] = ((x + y) % 2 == 0) ? 1f : 0f;
        }
        return pixels;
    }

    private static ushort[] BuildRgbUint16Gradient(int w, int h)
    {
        var pixels = new ushort[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 3;
            pixels[i + 0] = (ushort)((long)x * ushort.MaxValue / Math.Max(1, w - 1));
            pixels[i + 1] = (ushort)((long)y * ushort.MaxValue / Math.Max(1, h - 1));
            pixels[i + 2] = ((x + y) % 2 == 0) ? ushort.MaxValue : (ushort)0;
        }
        return pixels;
    }

    private static byte[] PackFloatsLittleEndian(ReadOnlySpan<float> values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        return bytes;
    }

    private static byte[] PackUint16LittleEndian(ReadOnlySpan<ushort> values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), values[i]);
        return bytes;
    }
}
