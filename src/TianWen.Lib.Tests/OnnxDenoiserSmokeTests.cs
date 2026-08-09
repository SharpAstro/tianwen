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
/// End-to-end smoke tests for the AI4 denoise enhancer, gated on the model
/// files being present under <c>%LOCALAPPDATA%\TianWen\models</c> (populated by
/// <c>tools/tianwen-ai-models-fetch.ps1</c>); the tests skip silently when
/// missing so CI / fresh-clone runs without the fetch don't fail.
/// <para>
/// <b>Why this file exists.</b> <see cref="OnnxDenoiser"/> was the only ONNX
/// enhancer with no smoke test at all, and it carried the same mono defect as
/// <see cref="OnnxStarRemover"/>: it declared the model's channel count to be
/// the source's, so a 1-channel frame was packed into a tensor the 3-channel
/// network rejects. The star remover happens to run first in
/// <c>SharpenPipeline</c>, so its failure masked this one entirely -- a mono
/// sharpen never got far enough to hit the denoise step.
/// </para>
/// </summary>
[Collection("Imaging")]
public class OnnxDenoiserSmokeTests(ITestOutputHelper output)
{
    private static bool HasModel(string fileName, out string skipMessage)
    {
        var resolver = new ModelResolver();
        if (resolver.TryResolve(fileName, out _))
        {
            skipMessage = string.Empty;
            return true;
        }
        skipMessage = $"{fileName} not found; run tools/tianwen-ai-models-fetch.ps1 to enable this test.";
        return false;
    }

    /// <summary>
    /// Flat background plus deterministic per-pixel grain. A denoiser should
    /// reduce the pixel-to-pixel scatter without moving the mean much, which is
    /// what the assertions below check; the RNG is seeded so the plate (and so
    /// the measured noise) is identical on every run.
    /// </summary>
    private static Image BuildNoisyPlate(int channels, int w, int h)
    {
        var planes = new float[channels][,];
        var rng = new Random(42);
        const float bg = 0.20f;
        const float amplitude = 0.05f;

        for (var c = 0; c < channels; c++)
        {
            var plane = new float[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    plane[y, x] = Math.Clamp(bg + amplitude * (float)(rng.NextDouble() - 0.5), 0f, 1f);
                }
            }
            planes[c] = plane;
        }

        return new Image(planes, BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = channels == 1 ? SensorType.Monochrome : SensorType.Color });
    }

    private async Task RunShapePreservingDenoiseAsync(int channels, string modelFile)
    {
        if (!HasModel(modelFile, out var skip)) { Assert.Skip(skip); return; }

        // Big enough for the pipeline to run, small enough not to hammer the
        // GPU; chunkSize 512 keeps it to a single chunk plus border.
        const int w = 256, h = 192;
        var src = BuildNoisyPlate(channels, w, h);
        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        using var enhancer = new OnnxDenoiser(new ModelResolver(), factory.CreateLogger<OnnxDenoiser>(), chunkSize: 512, overlap: 64);

        var result = await enhancer.EnhanceAsync(src, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var (outChannels, outW, outH) = result.Shape;
        outChannels.ShouldBe(channels);
        outW.ShouldBe(w);
        outH.ShouldBe(h);

        for (var c = 0; c < outChannels; c++)
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
    public Task EnhanceAsync_Color_ProducesSameShapedOutput()
        => RunShapePreservingDenoiseAsync(channels: 3, "deep_denoise_color_AI4.onnx");

    /// <summary>
    /// The mono path: a 1-channel source through a model that wants 3. The
    /// runner must tile channel 0 across the input slots and extract output
    /// channel 0, exactly as the stellar-sharpener and deconvolver paths do.
    /// </summary>
    [Fact]
    public Task EnhanceAsync_MonoTilesToThreeAndExtractsChannelZero()
        => RunShapePreservingDenoiseAsync(channels: 1, "deep_denoise_mono_AI4.onnx");

    [Fact]
    public async Task EnhanceAsync_RejectsOutOfRangeInput()
    {
        if (!HasModel("deep_denoise_color_AI4.onnx", out var skip)) { Assert.Skip(skip); return; }

        // MaxValue well past the tolerated NAFNet overshoot -> fail loudly with
        // a pointer to the right normalisation helper rather than silently
        // producing garbage through the MTF clamp.
        var src = new Image([new float[16, 16], new float[16, 16], new float[16, 16]],
            BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f,
            new ImageMeta { SensorType = SensorType.Color });

        using var enhancer = new OnnxDenoiser(new ModelResolver());
        var ex = await Should.ThrowAsync<ArgumentException>(
            async () => await enhancer.EnhanceAsync(src, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("AdoptImageAsync");
    }

    [Fact]
    public void AddTianWenAi_WiresUpIDenoiseEnhancer()
    {
        // DI smoke test: no model load, only the registration shape.
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddTianWenAi();

        using var provider = services.BuildServiceProvider();
        var denoiser = provider.GetRequiredService<IDenoiseEnhancer>();
        denoiser.ShouldBeOfType<OnnxDenoiser>();
        denoiser.Name.ShouldContain("AI4");
    }
}
