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

    [Theory]
    [InlineData(EnhanceBackend.ForceSas, true, true, false)]      // SAS even when present + licensed
    [InlineData(EnhanceBackend.Auto, true, true, true)]           // RC when present + licensed
    [InlineData(EnhanceBackend.Auto, true, false, false)]         // SAS when present but unlicensed
    [InlineData(EnhanceBackend.ForceRcAstro, true, false, true)]  // RC when present, license gate skipped
    [InlineData(EnhanceBackend.ForceRcAstro, false, false, false)]// SAS when the binary is absent
    [InlineData(EnhanceBackend.N2n, true, true, true)]            // no in-house lane on this role -> Auto -> RC
    [InlineData(EnhanceBackend.N2n, false, false, false)]         // no in-house lane, no RC -> Auto -> SAS
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

    /// <summary>
    /// The explicit N2n backend routes the DENOISE role to the in-house lane whenever one is
    /// wired -- no model-file probe, no RC consultation. And a DeferredDenoiser built WITHOUT
    /// the lane (a composition root that never wired it) degrades to Auto rather than throwing,
    /// because the same options record reaches roles that cannot serve n2n.
    /// </summary>
    [Fact]
    public async Task ExplicitN2n_RoutesToTheInHouseLane_AndDegradesToAutoWithoutOne()
    {
        var cli = new FakeRcAstroCli(available: false);
        var src = RcAstroTestSupport.BuildNoisyRgb(32, 32, bg: 0.2f, noiseSigma: 0.02f, seed: 7);

        var sas = new RecordingEnhancer("sas");
        var n2n = new RecordingEnhancer("n2n");
        var withLane = new DeferredDenoiser(cli, () => new RecordingEnhancer("rc"), () => sas, () => n2n);
        await withLane.EnhanceAsync(src, DenoiseVariant.Default, new EnhanceOptions(EnhanceBackend.N2n),
            cancellationToken: TestContext.Current.CancellationToken);
        n2n.Called.ShouldBeTrue();
        sas.Called.ShouldBeFalse();

        var sasOnly = new RecordingEnhancer("sas");
        var withoutLane = new DeferredDenoiser(cli, () => new RecordingEnhancer("rc"), () => sasOnly);
        await withoutLane.EnhanceAsync(src, DenoiseVariant.Default, new EnhanceOptions(EnhanceBackend.N2n),
            cancellationToken: TestContext.Current.CancellationToken);
        sasOnly.Called.ShouldBeTrue();
    }

    /// <summary>
    /// The Auto rescue tier: with no RC binary, Auto lands on SAS -- and only when the SAS AI4
    /// weights are NOT on disk, the input is OSC, and the variant is Default does the in-house
    /// N2N model serve instead. A mono input or a Lite variant stays with SAS, whose own
    /// missing-model error names the one bundle that could serve it.
    /// </summary>
    [Theory]
    [InlineData(true, 3, DenoiseVariant.Default, false)]  // SAS weights installed -> SAS, byte-for-byte the old path
    [InlineData(false, 3, DenoiseVariant.Default, true)]  // absent + OSC default -> N2N rescue
    [InlineData(false, 1, DenoiseVariant.Default, false)] // mono -> N2N cannot serve it
    [InlineData(false, 3, DenoiseVariant.Lite, false)]    // Lite -> N2N has one bundle, no Lite
    public async Task AutoRescue_ServesN2nOnlyWhenSasWeightsAreAbsentAndTheInputIsServable(
        bool sasWeightsOnDisk, int channels, DenoiseVariant variant, bool expectN2n)
    {
        var dir = Directory.CreateTempSubdirectory("tw-n2n-rescue-").FullName;
        try
        {
            // Presence is all the rescue probes (content is never read), but the file must not
            // LOOK like a Git LFS pointer stub, which ModelResolver refuses by design.
            if (sasWeightsOnDisk)
            {
                File.WriteAllText(Path.Combine(dir, TianWen.AI.Imaging.Onnx.OnnxDenoiser.ModelFileNameFor(channels, variant)), "weights");
            }
            File.WriteAllText(Path.Combine(dir, TianWen.AI.Imaging.Onnx.N2nDenoiser.ModelFileName), "weights");
            var resolver = new TianWen.AI.Imaging.ModelResolver([dir]);

            var cli = new FakeRcAstroCli(available: false);
            var sas = new RecordingEnhancer("sas");
            var n2n = new RecordingEnhancer("n2n");
            var deferred = new DeferredDenoiser(cli, () => new RecordingEnhancer("rc"), () => sas, () => n2n, resolver);

            var src = channels == 3
                ? RcAstroTestSupport.BuildNoisyRgb(32, 32, bg: 0.2f, noiseSigma: 0.02f, seed: 7)
                : new Image([new float[32, 32]], BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta { SensorType = SensorType.Monochrome });
            await deferred.EnhanceAsync(src, variant, new EnhanceOptions(EnhanceBackend.Auto),
                cancellationToken: TestContext.Current.CancellationToken);

            n2n.Called.ShouldBe(expectN2n);
            sas.Called.ShouldBe(!expectN2n);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
