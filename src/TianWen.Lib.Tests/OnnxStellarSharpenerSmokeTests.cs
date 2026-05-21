using System;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// End-to-end smoke tests for the AI4 stellar-sharpening pipeline. Gated on
/// <c>deep_sharp_stellar_AI4.onnx</c> being present under
/// <c>%LOCALAPPDATA%\TianWen\models</c>; the tests skip silently when
/// missing.
/// </summary>
[Collection("Imaging")]
public class OnnxStellarSharpenerSmokeTests(ITestOutputHelper output)
{
    private static bool HasStellarModel(out string skipMessage)
    {
        var resolver = new ModelResolver();
        if (resolver.TryResolve("deep_sharp_stellar_AI4.onnx", out _))
        {
            skipMessage = string.Empty;
            return true;
        }
        skipMessage = "deep_sharp_stellar_AI4.onnx not found; run tools/tianwen-ai-models-fetch.ps1 to enable this test.";
        return false;
    }

    private static Image BuildSyntheticStarsOnly(int channels, int w, int h)
    {
        // A few Gaussian "stars" on a near-zero background -- representative
        // of the stars-only plate the sharpener consumes in the canonical
        // pipeline (Source - Starless). Values in [0, 1] as required.
        var data = new float[channels][,];
        for (var c = 0; c < channels; c++) data[c] = new float[h, w];

        (int X, int Y, float Sigma, float Peak)[] stars =
        [
            (w / 4, h / 4, 2.0f, 0.75f),
            (3 * w / 4, h / 4, 1.8f, 0.65f),
            (w / 2, h / 2, 1.5f, 0.90f),
            (w / 3, 2 * h / 3, 1.6f, 0.55f),
            (2 * w / 3, 3 * h / 4, 2.2f, 0.50f),
        ];
        foreach (var (sx, sy, sigma, peak) in stars)
        {
            for (var dy = -8; dy <= 8; dy++)
            {
                var y = sy + dy;
                if ((uint)y >= (uint)h) continue;
                for (var dx = -8; dx <= 8; dx++)
                {
                    var x = sx + dx;
                    if ((uint)x >= (uint)w) continue;
                    var weight = peak * MathF.Exp(-(dx * dx + dy * dy) / (2f * sigma * sigma));
                    for (var c = 0; c < channels; c++)
                    {
                        data[c][y, x] = MathF.Min(1f, data[c][y, x] + weight);
                    }
                }
            }
        }

        return new Image(data, BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = channels == 1 ? SensorType.Monochrome : SensorType.Color });
    }

    [Fact]
    public async Task EnhanceAsync_RgbProducesSameShapedOutput()
    {
        if (!HasStellarModel(out var skip)) { Assert.Skip(skip); return; }

        const int w = 256, h = 192;
        var src = BuildSyntheticStarsOnly(channels: 3, w, h);
        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        using var enhancer = new OnnxStellarSharpener(new ModelResolver(), factory.CreateLogger<OnnxStellarSharpener>(), chunkSize: 512, overlap: 64);

        var result = await enhancer.EnhanceAsync(src, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var (channels, outW, outH) = result.Shape;
        channels.ShouldBe(3);
        outW.ShouldBe(w);
        outH.ShouldBe(h);

        for (var c = 0; c < channels; c++)
        {
            var span = result.GetChannelSpan(c);
            for (var i = 0; i < span.Length; i++)
            {
                float.IsFinite(span[i]).ShouldBeTrue($"non-finite at c={c} index={i}: {span[i]}");
            }
        }

        result.Release();
    }

    [Fact]
    public async Task EnhanceAsync_MonoTilesToThreeAndExtractsChannelZero()
    {
        // Mono path: source has 1 channel, the model takes 3, so the impl
        // must tile across the 3 input slots and extract channel 0 of the
        // 3-channel output. Verifies that path end-to-end.
        if (!HasStellarModel(out var skip)) { Assert.Skip(skip); return; }

        const int w = 256, h = 192;
        var src = BuildSyntheticStarsOnly(channels: 1, w, h);
        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        using var enhancer = new OnnxStellarSharpener(new ModelResolver(), factory.CreateLogger<OnnxStellarSharpener>(), chunkSize: 512, overlap: 64);

        var result = await enhancer.EnhanceAsync(src, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var (channels, outW, outH) = result.Shape;
        channels.ShouldBe(1);
        outW.ShouldBe(w);
        outH.ShouldBe(h);

        var span = result.GetChannelSpan(0);
        for (var i = 0; i < span.Length; i++)
        {
            float.IsFinite(span[i]).ShouldBeTrue($"non-finite at index={i}: {span[i]}");
        }

        result.Release();
    }

    [Fact]
    public void AddTianWenAi_WiresUpIStellarSharpener()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTianWenAi();

        using var provider = services.BuildServiceProvider();
        var sharpener = provider.GetRequiredService<IStellarSharpener>();
        sharpener.ShouldBeOfType<OnnxStellarSharpener>();
        sharpener.Name.ShouldContain("AI4");
    }

    [Fact]
    public async Task EnhanceAsync_RejectsOutOfRangeInput()
    {
        if (!HasStellarModel(out var skip)) { Assert.Skip(skip); return; }

        var r = new float[16, 16];
        var g = new float[16, 16];
        var b = new float[16, 16];
        var src = new Image([r, g, b], BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f,
            new ImageMeta { SensorType = SensorType.Color });

        using var enhancer = new OnnxStellarSharpener(new ModelResolver());
        var ex = await Should.ThrowAsync<ArgumentException>(
            async () => await enhancer.EnhanceAsync(src, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("AdoptImageAsync");
    }
}
