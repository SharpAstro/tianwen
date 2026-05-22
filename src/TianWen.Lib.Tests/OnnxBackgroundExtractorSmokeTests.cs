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
/// End-to-end smoke tests for the GraXpert BGE background-extraction pipeline.
/// Gated on the model file being present under
/// <c>%LOCALAPPDATA%\TianWen\models\graxpert_bge.onnx</c> (populated by
/// <c>tools/tianwen-ai-models-fetch.ps1</c>); skips silently when missing
/// so CI / fresh-clone runs without the fetch don't fail.
/// </summary>
[Collection("Imaging")]
public class OnnxBackgroundExtractorSmokeTests(ITestOutputHelper output)
{
    private static bool HasBgeModel(out string skipMessage)
    {
        var resolver = new ModelResolver();
        if (resolver.TryResolve("graxpert_bge.onnx", out _))
        {
            skipMessage = string.Empty;
            return true;
        }
        skipMessage = "graxpert_bge.onnx not found; run tools/tianwen-ai-models-fetch.ps1 to enable this test.";
        return false;
    }

    /// <summary>
    /// 3-channel synthetic plate: smooth horizontal brightness gradient on
    /// top of a low-key sky background. A correct gradient corrector should
    /// land on something close to uniform after subtraction.
    /// </summary>
    private static Image BuildSyntheticRgbWithGradient(int w, int h)
    {
        var r = new float[h, w];
        var g = new float[h, w];
        var b = new float[h, w];
        const float bg = 0.10f;
        const float maxLift = 0.05f;  // peak gradient amplitude above bg
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // Linear horizontal ramp + tiny per-channel tint so we can
                // probe per-channel gradient handling.
                var t = (float)x / (w - 1);
                r[y, x] = bg + maxLift * t;
                g[y, x] = bg + maxLift * t * 0.8f;
                b[y, x] = bg + maxLift * t * 1.2f;
            }
        }
        return new Image([r, g, b], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
            new ImageMeta { SensorType = SensorType.Color });
    }

    [Fact]
    public async Task EnhanceAsync_ProducesSameShapedOutput()
    {
        if (!HasBgeModel(out var skip)) { Assert.Skip(skip); return; }

        // 256x256 means the BGE shrink/pad pass round-trips a 1:1 plate
        // through the model -- no resize loss to confuse the assertion.
        const int w = 256, h = 256;
        var src = BuildSyntheticRgbWithGradient(w, h);
        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        using var enhancer = new OnnxBackgroundExtractor(new ModelResolver(), factory.CreateLogger<OnnxBackgroundExtractor>());

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
    public async Task EnhanceAndEstimateBackgroundAsync_ReturnsBothCorrectedAndBackground()
    {
        if (!HasBgeModel(out var skip)) { Assert.Skip(skip); return; }

        // Verifies the diagnostic variant exposes the background plate.
        // Both outputs must be the same shape as the input, non-null, and
        // contain finite values. The corrected output should be closer to
        // uniform than the input (gradient reduced); we assert a loose
        // monotonicity-of-stddev bound rather than an exact value because
        // the model is allowed creative latitude in what it considers a
        // gradient.
        const int w = 256, h = 256;
        var src = BuildSyntheticRgbWithGradient(w, h);
        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        using var enhancer = new OnnxBackgroundExtractor(new ModelResolver(), factory.CreateLogger<OnnxBackgroundExtractor>());

        var (corrected, background) = await enhancer.EnhanceAndEstimateBackgroundAsync(src, TestContext.Current.CancellationToken);

        corrected.ShouldNotBeNull();
        background.ShouldNotBeNull();

        // Same shape on both outputs.
        corrected.Shape.ShouldBe(src.Shape);
        background!.Shape.ShouldBe(src.Shape);

        // No non-finite leaks.
        for (var c = 0; c < 3; c++)
        {
            foreach (var v in corrected.GetChannelSpan(c)) float.IsFinite(v).ShouldBeTrue();
            foreach (var v in background.GetChannelSpan(c)) float.IsFinite(v).ShouldBeTrue();
        }

        // Gradient should be reduced: per-channel standard deviation of the
        // CORRECTED plate is lower than the input. Pure horizontal ramp's
        // stddev for [0, lift] is lift/sqrt(12); a well-functioning corrector
        // should bring it well below the input value.
        for (var c = 0; c < 3; c++)
        {
            var srcStd = StdDev(src.GetChannelSpan(c));
            var corStd = StdDev(corrected.GetChannelSpan(c));
            corStd.ShouldBeLessThan(srcStd,
                customMessage: $"channel {c}: corrected stddev {corStd:F5} should be below input stddev {srcStd:F5}");
        }

        corrected.Release();
        background.Release();

        static float StdDev(ReadOnlySpan<float> values)
        {
            double mean = 0; for (var i = 0; i < values.Length; i++) mean += values[i];
            mean /= values.Length;
            double sumSq = 0; for (var i = 0; i < values.Length; i++) { var d = values[i] - mean; sumSq += d * d; }
            return (float)Math.Sqrt(sumSq / values.Length);
        }
    }

    [Fact]
    public async Task DefaultInterfaceMethod_ReturnsNullBackground()
    {
        // Any IGradientCorrector that doesn't override
        // EnhanceAndEstimateBackgroundAsync must surface a null Background --
        // tells the caller "no diagnostic available here" without throwing.
        // Use an inline minimal impl that just returns the input unchanged.
        // Note: default-interface-method must be called through the interface
        // type, not the concrete class.
        IGradientCorrector stub = new IdentityCorrector();
        var src = BuildSyntheticRgbWithGradient(16, 16);

        var (corrected, background) = await stub.EnhanceAndEstimateBackgroundAsync(src, TestContext.Current.CancellationToken);

        corrected.ShouldNotBeNull();
        background.ShouldBeNull();
        corrected.Release();
    }

    [Fact]
    public void AddTianWenAi_WiresUpIGradientCorrector()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddTianWenAi();

        using var provider = services.BuildServiceProvider();
        var corrector = provider.GetRequiredService<IGradientCorrector>();
        corrector.ShouldBeOfType<OnnxBackgroundExtractor>();
        corrector.Name.ShouldContain("GraXpert");
    }

    private sealed class IdentityCorrector : IGradientCorrector
    {
        public string Name => "IdentityCorrector";
        public Task<Image> EnhanceAsync(Image input, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(input);
    }
}
