using System;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// End-to-end smoke tests for the AI4 star removal pipeline. Gated on the
/// model file being present under <c>%LOCALAPPDATA%\TianWen\models</c>
/// (populated by <c>tools/tianwen-ai-models-fetch.ps1</c>); the tests skip
/// silently when missing so CI / fresh-clone runs without the fetch don't
/// fail.
/// </summary>
[Collection("Imaging")]
public class OnnxStarRemoverSmokeTests(ITestOutputHelper output)
{
    private static bool HasColorModel(out string skipMessage)
    {
        var resolver = new ModelResolver();
        if (resolver.TryResolve("darkstar_color_AI4.onnx", out _))
        {
            skipMessage = string.Empty;
            return true;
        }
        skipMessage = "darkstar_color_AI4.onnx not found; run tools/tianwen-ai-models-fetch.ps1 to enable this test.";
        return false;
    }

    private static Image BuildSyntheticRgbWithStars(int w, int h)
    {
        // 3-channel synthetic plate: low-key sky background plus a handful of
        // Gaussian "stars". Constant background means a well-functioning star
        // remover should produce something close to uniform after the
        // pipeline runs. Values in [0, 1] as required by OnnxStarRemover.
        var r = new float[h, w];
        var g = new float[h, w];
        var b = new float[h, w];
        const float bg = 0.10f;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                r[y, x] = bg;
                g[y, x] = bg;
                b[y, x] = bg;
            }
        }

        // Drop a few Gaussian stars at deterministic positions.
        (int X, int Y, float Sigma, float Peak)[] stars =
        [
            (w / 4, h / 4, 1.8f, 0.8f),
            (3 * w / 4, h / 4, 2.0f, 0.7f),
            (w / 2, h / 2, 1.5f, 0.9f),
            (w / 3, 2 * h / 3, 1.6f, 0.6f),
            (2 * w / 3, 3 * h / 4, 2.2f, 0.5f),
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
                    r[y, x] = MathF.Min(1f, r[y, x] + weight);
                    g[y, x] = MathF.Min(1f, g[y, x] + weight);
                    b[y, x] = MathF.Min(1f, b[y, x] + weight);
                }
            }
        }

        return new Image([r, g, b], BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.Color });
    }

    [Fact]
    public async Task EnhanceAsync_ProducesSameShapedOutput()
    {
        if (!HasColorModel(out var skip)) { Assert.Skip(skip); return; }

        // Big enough that the pipeline runs, small enough that the test
        // doesn't hammer the GPU. With chunkSize=512 (overriding default 256)
        // the whole image fits in a single chunk + border -- exercises the
        // stretch/infer/unstretch path without multi-chunk stitching.
        const int w = 256, h = 192;
        var src = BuildSyntheticRgbWithStars(w, h);
        // Wire a real logger so the timing breakdown shows up in test output.
        // Verifies the LogInformation call lands and lets us eyeball the
        // per-phase numbers when re-running the smoke test.
        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        using var enhancer = new OnnxStarRemover(new ModelResolver(), factory.CreateLogger<OnnxStarRemover>(), chunkSize: 512, overlap: 64);

        var result = await enhancer.EnhanceAsync(src, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var (channels, outW, outH) = result.Shape;
        channels.ShouldBe(3);
        outW.ShouldBe(w);
        outH.ShouldBe(h);

        // No NaN / Inf in the output.
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
    public void AddTianWenAi_WiresUpIStarRemover()
    {
        // DI smoke test: AddTianWenAi must register a working IStarRemover.
        // No model load here -- this only verifies the registration shape.
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddTianWenAi();

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IModelResolver>();
        resolver.ShouldBeOfType<ModelResolver>();
        var remover = provider.GetRequiredService<IStarRemover>();
        remover.ShouldBeOfType<OnnxStarRemover>();
        remover.Name.ShouldContain("AI4");
    }

    [Fact]
    public async Task EnhanceAsync_RejectsOutOfRangeInput()
    {
        if (!HasColorModel(out var skip)) { Assert.Skip(skip); return; }

        // MaxValue > 1.0 -> we should fail loudly with a pointer to the
        // right normalisation helper instead of silently producing garbage
        // via the MTF clamp.
        var r = new float[16, 16];
        var g = new float[16, 16];
        var b = new float[16, 16];
        var src = new Image([r, g, b], BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f,
            new ImageMeta { SensorType = SensorType.Color });

        using var enhancer = new OnnxStarRemover(new ModelResolver());
        var ex = await Should.ThrowAsync<ArgumentException>(
            async () => await enhancer.EnhanceAsync(src));
        ex.Message.ShouldContain("AdoptImageAsync");
    }
}
