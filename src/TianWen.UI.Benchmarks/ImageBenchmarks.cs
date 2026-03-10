using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class ImageBenchmarks
{
    private Image _monoImage = null!;
    private Image _colorImage = null!;
    private float[] _monoPedestals = null!;
    private float[] _colorPedestals = null!;

    [Params(1280, 4096)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);

        // Create a realistic mono image: background ~500 ADU with noise + a few bright spots
        var monoData = CreateChannelData(1, Size, Size);
        FillRealisticChannel(monoData[0], rng, background: 500f, noise: 30f);
        _monoImage = new Image(monoData, BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta("", default, default, FrameType.Light, "", 0, 0, 0, 0, default, 1, 1, float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, 0f, 0f));

        // Create a realistic 3-channel color image
        var colorData = CreateChannelData(3, Size, Size);
        FillRealisticChannel(colorData[0], rng, background: 520f, noise: 28f);
        FillRealisticChannel(colorData[1], rng, background: 480f, noise: 32f);
        FillRealisticChannel(colorData[2], rng, background: 460f, noise: 35f);
        _colorImage = new Image(colorData, BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta("", default, default, FrameType.Light, "", 0, 0, 0, 0, default, 1, 1, float.NaN, SensorType.Color, 0, 0, RowOrder.TopDown, 0f, 0f));

        // Pre-compute pedestals (as the real pipeline does)
        _monoPedestals = [_monoImage.GetPedestralMedianAndMADScaledToUnit(0).Pedestral];
        _colorPedestals =
        [
            _colorImage.GetPedestralMedianAndMADScaledToUnit(0).Pedestral,
            _colorImage.GetPedestralMedianAndMADScaledToUnit(1).Pedestral,
            _colorImage.GetPedestralMedianAndMADScaledToUnit(2).Pedestral,
        ];
    }

    private static float[][,] CreateChannelData(int channelCount, int height, int width)
    {
        var channels = new float[channelCount][,];
        for (var c = 0; c < channelCount; c++)
        {
            channels[c] = new float[height, width];
        }
        return channels;
    }

    private static void FillRealisticChannel(float[,] channel, Random rng, float background, float noise)
    {
        var height = channel.GetLength(0);
        var width = channel.GetLength(1);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Gaussian-ish noise via Box-Muller
                var u1 = (float)rng.NextDouble();
                var u2 = (float)rng.NextDouble();
                var z = MathF.Sqrt(-2f * MathF.Log(u1 + 1e-10f)) * MathF.Cos(2f * MathF.PI * u2);
                channel[y, x] = Math.Clamp(background + noise * z, 0f, 65535f);
            }
        }

        // Sprinkle some bright stars
        for (var i = 0; i < 50; i++)
        {
            var cx = rng.Next(20, width - 20);
            var cy = rng.Next(20, height - 20);
            var brightness = 5000f + (float)rng.NextDouble() * 55000f;
            var radius = 2 + rng.Next(5);

            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var dist2 = dx * dx + dy * dy;
                    if (dist2 <= radius * radius)
                    {
                        var falloff = 1f - (float)dist2 / (radius * radius);
                        var py = cy + dy;
                        var px = cx + dx;
                        if (py >= 0 && py < height && px >= 0 && px < width)
                        {
                            channel[py, px] = Math.Clamp(channel[py, px] + brightness * falloff, 0f, 65535f);
                        }
                    }
                }
            }
        }
    }

    // --- Histogram / Statistics ---

    [Benchmark]
    public ImageHistogram Histogram_Mono() => _monoImage.Histogram(0);

    [Benchmark]
    public ImageHistogram Statistics_Mono() => _monoImage.Statistics(0);

    [Benchmark]
    public ImageHistogram Statistics_Color_Ch0() => _colorImage.Statistics(0);

    [Benchmark]
    public (float, float, float) GetPedestralMedianMAD_Mono() => _monoImage.GetPedestralMedianAndMADScaledToUnit(0);

    // --- Background Scan ---

    [Benchmark]
    public (float[], float) ScanBackground_Mono() => _monoImage.ScanBackgroundRegion(_monoPedestals);

    [Benchmark]
    public (float[], float) ScanBackground_Color() => _colorImage.ScanBackgroundRegion(_colorPedestals);

    // --- Background (peak histogram) ---

    [Benchmark]
    public (float, float, float, float) Background_Mono() => _monoImage.Background(0);

    [Benchmark]
    public (float, float, float, float) Background_Color_Ch0() => _colorImage.Background(0);

    // --- Stretch ---

    [Benchmark]
    public Task<Image> StretchLinked_Color() => _colorImage.StretchLinkedAsync(0.15, -5.0, DebayerAlgorithm.None);

    [Benchmark]
    public Task<Image> StretchUnlinked_Color() => _colorImage.StretchUnlinkedAsync(0.15, -5.0, DebayerAlgorithm.None);

    [Benchmark]
    public Task<Image> StretchLuma_Color() => _colorImage.StretchLumaAsync(0.15, -5.0, DebayerAlgorithm.None);

    [Benchmark]
    public Task<Image> StretchUnlinked_Mono() => _monoImage.StretchUnlinkedAsync(0.15, -5.0, DebayerAlgorithm.None);

    // --- Star Detection ---

    [Benchmark]
    public Task<StarList> FindStars_Mono() => _monoImage.FindStarsAsync(0, snrMin: 10f, maxStars: 500);

    [Benchmark]
    public Task<StarList> FindStars_Color_Ch0() => _colorImage.FindStarsAsync(0, snrMin: 10f, maxStars: 500);
}
