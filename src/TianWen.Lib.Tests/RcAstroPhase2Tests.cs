using System.Collections.Immutable;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.AI.Imaging.Onnx;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Phase 2: the nxt-&gt;<see cref="IDenoiseEnhancer"/> and
/// bxt-&gt;<see cref="INonStellarDeconvolver"/> wrappers. Covers the pure
/// noise-&gt;strength map, the present+licensed selectors, and CLI-gated
/// end-to-end smoke tests for both products.
/// </summary>
[Collection("Imaging")]
public class RcAstroPhase2Tests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1e-5, 0.70)]   // cleaner than the floor -> min
    [InlineData(1e-4, 0.70)]   // floor
    [InlineData(1e-3, 0.825)]  // log-midpoint -> band midpoint
    [InlineData(1e-2, 0.95)]   // ceiling
    [InlineData(1e-1, 0.95)]   // noisier than the ceiling -> max
    public void MapNoiseToStrength_LogInterpolatesAndClamps(double sigma, double expected)
    {
        var dn = RcAstroDenoiser.MapNoiseToStrength([(float)sigma], 0.70, 0.95);
        dn.ShouldBe(expected, tolerance: 0.01);
    }

    [Fact]
    public void MapNoiseToStrength_EdgeCases()
    {
        // Empty profile -> band midpoint.
        RcAstroDenoiser.MapNoiseToStrength([], 0.70, 0.95).ShouldBe(0.825, tolerance: 0.01);
        // All-zero (degenerate / flat synthetic) -> lightest touch.
        RcAstroDenoiser.MapNoiseToStrength([0f, 0f, 0f], 0.70, 0.95).ShouldBe(0.70, tolerance: 0.01);
        // Monotonic in sigma.
        var low = RcAstroDenoiser.MapNoiseToStrength([5e-4f], 0.70, 0.95);
        var high = RcAstroDenoiser.MapNoiseToStrength([5e-3f], 0.70, 0.95);
        high.ShouldBeGreaterThan(low);
        // Multi-channel averages.
        var mixed = RcAstroDenoiser.MapNoiseToStrength([1e-4f, 1e-2f], 0.70, 0.95);
        mixed.ShouldBeInRange(0.70, 0.95);
    }

    [Fact]
    public void AddRcAstroAi_ResolvesDeferredProxies_ThenSelectsByLicense()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddRcAstroAi();

        using var provider = services.BuildServiceProvider();
        // Resolution returns proxies and spawns no rc-astro process.
        var denoiser = provider.GetRequiredService<IDenoiseEnhancer>().ShouldBeOfType<DeferredDenoiser>();
        var deconvolver = provider.GetRequiredService<INonStellarDeconvolver>().ShouldBeOfType<DeferredNonStellarDeconvolver>();

        if (RcAstroTestSupport.ProductAvailable("nxt", out _))
        {
            denoiser.Backend.ShouldBeOfType<RcAstroDenoiser>();
        }
        else
        {
            denoiser.Backend.ShouldBeOfType<OnnxDenoiser>();
        }

        if (RcAstroTestSupport.ProductAvailable("bxt", out _))
        {
            deconvolver.Backend.ShouldBeOfType<RcAstroNonStellarDeconvolver>();
        }
        else
        {
            deconvolver.Backend.ShouldBeOfType<OnnxNonStellarDeconvolver>();
        }
    }

    [Fact]
    public async Task Denoiser_ReducesNoise_RoundTripsThroughFits()
    {
        if (!RcAstroTestSupport.ProductAvailable("nxt", out var skip)) { Assert.Skip(skip); return; }

        const int w = 256, h = 256;
        var src = RcAstroTestSupport.BuildNoisyRgb(w, h, bg: 0.20f, noiseSigma: 0.03f, seed: 1234);
        var sigmaIn = RcAstroTestSupport.MeanSigma(src);

        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        var enhancer = new RcAstroDenoiser(new RcAstroCli(factory.CreateLogger<RcAstroCli>()), factory.CreateLogger<RcAstroDenoiser>());

        var result = await enhancer.EnhanceAsync(src, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Shape.ShouldBe((3, w, h));
        var sigmaOut = RcAstroTestSupport.MeanSigma(result);
        output.WriteLine($"sigma: in={sigmaIn:E3} out={sigmaOut:E3}");
        sigmaOut.ShouldBeLessThan(sigmaIn);
        RcAstroTestSupport.AllFinite(result).ShouldBeTrue();

        result.Release();
    }

    [Fact]
    public async Task Deconvolver_SharpensNebula_RoundTripsThroughFits()
    {
        if (!RcAstroTestSupport.ProductAvailable("bxt", out var skip)) { Assert.Skip(skip); return; }

        const int w = 256, h = 256;
        var src = RcAstroTestSupport.BuildNebula(w, h, seed: 99);

        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        var enhancer = new RcAstroNonStellarDeconvolver(new RcAstroCli(factory.CreateLogger<RcAstroCli>()), factory.CreateLogger<RcAstroNonStellarDeconvolver>());

        var result = await enhancer.EnhanceAsync(src, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Shape.ShouldBe((3, w, h));
        RcAstroTestSupport.AllFinite(result).ShouldBeTrue();
        // bxt must actually change the plate (nonstellar sharpening).
        var rms = RcAstroTestSupport.RmsDifference(src, result);
        output.WriteLine($"bxt RMS difference: {rms:E4}");
        rms.ShouldBeGreaterThan(1e-4);

        result.Release();
    }
}
