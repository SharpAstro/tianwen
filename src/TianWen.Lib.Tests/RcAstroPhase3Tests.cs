using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Phase 3a: the threaded <see cref="EnhanceOptions"/> surface -- per-call backend
/// selection (Auto / ForceRcAstro / TianWen) in <c>DeferredEnhancer</c> and RC-Astro
/// per-product <see cref="EnhanceTuning"/> flowing into the <c>rc-astro</c> CLI args.
/// Uses a fake <see cref="IRcAstroCli"/> so it runs with no real binary: backend choice
/// is asserted via which factory ran, tuning via the captured CLI args.
/// </summary>
[Collection("Imaging")]
public class RcAstroPhase3Tests : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    /// <summary>Fake CLI: configurable presence/license, captures the extra args, and echoes
    /// the input FITS the base just wrote to the output path so the FITS round-trip succeeds.</summary>
    private sealed class FakeRcAstroCli(bool available = true, bool licensed = true) : IRcAstroCli
    {
        public string? ExecutablePath => available ? "/fake/rc-astro" : null;
        public bool IsAvailable => available;
        public bool IsLicensed(string productKey) => available && licensed;

        public string? LastProduct { get; private set; }
        public IReadOnlyList<string> LastExtraArgs { get; private set; } = [];
        public int RunCount { get; private set; }

        public Task<RcAstroRunResult> RunAsync(
            string productKey, string inputPath, string outputPath,
            IReadOnlyList<string> extraArgs, IProgress<RcAstroProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastProduct = productKey;
            LastExtraArgs = extraArgs;
            RunCount++;
            File.Copy(inputPath, outputPath, overwrite: true); // input is a valid FITS -> readable round-trip
            progress?.Report(new RcAstroProgress(100, 1, 0));
            return Task.FromResult(new RcAstroRunResult("gpu", "Fake", new RcAstroProgress(100, 1, 0)));
        }
    }

    /// <summary>Marker enhancer that records whether it was invoked.</summary>
    private sealed class RecordingEnhancer(string name) : IImageEnhancer
    {
        public string Name => name;
        public bool Called { get; private set; }
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(input);
        }
    }

    [Fact]
    public async Task Tuning_OverridesBxtNonStellarSharpen_ElseDefault()
    {
        var cli = new FakeRcAstroCli();
        var deconv = new RcAstroNonStellarDeconvolver(cli);
        var src = RcAstroTestSupport.BuildNebula(64, 64, seed: 1);

        await deconv.EnhanceAsync(src, new EnhanceOptions(Tuning: new EnhanceTuning(DeblurSharpen: 0.5f)),
            cancellationToken: TestContext.Current.CancellationToken);
        cli.LastExtraArgs.ShouldBe(["--sn", "0.50"]);

        await deconv.EnhanceAsync(src, EnhanceOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        cli.LastExtraArgs.ShouldBe(["--sn", "0.90"]); // enhancer's own default preserved
    }

    [Fact]
    public async Task Tuning_MapsDenoiseStrengthAndIterationsToNxtArgs()
    {
        var cli = new FakeRcAstroCli();
        var nxt = new RcAstroDenoiser(cli);
        var src = RcAstroTestSupport.BuildNoisyRgb(64, 64, bg: 0.2f, noiseSigma: 0.02f, seed: 7);

        await nxt.EnhanceAsync(src, new EnhanceOptions(Tuning: new EnhanceTuning(DenoiseStrength: 0.33f, DenoiseIterations: 4)),
            cancellationToken: TestContext.Current.CancellationToken);

        cli.LastExtraArgs.ShouldBe(["--dn", "0.33", "--it", "4"]);
    }

    [Fact]
    public async Task NullTuning_UsesFixedDenoiserDefaults()
    {
        var cli = new FakeRcAstroCli();
        var nxt = new RcAstroDenoiser(cli, autoStrength: false, denoise: 0.90, iterations: 2);
        var src = RcAstroTestSupport.BuildNoisyRgb(64, 64, bg: 0.2f, noiseSigma: 0.02f, seed: 7);

        await nxt.EnhanceAsync(src, EnhanceOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        cli.LastExtraArgs.ShouldBe(["--dn", "0.90", "--it", "2"]);
    }

    /// <summary>
    /// A role with no in-house lane (the starless deconvolver until E7): RC-Astro when the choice
    /// lands on it, and otherwise NO backend -- it reports that it cannot serve, which is what keeps
    /// the canonical program from asking, and a direct call fails naming the role. There is no SETI
    /// Astro fallback any more (removed 2026-09-26).
    /// </summary>
    [Theory]
    [InlineData(EnhanceBackend.Auto, true, true, true)]           // RC when present + licensed
    [InlineData(EnhanceBackend.Auto, true, false, false)]         // present but unlicensed -> nothing
    [InlineData(EnhanceBackend.ForceRcAstro, true, false, true)]  // RC when present, license gate skipped
    [InlineData(EnhanceBackend.ForceRcAstro, false, false, false)]// binary absent -> nothing
    [InlineData(EnhanceBackend.TianWen, true, true, true)]            // no in-house lane on this role -> Auto -> RC
    [InlineData(EnhanceBackend.TianWen, false, false, false)]         // no in-house lane, no RC -> nothing
    public async Task Backend_SelectionMatrix_WithoutAnInHouseLane(EnhanceBackend backend, bool available, bool licensed, bool expectRc)
    {
        var cli = new FakeRcAstroCli(available, licensed);
        var rc = new RecordingEnhancer("rc");
        var deferred = new DeferredNonStellarDeconvolver(cli, () => rc);
        var src = RcAstroTestSupport.BuildNebula(32, 32, seed: 1);
        var options = new EnhanceOptions(backend);

        deferred.CanServe(3, options).ShouldBe(expectRc);
        if (expectRc)
        {
            await deferred.EnhanceAsync(src, options, cancellationToken: TestContext.Current.CancellationToken);
            rc.Called.ShouldBeTrue();
        }
        else
        {
            var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
                await deferred.EnhanceAsync(src, options, cancellationToken: TestContext.Current.CancellationToken));
            ex.Message.ShouldContain("starless deconvolution");
            rc.Called.ShouldBeFalse();
        }
    }

    /// <summary>
    /// The denoise role's in-house lane: Auto takes RC-Astro where nxt is licensed and the in-house
    /// model otherwise; <see cref="EnhanceBackend.TianWen"/> takes the in-house model even where RC is
    /// licensed; ForceRcAstro takes RC whenever the binary exists and the in-house model when it
    /// does not.
    /// </summary>
    [Theory]
    [InlineData(EnhanceBackend.Auto, true, true, "rc")]
    [InlineData(EnhanceBackend.Auto, true, false, "n2n")]
    [InlineData(EnhanceBackend.Auto, false, false, "n2n")]
    [InlineData(EnhanceBackend.TianWen, true, true, "n2n")]
    [InlineData(EnhanceBackend.ForceRcAstro, true, false, "rc")]
    [InlineData(EnhanceBackend.ForceRcAstro, false, false, "n2n")]
    public async Task Denoise_PrefersRcAstroThenTheInHouseModel(EnhanceBackend backend, bool available, bool licensed, string expected)
    {
        var cli = new FakeRcAstroCli(available, licensed);
        var rc = new RecordingEnhancer("rc");
        var n2n = new RecordingEnhancer("n2n");
        var deferred = new DeferredDenoiser(cli, () => rc, () => n2n);
        var src = RcAstroTestSupport.BuildNoisyRgb(32, 32, bg: 0.2f, noiseSigma: 0.02f, seed: 7);

        await deferred.EnhanceAsync(src, DenoiseVariant.Default, new EnhanceOptions(backend),
            cancellationToken: TestContext.Current.CancellationToken);

        rc.Called.ShouldBe(expected == "rc");
        n2n.Called.ShouldBe(expected == "n2n");
    }

    /// <summary>
    /// A denoiser built WITHOUT the in-house lane (a composition root that never wired it) degrades
    /// <see cref="EnhanceBackend.TianWen"/> to Auto rather than throwing, because the same options record
    /// reaches roles that cannot serve n2n.
    /// </summary>
    [Fact]
    public async Task ExplicitN2n_WithoutTheLane_DegradesToAuto()
    {
        var cli = new FakeRcAstroCli(available: true, licensed: true);
        var rc = new RecordingEnhancer("rc");
        var withoutLane = new DeferredDenoiser(cli, () => rc);
        var src = RcAstroTestSupport.BuildNoisyRgb(32, 32, bg: 0.2f, noiseSigma: 0.02f, seed: 7);

        await withoutLane.EnhanceAsync(src, DenoiseVariant.Default, new EnhanceOptions(EnhanceBackend.TianWen),
            cancellationToken: TestContext.Current.CancellationToken);

        rc.Called.ShouldBeTrue();
    }

    /// <summary>
    /// Availability passes through the proxy to the in-house model's own answer: with no RC-Astro,
    /// the denoise role serves 3-channel input exactly when the N2N weights resolve WITH their contract
    /// beside them (#824: weights without one are refused at load, so they do not serve), and never mono.
    /// This is what the canonical program asks before it puts a denoise step in the program.
    /// </summary>
    [Theory]
    [InlineData(true, true, 3, true)]
    [InlineData(true, true, 1, false)]   // the N2N model is one-shot-colour; mono is left out, not failed
    [InlineData(false, false, 3, false)] // no weights on disk: nothing to run
    [InlineData(true, false, 3, false)]  // weights with no contract: refused at load, so left out
    public void Denoise_AvailabilityIsTheInHouseModelsOwnAnswer(bool weightsOnDisk, bool contractOnDisk, int channels, bool expected)
    {
        var dir = _folders.Create("tw-n2n-avail-").FullName;
        // Presence is all CanServe probes (content is never read), but the file must not LOOK
        // like a Git LFS pointer stub, which ModelResolver refuses by design.
        if (weightsOnDisk)
        {
            File.WriteAllText(Path.Combine(dir, TianWen.AI.Imaging.Onnx.N2nDenoiser.ModelFileName), "weights");
        }
        if (contractOnDisk)
        {
            File.WriteAllText(Path.Combine(dir, TianWen.AI.Imaging.Onnx.ModelContract.ContractFileName(TianWen.AI.Imaging.Onnx.N2nDenoiser.ModelFileName)), "{}");
        }
        var resolver = new TianWen.AI.Imaging.ModelResolver([dir]);
        var cli = new FakeRcAstroCli(available: false);
        var deferred = new DeferredDenoiser(cli, () => new RecordingEnhancer("rc"),
            () => new TianWen.AI.Imaging.Onnx.N2nDenoiser(resolver));

        deferred.CanServe(channels, EnhanceOptions.Default).ShouldBe(expected);
    }
}
