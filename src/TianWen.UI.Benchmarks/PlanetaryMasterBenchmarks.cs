using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Per-publish display cost of the live stacked master: the wavelet sharpen (<see cref="Sharpen"/>) and the
/// adopt-into-a-stats-document (<see cref="Adopt"/>) that run every time a master is published (a slider
/// drag re-runs both). These are the floor on how fast a wavelet-slider adjustment can show -- distinct from
/// the window stack cost in <see cref="PlanetaryStackBenchmarks"/>. Synthetic RGB master (textured disk).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class PlanetaryMasterBenchmarks
{
    [Params(512, 1024)]
    public int Size;

    private Image _master = null!;
    private WaveletSharpenOptions _sharpen = null!;

    [GlobalSetup]
    public void Setup()
    {
        _master = PlanetaryBenchData.RgbMaster(Size);
        _sharpen = WaveletSharpenOptions.PlanetaryDefault;
    }

    [Benchmark(Description = "Wavelet sharpen (6-scale a-trous, RGB)")]
    public int Sharpen()
    {
        var sharpened = WaveletSharpen.Sharpen(_master, _sharpen);
        return sharpened.Width;
    }

    [Benchmark(Description = "Adopt master into stats-bearing document (stretch stats)")]
    public async Task<int> Adopt()
    {
        // AdoptImageAsync normalises in place, so hand it a throwaway clone each iteration.
        var doc = await AstroImageDocument.AdoptImageAsync(PlanetaryBenchData.Clone(_master), DebayerAlgorithm.None);
        return ((IPreviewSource)doc).Width;
    }
}
