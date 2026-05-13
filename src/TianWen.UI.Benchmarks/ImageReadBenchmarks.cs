using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using SharpAstro.Tiff;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// File-decode timings for the four formats <see cref="Image.TryReadImageFile"/>
/// accepts (TIFF / CR2 / CR3 / FITS). Each benchmark exercises the production
/// import path end-to-end — open file, decode pixels, build <see cref="Image"/>
/// + <see cref="ImageMeta"/> — so the result is what a GUI / Session caller
/// actually pays at import time.
///
/// <para>OS file cache warms up after the first iteration, so disk seek is
/// effectively excluded from the steady-state measurement. The interesting
/// signal is decode CPU time + per-format allocation churn; absolute disk
/// throughput is not what we're after.</para>
///
/// <para>Fixtures: the M50 CR2/CR3 + R5 CR3 LFS files from
/// <c>TianWen.Lib.Tests/Data/</c> are linked into <c>Fixtures/</c> at the
/// bench output. The TIFF case writes a synthetic 16-bit mono TIFF in
/// <see cref="GlobalSetup"/> so the benchmark doesn't need a committed
/// TIFF fixture and the file size stays predictable across machines.</para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ImageReadBenchmarks
{
    private string _tiffPath = null!;
    private string _cr2Path = null!;
    private string _cr3CrawLossyPath = null!;
    private string _tempDir = null!;

    /// <summary>Synthetic TIFF dimensions. 4096x4096 16-bit single-channel
    /// is ~33 MB on disk — comparable to a full-frame APS-C TIFF export
    /// from an astro stacking pipeline.</summary>
    private const int TiffWidth = 4096;
    private const int TiffHeight = 4096;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TianWen.UI.Benchmarks", "ImageReadBenchmarks");
        Directory.CreateDirectory(_tempDir);

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        _cr2Path = Path.Combine(fixturesDir, "CR2", "_MG_7578.CR2");
        // Only the R5 cRAW lives in TianWen.Lib.Tests/Data/CR3/ today.
        // The M50 RAW (encType=0 levels=0) + M50 CRAW (encType=0 levels=3
        // lossless wavelet) fixtures sit in FC.SDK.Raw.Tests/Fixtures/ —
        // link those in too if separate numbers are wanted for each path.
        _cr3CrawLossyPath = Path.Combine(fixturesDir, "CR3", "Canon_EOS_R5_CRAW.CR3");

        _tiffPath = Path.Combine(_tempDir, $"synthetic_{TiffWidth}x{TiffHeight}_uint16.tif");
        if (!File.Exists(_tiffPath))
        {
            WriteSyntheticTiffAsync(_tiffPath, TiffWidth, TiffHeight).GetAwaiter().GetResult();
        }
    }

    /// <summary>Writes a deterministic 16-bit mono TIFF with a cheap
    /// ramp+xor pattern (distinct per-pixel values, no PRNG state). Uses
    /// DIR.Lib's <see cref="TiffWriter"/> — same writer the production
    /// export path uses — so the file is exactly what the read path would
    /// otherwise produce + consume.</summary>
    private static async Task WriteSyntheticTiffAsync(string path, int width, int height)
    {
        var pixels = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = (ushort)(((x * 17) ^ (y * 31)) & 0xFFFF);
            }
        }
        var bytes = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
        await using var fs = File.Create(path);
        await using var writer = TiffWriter.Create(fs);
        await writer.AddPageAsync(bytes, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 1,
            BitsPerSample = 16,
            Photometric = TiffPhotometric.MinIsBlack,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = TiffCompression.Deflate,
        });
        await writer.FlushAsync();
    }

    [Benchmark(Description = "Read 4096x4096 16-bit mono TIFF via DIR.Lib")]
    public Image? ReadTiff()
    {
        Image.TryReadImageFile(_tiffPath, out var image);
        return image;
    }

    [Benchmark(Description = "Read Canon EOS 6D CR2 (5568x3708 14-bit RGGB) via FC.SDK.Raw")]
    public Image? ReadCr2()
    {
        Image.TryReadImageFile(_cr2Path, out var image);
        return image;
    }

    [Benchmark(Description = "Read Canon EOS R5 cRAW CR3 (5248x3510 14-bit RGGB + FF13 quantization) via FC.SDK.Raw")]
    public Image? ReadCr3CrawLossy()
    {
        Image.TryReadImageFile(_cr3CrawLossyPath, out var image);
        return image;
    }
}
