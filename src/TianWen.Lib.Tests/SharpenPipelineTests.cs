using System;
using System.Threading;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for the <see cref="SharpenPipeline"/> orchestrator. The
/// validation rules + per-step pass-through behaviour are exercised against
/// trivial fake enhancers (no model files needed). One end-to-end test
/// drives the real ONNX enhancers; it gates on model availability and skips
/// silently otherwise.
/// </summary>
[Collection("Imaging")]
public class SharpenPipelineTests(ITestOutputHelper output)
{
    // --- Fake enhancers for validation + pass-through behaviour --------

    /// <summary>
    /// Subtracts a constant from each pixel and clamps to >= 0. Stands in
    /// for IStarRemover -- output is a "fake starless" plate where every
    /// pixel has been dimmed by the constant. Combined with the canonical
    /// Source - Starless split this produces a stars-only plate where each
    /// pixel = min(Source, constant).
    /// </summary>
    private sealed class ConstantStarRemover(float subtract) : IStarRemover
    {
        public string Name => "Test/ConstantStarRemover";
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var (channels, w, h) = input.Shape;
            var data = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var plane = new float[h, w];
                var src = input.GetChannelSpan(c);
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var v = src[y * w + x] - subtract;
                        plane[y, x] = v > 0f ? v : 0f;
                    }
                }
                data[c] = plane;
            }
            return Task.FromResult(new Image(data, BitDepth.Float32, 1.0f, 0f, 0f, input.ImageMeta));
        }
    }

    /// <summary>Identity enhancer (returns input unchanged). Stand-in for any role.</summary>
    private sealed class IdentityEnhancer(string name) : IStarRemover, IStellarSharpener, INonStellarDeconvolver
    {
        public string Name => name;
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);
    }

    /// <summary>
    /// Star remover that ignores the input and returns a uniform-colored plate
    /// of the same shape. Useful for SCNR-on-stars tests where the test wants
    /// to control the stars-only plate's per-channel distribution (the stars
    /// plate becomes <c>Source - uniformR/G/B</c> per channel after the split,
    /// so the test can dial in a green dominance independently of the source).
    /// </summary>
    private sealed class UniformStarRemover(float r, float g, float b) : IStarRemover
    {
        public string Name => $"Test/UniformStarRemover({r:F2},{g:F2},{b:F2})";
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var (channels, w, h) = input.Shape;
            var data = new float[channels][,];
            var fills = channels == 1 ? new[] { (r + g + b) / 3f } : new[] { r, g, b };
            for (var c = 0; c < channels; c++)
            {
                var plane = new float[h, w];
                var fill = c < fills.Length ? fills[c] : fills[^1];
                for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) plane[y, x] = fill;
                data[c] = plane;
            }
            return Task.FromResult(new Image(data, BitDepth.Float32, 1.0f, 0f, 0f, input.ImageMeta));
        }
    }

    private static Image SyntheticRgb(int w, int h, float fill)
    {
        var r = new float[h, w];
        var g = new float[h, w];
        var b = new float[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                r[y, x] = fill;
                g[y, x] = fill;
                b[y, x] = fill;
            }
        return new Image([r, g, b], BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.Color });
    }

    // --- Request validation --------------------------------------------

    [Fact]
    public async Task ProcessAsync_RejectsStellarSharpenWithoutStarRemoval()
    {
        var pipeline = new SharpenPipeline(
            starRemover: new IdentityEnhancer("star"),
            stellarSharpener: new IdentityEnhancer("stellar"));
        var src = SyntheticRgb(8, 8, 0.5f);

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await pipeline.ProcessAsync(new SharpenRequest(src,
                RunStarRemoval: false, RunStellarSharpen: true, RunNonStellarDeconv: false, Recombine: false),
                TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("RunStellarSharpen requires RunStarRemoval");
    }

    [Fact]
    public async Task ProcessAsync_RejectsNonStellarDeconvWithoutStarRemoval()
    {
        var pipeline = new SharpenPipeline(
            starRemover: new IdentityEnhancer("star"),
            nonStellarDeconvolver: new IdentityEnhancer("deconv"));
        var src = SyntheticRgb(8, 8, 0.5f);

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await pipeline.ProcessAsync(new SharpenRequest(src,
                RunStarRemoval: false, RunStellarSharpen: false, RunNonStellarDeconv: true, Recombine: false),
                TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("RunNonStellarDeconv requires RunStarRemoval");
    }

    [Fact]
    public async Task ProcessAsync_RejectsRecombineWithoutAnyEnhancement()
    {
        var pipeline = new SharpenPipeline(starRemover: new IdentityEnhancer("star"));
        var src = SyntheticRgb(8, 8, 0.5f);

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await pipeline.ProcessAsync(new SharpenRequest(src,
                RunStarRemoval: true, RunStellarSharpen: false, RunNonStellarDeconv: false, Recombine: true),
                TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("Recombine requires at least one");
    }

    [Fact]
    public async Task ProcessAsync_FailsClearlyWhenEnhancerNotRegistered()
    {
        // Pipeline constructed with no enhancers; any enabled step throws.
        var pipeline = new SharpenPipeline();
        var src = SyntheticRgb(8, 8, 0.5f);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await pipeline.ProcessAsync(new SharpenRequest(src,
                RunStarRemoval: true, RunStellarSharpen: false, RunNonStellarDeconv: false, Recombine: false),
                TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("AddTianWenAi");
    }

    // --- Per-step results + recombine math -----------------------------

    [Fact]
    public async Task ProcessAsync_StarRemovalOnly_PopulatesStarlessAndStarsOnly()
    {
        var pipeline = new SharpenPipeline(starRemover: new ConstantStarRemover(0.2f));
        var src = SyntheticRgb(8, 8, 0.5f);

        var result = await pipeline.ProcessAsync(new SharpenRequest(src,
            RunStarRemoval: true, RunStellarSharpen: false, RunNonStellarDeconv: false, Recombine: false),
            TestContext.Current.CancellationToken);

        result.Starless.ShouldNotBeNull();
        result.StarsOnly.ShouldNotBeNull();
        result.SharpenedStars.ShouldBeNull();
        result.DeconvolvedStarless.ShouldBeNull();
        result.Final.ShouldBeNull();

        // Starless = max(Source - 0.2, 0) = 0.3 everywhere.
        // StarsOnly = max(Source - Starless, 0) = max(0.5 - 0.3, 0) = 0.2.
        result.Starless[0, 0, 0].ShouldBe(0.3f, 1e-5f);
        result.StarsOnly[0, 0, 0].ShouldBe(0.2f, 1e-5f);
    }

    [Fact]
    public async Task ProcessAsync_FullPipelineRecombine_AdditiveIdentityWhenEnhancersAreIdentity()
    {
        // With identity enhancers (stellar + deconv pass-through their
        // inputs), Final = StarsOnly + Starless = Source (modulo clamping).
        var pipeline = new SharpenPipeline(
            starRemover: new ConstantStarRemover(0.2f),
            stellarSharpener: new IdentityEnhancer("stellar"),
            nonStellarDeconvolver: new IdentityEnhancer("deconv"));
        var src = SyntheticRgb(8, 8, 0.5f);

        var result = await pipeline.ProcessAsync(new SharpenRequest(src,
            RunStarRemoval: true, RunStellarSharpen: true, RunNonStellarDeconv: true, Recombine: true),
            TestContext.Current.CancellationToken);

        result.Final.ShouldNotBeNull();
        result.SharpenedStars.ShouldNotBeNull();
        result.DeconvolvedStarless.ShouldNotBeNull();

        // SharpenedStars = identity(StarsOnly) = 0.2
        // DeconvolvedStarless = identity(Starless) = 0.3
        // Final = SharpenedStars + DeconvolvedStarless = 0.2 + 0.3 = 0.5 = Source.
        result.Final[0, 0, 0].ShouldBe(0.5f, 1e-5f);
    }

    [Fact]
    public async Task ProcessAsync_RecombineWithSharpenedStarsOnly_FallsBackToStarlessForBackground()
    {
        // RunNonStellarDeconv: false -> recombine uses raw Starless as the bg.
        var pipeline = new SharpenPipeline(
            starRemover: new ConstantStarRemover(0.2f),
            stellarSharpener: new IdentityEnhancer("stellar"));
        var src = SyntheticRgb(8, 8, 0.5f);

        var result = await pipeline.ProcessAsync(new SharpenRequest(src,
            RunStarRemoval: true, RunStellarSharpen: true, RunNonStellarDeconv: false, Recombine: true),
            TestContext.Current.CancellationToken);

        result.Final.ShouldNotBeNull();
        result.DeconvolvedStarless.ShouldBeNull();
        // Final = SharpenedStars (0.2) + Starless (0.3) = 0.5.
        result.Final[0, 0, 0].ShouldBe(0.5f, 1e-5f);
    }

    [Fact]
    public async Task ProcessAsync_RecombineWithDeconvOnly_FallsBackToStarsOnlyForForeground()
    {
        var pipeline = new SharpenPipeline(
            starRemover: new ConstantStarRemover(0.2f),
            nonStellarDeconvolver: new IdentityEnhancer("deconv"));
        var src = SyntheticRgb(8, 8, 0.5f);

        var result = await pipeline.ProcessAsync(new SharpenRequest(src,
            RunStarRemoval: true, RunStellarSharpen: false, RunNonStellarDeconv: true, Recombine: true),
            TestContext.Current.CancellationToken);

        result.Final.ShouldNotBeNull();
        result.SharpenedStars.ShouldBeNull();
        // Final = StarsOnly (0.2) + DeconvolvedStarless (0.3) = 0.5.
        result.Final[0, 0, 0].ShouldBe(0.5f, 1e-5f);
    }

    // --- Screen mode --------------------------------------------------

    [Fact]
    public async Task ProcessAsync_ScreenMode_UnscreenSplitRoundTripsThroughScreenRecombine()
    {
        // With identity enhancers, screen-mode split + screen-mode recombine
        // must produce Final = Source. This is the screen-identity
        // round-trip: source = screen(starless, unscreen(source, starless)).
        var pipeline = new SharpenPipeline(
            starRemover: new ConstantStarRemover(0.2f),
            stellarSharpener: new IdentityEnhancer("stellar"),
            nonStellarDeconvolver: new IdentityEnhancer("deconv"));
        var src = SyntheticRgb(8, 8, 0.5f);

        var result = await pipeline.ProcessAsync(new SharpenRequest(src,
            RunStarRemoval: true, RunStellarSharpen: true, RunNonStellarDeconv: true,
            Recombine: true, Mode: RecombineMode.Screen),
            TestContext.Current.CancellationToken);

        result.Final.ShouldNotBeNull();
        // Starless = max(Source - 0.2, 0) = 0.3.
        // Unscreen split: StarsOnly = 1 - (1 - 0.5) / (1 - 0.3)
        //               = 1 - 0.5/0.7 ≈ 0.2857.
        // Identity enhancers pass through.
        // Screen recombine: Final = 1 - (1 - 0.3) * (1 - 0.2857) ≈ 0.5.
        result.Final[0, 0, 0].ShouldBe(0.5f, 1e-4f);
    }

    [Fact]
    public async Task ProcessAsync_ScreenMode_UnscreenSplitProducesExpectedStarsValue()
    {
        var pipeline = new SharpenPipeline(starRemover: new ConstantStarRemover(0.2f));
        var src = SyntheticRgb(8, 8, 0.5f);

        var result = await pipeline.ProcessAsync(new SharpenRequest(src,
            RunStarRemoval: true, RunStellarSharpen: false, RunNonStellarDeconv: false,
            Recombine: false, Mode: RecombineMode.Screen),
            TestContext.Current.CancellationToken);

        result.Starless.ShouldNotBeNull();
        result.StarsOnly.ShouldNotBeNull();
        // Starless = 0.3 (from constant star remover).
        // Unscreen: StarsOnly = 1 - (1 - 0.5) / (1 - 0.3) = 1 - 5/7 ≈ 0.2857.
        result.Starless[0, 0, 0].ShouldBe(0.3f, 1e-5f);
        result.StarsOnly[0, 0, 0].ShouldBe(1f - 5f / 7f, 1e-4f);
    }

    // --- SCNR-on-stars + AI blend amounts ------------------------------

    [Fact]
    public async Task ProcessAsync_StarsScnr_AppliesToStarsPlateNotStarless()
    {
        // Build a green-tinted source over a neutral-grey "starless" plate so
        // the additive split puts the green dominance squarely on the stars
        // plate -- exactly the workflow shape SCNR is for.
        // Source = (0.5, 0.8, 0.5). UniformStarRemover(0.3, 0.3, 0.3) ->
        //   Starless = (0.3, 0.3, 0.3) (uniform neutral plate),
        //   StarsOnly = max(Source - Starless, 0) = (0.2, 0.5, 0.2).
        // SCNR Average + amount=1 on stars: m = (0.2+0.2)/2 = 0.2,
        //   excess = 0.5-0.2 = 0.3, Gnew = 0.5 - 0.3 = 0.2 -- neutralised.
        // Starless: untouched, still (0.3, 0.3, 0.3).
        // Recombined: (0.2+0.3, 0.2+0.3, 0.2+0.3) = (0.5, 0.5, 0.5) -- white.
        const int w = 4, h = 4;
        var r = new float[h, w];
        var g = new float[h, w];
        var b = new float[h, w];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) { r[y, x] = 0.5f; g[y, x] = 0.8f; b[y, x] = 0.5f; }
        var src = new Image([r, g, b], BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });

        var pipeline = new SharpenPipeline(starRemover: new UniformStarRemover(0.3f, 0.3f, 0.3f));
        var result = await pipeline.ProcessAsync(new SharpenRequest(src,
            RunStarRemoval: true, RunStellarSharpen: false, RunNonStellarDeconv: false,
            Recombine: true, StarsScnrMode: ScnrMode.Average, StarsScnrAmount: 1.0f),
            TestContext.Current.CancellationToken);

        result.Final.ShouldNotBeNull();
        // Stars plate after SCNR: G channel pulled from 0.5 down to 0.2.
        result.StarsOnly!.ShouldNotBeNull();
        result.StarsOnly![1, 0, 0].ShouldBe(0.2f, 1e-5f);
        // Starless plate: untouched -- still (0.3, 0.3, 0.3) per channel.
        result.Starless![1, 0, 0].ShouldBe(0.3f, 1e-5f);
        // Recombined: 0.2 + 0.3 = 0.5 on every channel -> neutral white.
        result.Final[0, 0, 0].ShouldBe(0.5f, 1e-5f);
        result.Final[1, 0, 0].ShouldBe(0.5f, 1e-5f);
        result.Final[2, 0, 0].ShouldBe(0.5f, 1e-5f);
    }

    [Fact]
    public async Task ProcessAsync_StellarBlend_BlendsAiOutputBackTowardStarsOnly()
    {
        // With identity stellar sharpener, the AI output equals the input;
        // Lerp is a no-op at any blend value. Use an enhancer that DOUBLES
        // the input so the blend dial is observable.
        var pipeline = new SharpenPipeline(
            starRemover: new ConstantStarRemover(0.2f),
            stellarSharpener: new ScaleEnhancer(2.0f));
        var src = SyntheticRgb(8, 8, 0.5f);

        // StarsOnly = 0.2 everywhere. ScaleEnhancer(2x) -> 0.4. Blend=0.5
        // -> lerp(0.2, 0.4, 0.5) = 0.3 -- exact midpoint.
        var halfResult = await pipeline.ProcessAsync(new SharpenRequest(src,
            RunStarRemoval: true, RunStellarSharpen: true, RunNonStellarDeconv: false,
            Recombine: false, StellarBlend: 0.5f),
            TestContext.Current.CancellationToken);
        halfResult.SharpenedStars![0, 0, 0].ShouldBe(0.3f, 1e-5f);

        // Blend=0 -> entirely the input, no AI applied.
        var zeroResult = await pipeline.ProcessAsync(new SharpenRequest(src,
            RunStarRemoval: true, RunStellarSharpen: true, RunNonStellarDeconv: false,
            Recombine: false, StellarBlend: 0.0f),
            TestContext.Current.CancellationToken);
        zeroResult.SharpenedStars![0, 0, 0].ShouldBe(0.2f, 1e-5f);
    }

    /// <summary>Multiplies every pixel by a scalar. Stand-in for an enhancer that
    /// actually changes its input so the blend math is observable in tests.</summary>
    private sealed class ScaleEnhancer(float scale) : IStarRemover, IStellarSharpener, INonStellarDeconvolver
    {
        public string Name => $"Test/Scale({scale})";
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var (channels, w, h) = input.Shape;
            var data = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var plane = new float[h, w];
                var src = input.GetChannelSpan(c);
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                        plane[y, x] = src[y * w + x] * scale;
                data[c] = plane;
            }
            return Task.FromResult(new Image(data, BitDepth.Float32, 1.0f, 0f, 0f, input.ImageMeta));
        }
    }

    // --- DI wiring -----------------------------------------------------

    [Fact]
    public void AddTianWenAi_WiresUpSharpenPipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTianWenAi();

        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<SharpenPipeline>();
        pipeline.ShouldNotBeNull();
    }

    // --- End-to-end smoke test (gated on real models) ------------------

    private static bool HasAllModels(out string skip)
    {
        var r = new ModelResolver();
        if (r.TryResolve("darkstar_color_AI4.onnx", out _) &&
            r.TryResolve("deep_sharp_stellar_AI4.onnx", out _) &&
            r.TryResolve("deep_nonstellar_sharp_conditional_psf_AI4.onnx", out _))
        {
            skip = string.Empty;
            return true;
        }
        skip = "AI4 model files not all present; run tools/tianwen-ai-models-fetch.ps1 to enable this test.";
        return false;
    }

    [Fact]
    public async Task ProcessAsync_FullPipelineAgainstRealModels()
    {
        if (!HasAllModels(out var skip)) { Assert.Skip(skip); return; }

        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(factory);
        services.AddLogging();
        services.AddTianWenAi();

        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<SharpenPipeline>();

        // Synthetic but realistic: low-key background + a couple of bright
        // Gaussian blobs (stars). Stretched chunk size means the whole
        // image fits in one chunk per enhancer.
        const int w = 256, h = 192;
        var src = SyntheticRgb(w, h, 0.10f);
        // Inject two bright stars by mutating the synthetic plane via the
        // GetChannelArray internal accessor (test project has
        // InternalsVisibleTo via the regular test build chain).
        for (var dy = -5; dy <= 5; dy++)
            for (var dx = -5; dx <= 5; dx++)
            {
                var weight = 0.7f * MathF.Exp(-(dx * dx + dy * dy) / 8f);
                AddStarPixel(src, w / 3 + dx, h / 2 + dy, weight);
                AddStarPixel(src, 2 * w / 3 + dx, h / 2 + dy, weight);
            }

        var result = await pipeline.ProcessAsync(new SharpenRequest(src,
            RunStarRemoval: true, RunStellarSharpen: true, RunNonStellarDeconv: true, Recombine: true),
            TestContext.Current.CancellationToken);

        result.Final.ShouldNotBeNull();
        result.Starless.ShouldNotBeNull();
        result.StarsOnly.ShouldNotBeNull();
        result.SharpenedStars.ShouldNotBeNull();
        result.DeconvolvedStarless.ShouldNotBeNull();

        // Shape + finite-value sanity check.
        var (rc, rw, rh) = result.Final.Shape;
        rc.ShouldBe(3);
        rw.ShouldBe(w);
        rh.ShouldBe(h);
        for (var c = 0; c < 3; c++)
        {
            var span = result.Final.GetChannelSpan(c);
            for (var i = 0; i < span.Length; i++)
            {
                float.IsFinite(span[i]).ShouldBeTrue($"non-finite c={c} idx={i}: {span[i]}");
            }
        }

        result.Final.Release();
        result.Starless.Release();
        result.StarsOnly.Release();
        result.SharpenedStars.Release();
        result.DeconvolvedStarless.Release();
    }

    private static void AddStarPixel(Image image, int x, int y, float weight)
    {
        if ((uint)x >= (uint)image.Width || (uint)y >= (uint)image.Height) return;
        for (var c = 0; c < image.ChannelCount; c++)
        {
            var arr = image.GetChannelArray(c);
            arr[y, x] = MathF.Min(1f, arr[y, x] + weight);
        }
    }
}
