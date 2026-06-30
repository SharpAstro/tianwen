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
/// selection (Auto / ForceRcAstro / ForceSas) in <c>DeferredEnhancer</c> and RC-Astro
/// per-product <see cref="EnhanceTuning"/> flowing into the <c>rc-astro</c> CLI args.
/// Uses a fake <see cref="IRcAstroCli"/> so it runs with no real binary: backend choice
/// is asserted via which factory ran, tuning via the captured CLI args.
/// </summary>
[Collection("Imaging")]
public class RcAstroPhase3Tests
{
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
    public async Task Tuning_OverridesNxtDenoiseAndIterations()
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

    [Theory]
    [InlineData(EnhanceBackend.ForceSas, true, true, false)]      // SAS even when present + licensed
    [InlineData(EnhanceBackend.Auto, true, true, true)]           // RC when present + licensed
    [InlineData(EnhanceBackend.Auto, true, false, false)]         // SAS when present but unlicensed
    [InlineData(EnhanceBackend.ForceRcAstro, true, false, true)]  // RC when present, license gate skipped
    [InlineData(EnhanceBackend.ForceRcAstro, false, false, false)]// SAS when the binary is absent
    public async Task Backend_SelectionMatrix(EnhanceBackend backend, bool available, bool licensed, bool expectRc)
    {
        var cli = new FakeRcAstroCli(available, licensed);
        var rc = new RecordingEnhancer("rc");
        var sas = new RecordingEnhancer("sas");
        var deferred = new DeferredNonStellarDeconvolver(cli, () => rc, () => sas);
        var src = RcAstroTestSupport.BuildNebula(32, 32, seed: 1);

        await deferred.EnhanceAsync(src, new EnhanceOptions(backend), cancellationToken: TestContext.Current.CancellationToken);

        rc.Called.ShouldBe(expectRc);
        sas.Called.ShouldBe(!expectRc);
    }
}
