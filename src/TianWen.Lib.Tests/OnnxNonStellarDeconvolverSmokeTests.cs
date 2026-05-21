using System;
using System.Threading;
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
/// End-to-end smoke tests for the AI4 non-stellar PSF-conditional
/// deconvolver. Gated on <c>deep_nonstellar_sharp_conditional_psf_AI4.onnx</c>
/// being present under <c>%LOCALAPPDATA%\TianWen\models</c>; tests skip
/// silently when missing.
/// </summary>
[Collection("Imaging")]
public class OnnxNonStellarDeconvolverSmokeTests(ITestOutputHelper output)
{
    private static bool HasDeconvModel(out string skipMessage)
    {
        var resolver = new ModelResolver();
        if (resolver.TryResolve("deep_nonstellar_sharp_conditional_psf_AI4.onnx", out _))
        {
            skipMessage = string.Empty;
            return true;
        }
        skipMessage = "deep_nonstellar_sharp_conditional_psf_AI4.onnx not found; run tools/tianwen-ai-models-fetch.ps1 to enable this test.";
        return false;
    }

    /// <summary>
    /// Synthetic starless-like plate: smooth nebula-ish gradient plus low-key
    /// noise, no stars (so the HfdPsfEstimator falls back to default radius
    /// 3.0 px / psf01 ≈ 0.528 -- the canonical "no usable stars" path).
    /// </summary>
    private static Image BuildSyntheticStarless(int channels, int w, int h)
    {
        var data = new float[channels][,];
        for (var c = 0; c < channels; c++) data[c] = new float[h, w];

        var rng = new Random(42);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // Smooth radial-ish "nebula" gradient, peak in the middle.
                var dx = (float)(x - w / 2) / w;
                var dy = (float)(y - h / 2) / h;
                var bg = 0.10f + 0.30f * MathF.Exp(-(dx * dx + dy * dy) * 6f);
                var noise = (float)(rng.NextDouble() - 0.5) * 0.02f;
                var v = MathF.Min(1f, MathF.Max(0f, bg + noise));
                for (var c = 0; c < channels; c++) data[c][y, x] = v;
            }
        }

        return new Image(data, BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = channels == 1 ? SensorType.Monochrome : SensorType.Color });
    }

    private sealed class FixedPsfEstimator(float psf01) : IPsfEstimator
    {
        public Task<float> EstimateAsync(Image image, CancellationToken cancellationToken = default)
            => Task.FromResult(psf01);
    }

    [Fact]
    public async Task EnhanceAsync_RgbProducesSameShapedOutput()
    {
        if (!HasDeconvModel(out var skip)) { Assert.Skip(skip); return; }

        const int w = 256, h = 192;
        var src = BuildSyntheticStarless(channels: 3, w, h);
        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        // Inject a fixed PSF estimator so the test doesn't depend on
        // FindStarsAsync's behaviour on synthetic starless data. 3.0 px ->
        // psf01 ≈ 0.528 (HfdPsfEstimator.EncodeRadiusToPsf01(3.0f)).
        var psf01 = HfdPsfEstimator.EncodeRadiusToPsf01(HfdPsfEstimator.DefaultRadiusPx);
        using var deconv = new OnnxNonStellarDeconvolver(
            new ModelResolver(), new FixedPsfEstimator(psf01),
            factory.CreateLogger<OnnxNonStellarDeconvolver>(), chunkSize: 512, overlap: 64);

        var result = await deconv.EnhanceAsync(src, TestContext.Current.CancellationToken);

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
        if (!HasDeconvModel(out var skip)) { Assert.Skip(skip); return; }

        const int w = 256, h = 192;
        var src = BuildSyntheticStarless(channels: 1, w, h);
        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        var psf01 = HfdPsfEstimator.EncodeRadiusToPsf01(HfdPsfEstimator.DefaultRadiusPx);
        using var deconv = new OnnxNonStellarDeconvolver(
            new ModelResolver(), new FixedPsfEstimator(psf01),
            factory.CreateLogger<OnnxNonStellarDeconvolver>(), chunkSize: 512, overlap: 64);

        var result = await deconv.EnhanceAsync(src, TestContext.Current.CancellationToken);

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
    public void AddTianWenAi_WiresUpINonStellarDeconvolver()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTianWenAi();

        using var provider = services.BuildServiceProvider();
        var deconv = provider.GetRequiredService<INonStellarDeconvolver>();
        deconv.ShouldBeOfType<OnnxNonStellarDeconvolver>();
        deconv.Name.ShouldContain("AI4");

        // The PSF estimator dep is also registered now.
        var psf = provider.GetRequiredService<IPsfEstimator>();
        psf.ShouldBeOfType<HfdPsfEstimator>();
    }

    [Fact]
    public async Task EnhanceAsync_RejectsOutOfRangeInput()
    {
        if (!HasDeconvModel(out var skip)) { Assert.Skip(skip); return; }

        var r = new float[16, 16];
        var g = new float[16, 16];
        var b = new float[16, 16];
        var src = new Image([r, g, b], BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f,
            new ImageMeta { SensorType = SensorType.Color });

        var psf01 = HfdPsfEstimator.EncodeRadiusToPsf01(HfdPsfEstimator.DefaultRadiusPx);
        using var deconv = new OnnxNonStellarDeconvolver(new ModelResolver(), new FixedPsfEstimator(psf01));
        var ex = await Should.ThrowAsync<ArgumentException>(
            async () => await deconv.EnhanceAsync(src, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("AdoptImageAsync");
    }
}
