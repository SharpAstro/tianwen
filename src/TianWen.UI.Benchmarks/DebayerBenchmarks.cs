using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Per-frame <see cref="Image.DebayerAsync"/> cost across the three CFA-debayer
/// algorithms, run against a real 3008x3008 IMX533M Bayer frame (the same
/// fixture <see cref="FindStarsBenchmarks"/> uses). Captures the wall-clock
/// gap that surfaced during the stacking debayer-split investigation: switching
/// the stack-warp pass from VNG to AHD made integrated wall-clock on a
/// 13-frame group go from 23 s to 100 s -- and the debayer step alone went
/// from 261 ms/frame to 5728 ms/frame (~22x). VNG vs BilinearMono is a more
/// modest ratio but BilinearMono's 2x2-cell average gives centroids that are
/// half-a-pixel offset from VNG's per-channel positions, which is why we use
/// VNG for both the centroid pass and the stack pass in
/// <c>StackingEndToEndManualTest</c> -- mixing them broke 2-frame group
/// plate-solving.
///
/// <para>What this bench is for: keep the slowdown visible so when someone
/// considers flipping <c>StackDebayerAlg = AHD</c> for better color
/// reconstruction (or as part of a future AHD-perf-improvement PR) we have
/// a calibrated cost number to weigh against the quality bump.</para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class DebayerBenchmarks
{
    // 3008x3008 OSC Bayer frame (RGGB), 60s exposure. Loaded once in setup;
    // each benchmark iteration allocates its own debayered output Image, so
    // GC + alloc cost is measured per call.
    private Image _raw = null!;

    [GlobalSetup]
    public void Setup()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "FITS", "imx533_sv605cc_60s_real.fits.gz");
        // Decompress to a temp file so TryReadFitsFile(string) handles parsing.
        var tempPath = Path.Combine(Path.GetTempPath(), "TianWen.UI.Benchmarks_debayer_raw.fits");
        if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
        {
            using var fileStream = File.OpenRead(path);
            using var gz = new GZipStream(fileStream, CompressionMode.Decompress);
            using var outStream = File.Create(tempPath);
            gz.CopyTo(outStream);
        }
        if (!Image.TryReadFitsFile(tempPath, out var raw))
        {
            throw new InvalidDataException($"Failed to parse FITS at {tempPath}");
        }
        _raw = raw;
    }

    /// <summary>BilinearMono: 2x2 CFA cell average -> 1 channel output.
    /// Fastest path; centroid coordinates differ from VNG/AHD by ~0.5 px.</summary>
    [Benchmark(Baseline = true)]
    public async Task<Image> BilinearMono() =>
        await _raw.DebayerAsync(DebayerAlgorithm.BilinearMono);

    /// <summary>VNG: variable-number-of-gradients per pixel -> 3 channel output.
    /// The default in the stacking pipeline (both centroid and stack passes).</summary>
    [Benchmark]
    public async Task<Image> VNG() =>
        await _raw.DebayerAsync(DebayerAlgorithm.VNG);

    /// <summary>AHD: adaptive homogeneity-directed -> 3 channel output. Best
    /// color fidelity + sharpest stars; ~20x slower than VNG on this hardware
    /// when the stacking pipeline ran the comparison end-to-end.</summary>
    [Benchmark]
    public async Task<Image> AHD() =>
        await _raw.DebayerAsync(DebayerAlgorithm.AHD);
}
