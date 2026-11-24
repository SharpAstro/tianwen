using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.GraXpert;
using TianWen.AI.Imaging.Onnx;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Cli;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The enhance step of a project and <c>tianwen image sharpen</c> are one routine (#2466): the step's printed command
/// line, run through the real verb on the same pipeline, writes the step's file to the byte. Stand-in enhancers serve
/// the star removal and the denoise; the gradient is the classical fit.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchEnhanceParityTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    /// <summary>An enhancer that scales what it is handed: the same answer for the same input.</summary>
    private sealed class Scaling(string name, float factor) : IStarRemover, IDenoiseEnhancer, IImageDeblurrer, INonStellarDeconvolver
    {
        public string Name => name;

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var (channels, width, height) = input.Shape;
            var planes = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var source = input.GetChannelSpan(c);
                var plane = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        plane[y, x] = source[(y * width) + x] * factor;
                    }
                }

                planes[c] = plane;
            }

            return Task.FromResult(new Image(planes, BitDepth.Float32, 1f, 0f, 0f, input.ImageMeta));
        }
    }

    /// <summary>A deblurrer that serves a colour image and no mono one, as a model that takes three channels does.</summary>
    private sealed class ColourOnlyDeblurrer(string name) : IImageDeblurrer, IEnhancerAvailability
    {
        private readonly Scaling _scaling = new(name, 0.95f);

        public string Name => name;

        public bool CanServe(int channelCount, EnhanceOptions options) => channelCount == 3;

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default) => _scaling.EnhanceAsync(input, cancellationToken);
    }

    /// <summary>
    /// A scaling enhancer that also writes down what it was handed with each image: the options, and for a denoise the
    /// variant. What a verb hands its enhancers is then compared with what a step hands them (#2475's third review:
    /// <see cref="Scaling"/> reads neither, so a verb that mis-mapped a tuning option would still write the step's file).
    /// </summary>
    private sealed class Recording(string name, float factor, List<string> handed) : IStarRemover, IDenoiseEnhancer, IImageDeblurrer, INonStellarDeconvolver
    {
        private readonly Scaling _scaling = new(name, factor);

        public string Name => name;

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default) => _scaling.EnhanceAsync(input, cancellationToken);

        public Task<Image> EnhanceAsync(Image input, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        {
            handed.Add($"{name}: {Described(options)}");
            return _scaling.EnhanceAsync(input, cancellationToken);
        }

        public Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, EnhanceOptions options, IProgress<float>? progress = null,
            CancellationToken cancellationToken = default)
        {
            handed.Add($"{name}: variant {variant}, {Described(options)}");
            return _scaling.EnhanceAsync(input, cancellationToken);
        }

        // In invariant numbers, so the words are the same on every computer.
        private static string Described(EnhanceOptions options)
        {
            var (tuning, kernel) = (options.Tuning, options.Tuning?.Deconvolution);
            FormattableString said = $"backend {options.Backend}, gradient {options.Gradient}, deblurSharpen {tuning?.DeblurSharpen}, denoiseStrength {tuning?.DenoiseStrength}, denoiseIterations {tuning?.DenoiseIterations}, kernel {kernel?.FwhmRed}/{kernel?.FwhmGreen}/{kernel?.FwhmBlue} beta {kernel?.Beta} resample {kernel?.Resample}";
            return said.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A model resolver for which every model is installed, by its name alone: nothing is loaded here.</summary>
    private sealed class EveryModelPresent : IModelResolver
    {
        public string Resolve(string modelFileName) => modelFileName;

        public bool TryResolve(string modelFileName, out string? absolutePath)
        {
            absolutePath = modelFileName;
            return true;
        }
    }

    /// <summary>A model resolver for which no model is installed: asked for one outright, it says so.</summary>
    private sealed class NoModelPresent : IModelResolver
    {
        public string Resolve(string modelFileName) => throw new FileNotFoundException($"{modelFileName} is not installed", modelFileName);

        public bool TryResolve(string modelFileName, out string? absolutePath)
        {
            absolutePath = null;
            return false;
        }
    }

    /// <summary>An RC-Astro CLI that is installed, and licensed or not, with the model and the version of itself it
    /// says it has, if it says any. Nothing is ever run through it.</summary>
    private sealed class StandInCli(bool licensed, string? model = null, string? cliVersion = null) : IRcAstroCli
    {
        /// <summary>How many times the licence was asked: each is a subprocess on a real CLI.</summary>
        public int Probes { get; set; }

        public string? ExecutablePath => "/stand-in/rc-astro";

        public bool IsAvailable => true;

        public bool IsLicensed(string productKey)
        {
            Probes++;
            return licensed;
        }

        public string? CliVersion => cliVersion;

        public string? ModelVersion(string productKey) => model;

        public Task<RcAstroRunResult> RunAsync(string productKey, string inputPath, string outputPath, IReadOnlyList<string> extraArgs,
            IProgress<RcAstroProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no product is run by this test");
    }

    private static void ShouldBeTheSameFile(string actual, string expected, string what)
    {
        var (a, e) = (File.ReadAllBytes(actual), File.ReadAllBytes(expected));
        var at = a.AsSpan().CommonPrefixLength(e);
        if (at == a.Length && at == e.Length)
        {
            return;
        }

        // A FITS header is 80-character cards, so the card holding the first difference says what differs.
        var card = at / 80 * 80;
        string Card(byte[] bytes) => card + 80 <= bytes.Length ? Encoding.ASCII.GetString(bytes, card, 80).TrimEnd() : "(past its end)";
        Assert.Fail($"{what}: the step's file and the verb's differ at byte {at} (lengths {a.Length} and {e.Length}). "
            + $"The 80 bytes there read '{Card(a)}' in the step's and '{Card(e)}' in the verb's.");
    }

    [Fact(Timeout = 600_000)]
    public async Task TheEnhanceStepIsTheFileImageSharpenWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("parity-enhance-").FullName;
        var master = Path.Combine(folder, "master.fits");
        new Image([SyntheticStarFieldRenderer.Render(256, 256, defocusSteps: 0, exposureSeconds: 10, skyBackground: 100, readNoise: 5,
            starCount: 80, seed: 5, noiseSeed: 1)], BitDepth.Float32, 65535f, 0f, 0f, new ImageMeta()).WriteToFitsFile(master);
        // One pipeline for the step and for the verb: a result is only the same file when the same enhancers made it.
        var pipeline = new SharpenPipeline(
            starRemover: new Scaling("Test/Remover", 0.5f), denoiser: new Scaling("Test/Denoiser", 0.8f), gradientCorrector: new ClassicalBackgroundExtractor());
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("plain", master, WorkbenchFixtures.Edit(EnhanceOperation.Id)),
            WorkbenchFixtures.Picture("pared", master, WorkbenchFixtures.Edit(EnhanceOperation.Id,
                new JsonObject { ["gradient"] = false, ["denoise"] = false, ["mode"] = "screen" })),
            WorkbenchFixtures.Picture("tuned", master, WorkbenchFixtures.Edit(EnhanceOperation.Id,
                new JsonObject { ["gradientBackend"] = "classical", ["denoiseBlend"] = 0.5, ["denoiseStrength"] = 0.3, ["denoiseIterations"] = 1 }))), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new EnhanceOperation(pipeline)]));

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        report.Images.Select(static image => image.Steps[0].Key).Distinct().Count().ShouldBe(3, "three settings are three steps");
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var image = new ImageSubCommand(
            host,
            pipeline,
            Substitute.For<IStarRemover>(),
            Substitute.For<IGradientCorrector>(),
            Substitute.For<IBackgroundExtractor>(),
            new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance)).Build();
        foreach (var each in report.Images)
        {
            var step = each.Steps[0];
            var line = step.CommandLine.ToArray();
            line[..3].ShouldBe([StepCommandLine.Program, "image", "sharpen"]);
            var written = Path.Combine(folder, each.Name + "-verb.fits");
            line[Array.IndexOf(line, "-o") + 1] = written;
            output.WriteLine(StepCommandLine.Display(line));
            said.Clear();

            var exit = await image.Parse(line[2..]).InvokeAsync(cancellationToken: ct);

            exit.ShouldBe(0, string.Join(Environment.NewLine, said));
            ShouldBeTheSameFile(step.Output.ShouldNotBeNull(), written, each.Name);
        }

        // And the three are three images: a setting that changes nothing would make the comparison above empty.
        var plain = WorkbenchFixtures.Read(report.Images[0].Steps[0].Output.ShouldNotBeNull());
        var pared = WorkbenchFixtures.Read(report.Images[1].Steps[0].Output.ShouldNotBeNull());
        var tuned = WorkbenchFixtures.Read(report.Images[2].Steps[0].Output.ShouldNotBeNull());
        plain[0, 100, 100].ShouldNotBe(pared[0, 100, 100]);
        plain[0, 100, 100].ShouldNotBe(tuned[0, 100, 100]);
    }

    // One star field in three planes, green above red and blue, so its stars carry the green excess SCNR takes out.
    // Its colour is stated as balanced and in the pixels (WBAPPLD): the pipeline runs SCNR only on balanced colour,
    // where a neutral star has R = G = B, and passes it over on any other plate, so without the cards the stated mode
    // would change nothing here.
    private static Image Colour()
    {
        const int size = 256;
        var plane = SyntheticStarFieldRenderer.Render(size, size, defocusSteps: 0, exposureSeconds: 10, skyBackground: 100, readNoise: 5,
            starCount: 80, seed: 5, noiseSeed: 1);
        float[,] Scaled(float by)
        {
            var scaled = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    scaled[y, x] = plane[y, x] * by;
                }
            }

            return scaled;
        }

        var meta = new ImageMeta { ColourCalibration = new ColourCalibration(1f, 1f, 1f, ColourCalibrationSource.Spcc, Applied: true) };
        return new Image([Scaled(1f), Scaled(1.3f), Scaled(0.9f)], BitDepth.Float32, 65535f * 1.3f, 0f, 0f, meta);
    }

    private static bool Differ(Image one, Image other)
    {
        for (var c = 0; c < one.ChannelCount; c++)
        {
            if (!one.GetChannelSpan(c).SequenceEqual(other.GetChannelSpan(c)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// With a deblurrer live (#2475's review): the two settings <c>image sharpen</c> passes on only when their option
    /// was typed, the starless deconvolution's blend and the SCNR mode, are held to the verb by the same comparison.
    /// A verb that dropped either would run another program and write another file. So is the SCNR's strength with
    /// its mode left to the program, which the step's line has to carry by itself. A colour input, since SCNR does
    /// nothing to one plane.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task WithADeblurrerAStatedDeconvolutionAndAStatedScnrAreTheFilesImageSharpenWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("parity-enhance-deblur-").FullName;
        var master = Path.Combine(folder, "colour.fits");
        Colour().WriteToFitsFile(master);
        var pipeline = new SharpenPipeline(
            starRemover: new Scaling("Test/Remover", 0.5f), nonStellarDeconvolver: new Scaling("Test/Deconvolver", 0.9f),
            denoiser: new Scaling("Test/Denoiser", 0.8f), deblurrer: new Scaling("Test/Deblurrer", 0.95f));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("program", master, WorkbenchFixtures.Edit(EnhanceOperation.Id)),
            WorkbenchFixtures.Picture("deconvolved", master, WorkbenchFixtures.Edit(EnhanceOperation.Id, new JsonObject { ["deconvolveBlend"] = 0.6 })),
            WorkbenchFixtures.Picture("fringed", master, WorkbenchFixtures.Edit(EnhanceOperation.Id, new JsonObject { ["scnr"] = "none" })),
            // The mode left to the program and its strength stated: the program's own SCNR, at a half.
            WorkbenchFixtures.Picture("half", master, WorkbenchFixtures.Edit(EnhanceOperation.Id, new JsonObject { ["scnrAmount"] = 0.5 }))), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new EnhanceOperation(pipeline)]));

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var image = new ImageSubCommand(
            host,
            pipeline,
            Substitute.For<IStarRemover>(),
            Substitute.For<IGradientCorrector>(),
            Substitute.For<IBackgroundExtractor>(),
            new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance)).Build();
        foreach (var each in report.Images)
        {
            var step = each.Steps[0];
            var line = step.CommandLine.ToArray();
            var written = Path.Combine(folder, each.Name + "-verb.fits");
            line[Array.IndexOf(line, "-o") + 1] = written;
            output.WriteLine(StepCommandLine.Display(line));
            said.Clear();

            var exit = await image.Parse(line[2..]).InvokeAsync(cancellationToken: ct);

            exit.ShouldBe(0, string.Join(Environment.NewLine, said));
            ShouldBeTheSameFile(step.Output.ShouldNotBeNull(), written, each.Name);
        }

        // Each line says what its settings state and nothing they leave to the program.
        LineOf("program").ShouldNotContain("--deconv-blend");
        LineOf("program").ShouldNotContain("--scnr");
        LineOf("deconvolved").ShouldContain("--deconv-blend");
        LineOf("fringed").ShouldContain("--scnr");
        LineOf("half").ShouldNotContain("--scnr");
        LineOf("half").ShouldContain("--scnr-amount");

        // And they are four programs: a stated blend keeps the starless deconvolution a deblurrer would drop, a stated
        // mode takes out the SCNR the program runs with one, and a stated strength runs the program's own SCNR at it.
        // Equal files would make the comparison above empty.
        var program = WorkbenchFixtures.Read(Result("program"));
        Differ(program, WorkbenchFixtures.Read(Result("deconvolved"))).ShouldBeTrue("the starless deconvolution ran where its blend was stated");
        Differ(program, WorkbenchFixtures.Read(Result("fringed"))).ShouldBeTrue("the SCNR did not run where its mode was stated as none");
        Differ(program, WorkbenchFixtures.Read(Result("half"))).ShouldBeTrue("the program's own SCNR ran at the strength stated");
        Differ(WorkbenchFixtures.Read(Result("fringed")), WorkbenchFixtures.Read(Result("half"))).ShouldBeTrue("and a half of it is not none of it");

        string[] LineOf(string name) => [.. report.Images.Single(each => each.Name == name).Steps[0].CommandLine];
        string Result(string name) => report.Images.Single(each => each.Name == name).Steps[0].Output.ShouldNotBeNull();
    }

    [Fact]
    public void TheSettingsMakeTheOptionsTheVerbMakesAndRefuseWhatTheVerbRefuses()
    {
        var enhance = new EnhanceOperation(new SharpenPipeline());

        EnhanceOperation.TryOptions(new EnhanceSettings(), out var plain, out _).ShouldBeTrue();
        plain.ShouldBe(EnhanceOptions.Default, "no override at all is no tuning, as EnhanceOptions.TryParse has it");
        EnhanceOperation.TryOptions(new EnhanceSettings { DenoiseStrength = 0.4f, GradientBackend = GradientBackend.Classical }, out var tuned, out _).ShouldBeTrue();
        tuned.Tuning.ShouldNotBeNull().DenoiseStrength.ShouldBe(0.4f);
        tuned.Gradient.ShouldBe(GradientBackend.Classical);

        enhance.Refusal(new EnhanceSettings()).ShouldBeNull();
        enhance.Refusal(new EnhanceSettings { Gradient = false, GradientBackend = GradientBackend.Classical }).ShouldNotBeNull().ShouldContain("one or the other");
        enhance.Refusal(new EnhanceSettings { WhiteBalance = [1f, 1f] }).ShouldNotBeNull().ShouldContain("three positive numbers");
        enhance.Refusal(new EnhanceSettings { WhiteBalance = [1f, 0f, 1f] }).ShouldNotBeNull().ShouldContain("three positive numbers");
        enhance.Refusal(new EnhanceSettings { DenoiseBlend = 1.5f }).ShouldNotBeNull().ShouldContain("0 to 1");
        enhance.Refusal(new EnhanceSettings { DeconvBeta = 2.5 }).ShouldNotBeNull("a beta with no kernel is the kernel parser's to refuse");
        // What the verb reads as "not stated" is refused, so no step keeps a value its own command line would drop.
        enhance.Refusal(new EnhanceSettings { DenoiseIterations = 0 }).ShouldNotBeNull().ShouldContain("denoiseIterations is 1 or more");
        enhance.Refusal(new EnhanceSettings { DenoiseIterations = 1 }).ShouldBeNull();
        enhance.Refusal(new EnhanceSettings { DeblurSharpen = -1f }).ShouldNotBeNull().ShouldContain("deblurSharpen is 0 to 1");
        enhance.Refusal(new EnhanceSettings { DeblurSharpen = 1.5f }).ShouldNotBeNull().ShouldContain("deblurSharpen is 0 to 1");
        enhance.Refusal(new EnhanceSettings { DenoiseStrength = -0.5f }).ShouldNotBeNull().ShouldContain("denoiseStrength is 0 to 1");
        enhance.Refusal(new EnhanceSettings { DeblurSharpen = 0f, DenoiseStrength = 1f }).ShouldBeNull();

        // What serves is in the key: the pipeline names the enhancer of every role the settings keep, "none" where
        // there is none, and "left out" for a role they switch off (the stellar sharpening is off unless asked for).
        enhance.ServedBy(new EnhanceSettings()).ShouldBe("deblur none; gradient none; stars none; stellar left out; deconvolve none; denoise none");
        new EnhanceOperation(new SharpenPipeline(denoiser: new Scaling("Test/Denoiser", 1f))).ServedBy(new EnhanceSettings()).ShouldContain("denoise Test/Denoiser");
    }

    [Fact]
    public void AnInHouseEnhancersNameCarriesItsModelFileSoARetrainIsAnotherStep()
    {
        // No model is on these paths and none is loaded: a name is asked of an enhancer that has run nothing.
        var resolver = new ModelResolver([_folders.Create("no-models-").FullName]);
        using var denoiser = new N2nDenoiser(resolver);
        using var deblurrer = new OnnxTianWenDeconvolver(resolver);

        denoiser.Name.ShouldContain(N2nDenoiser.ModelFileName);
        deblurrer.Name.ShouldContain(OnnxTianWenDeconvolver.ModelFileName);
        var served = new EnhanceOperation(new SharpenPipeline(denoiser: denoiser, deblurrer: deblurrer)).ServedBy(new EnhanceSettings());
        // What served an enhance names the model, so a retrained one is another key.
        served.ShouldContain(N2nDenoiser.ModelFileName);
        served.ShouldContain(OnnxTianWenDeconvolver.ModelFileName);
    }

    // The program the settings make of a set of roles, with what was said on the way. No enhancer runs: the program is
    // read off what serves, which is handed in.
    private static async Task<(LinearEnhanceProgram Program, List<string> Said)> ProgramAsync(EnhanceCapabilities capabilities, EnhanceSettings settings,
        bool stretchedInPipeline = false, bool recombine = true)
    {
        var said = new List<string>();
        var start = new EnhanceStart(WorkbenchFixtures.Master(seed: 1), EnhanceOptions.Default, capabilities);
        var program = await new EnhanceOperation(new SharpenPipeline()).ProgramAsync(start, settings, stretchedInPipeline, recombine, said.Add,
            TestContext.Current.CancellationToken);
        return (program, said);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheProgramKeepsWhatTheSettingsStateAndOnlyWhatServes()
    {
        var (withDeblur, noDeblur) = (EnhanceCapabilities.AllRoles(deblur: true), EnhanceCapabilities.AllRoles(deblur: false));

        // The starless deconvolution: dropped where a deblurrer serves, unless its blend is stated outright.
        (await ProgramAsync(withDeblur, new EnhanceSettings())).Program.DeconvolveStarless.ShouldBeFalse();
        var asked = (await ProgramAsync(withDeblur, new EnhanceSettings { DeconvolveBlend = 0.6f })).Program;
        (asked.DeconvolveStarless, asked.DeconvolveBlend).ShouldBe((true, 0.6f));
        (await ProgramAsync(noDeblur, new EnhanceSettings())).Program.DeconvolveStarless.ShouldBeTrue();
        (await ProgramAsync(noDeblur, new EnhanceSettings { Deconvolve = false })).Program.DeconvolveStarless.ShouldBeFalse();
        (await ProgramAsync(withDeblur, new EnhanceSettings { Deconvolve = false, DeconvolveBlend = 0.6f })).Program.DeconvolveStarless.ShouldBeFalse();

        // SCNR follows the program (on with a deblurrer, or where the plates are stretched in the pipeline) until stated.
        (await ProgramAsync(withDeblur, new EnhanceSettings())).Program.Scnr.ShouldBe(ScnrMode.Average);
        (await ProgramAsync(noDeblur, new EnhanceSettings())).Program.Scnr.ShouldBe(ScnrMode.None);
        (await ProgramAsync(noDeblur, new EnhanceSettings(), stretchedInPipeline: true)).Program.Scnr.ShouldBe(ScnrMode.Average);
        (await ProgramAsync(withDeblur, new EnhanceSettings { Scnr = ScnrMode.None })).Program.Scnr.ShouldBe(ScnrMode.None);
        var stated = (await ProgramAsync(noDeblur, new EnhanceSettings { Scnr = ScnrMode.Maximum, ScnrAmount = 0.5f })).Program;
        (stated.Scnr, stated.ScnrAmount).ShouldBe((ScnrMode.Maximum, 0.5f));

        // Stellar sharpening is asked for, and left out and said where a deblurrer is live or no sharpener serves.
        (await ProgramAsync(noDeblur, new EnhanceSettings())).Program.StellarSharpen.ShouldBeFalse("it is opt-in");
        (await ProgramAsync(noDeblur, new EnhanceSettings { StellarSharpen = true, StellarBlend = 0.4f })).Program.StellarSharpen.ShouldBeTrue();
        var overDeblur = await ProgramAsync(withDeblur, new EnhanceSettings { StellarSharpen = true });
        overDeblur.Program.StellarSharpen.ShouldBeFalse();
        overDeblur.Said.ShouldContain(line => line.Contains("stellar sharpening ignored: a deblurrer is live"));
        var noSharpener = await ProgramAsync(noDeblur with { StellarSharpen = false }, new EnhanceSettings { StellarSharpen = true });
        noSharpener.Program.StellarSharpen.ShouldBeFalse();
        noSharpener.Said.ShouldContain(line => line.Contains("no stellar sharpener is available"));

        // With no star remover the program is whole-frame: a stated SCNR is said to be ignored, and no step asks a role
        // that does not serve.
        var wholeFrame = await ProgramAsync(noDeblur with { StarRemoval = false }, new EnhanceSettings { Scnr = ScnrMode.Average });
        wholeFrame.Said.ShouldContain(line => line.Contains("scnr ignored"));
        wholeFrame.Program.ToSteps().ToArray().ShouldBe([new GradientCorrectionStep(), new DenoiseFrameStep()]);
        (await ProgramAsync(new EnhanceCapabilities(false, false, false, false, false, false), new EnhanceSettings())).Program.ToSteps().ShouldBeEmpty();

        // What a caller clears, and a stretch inside the pipeline: each plate by itself, and a screen of stretched plates.
        (await ProgramAsync(noDeblur, new EnhanceSettings(), recombine: false)).Program.Recombine.ShouldBeFalse();
        (await ProgramAsync(noDeblur, new EnhanceSettings(), stretchedInPipeline: true)).Program.RecombineMode.ShouldBe(RecombineMode.Screen);
        var screened = (await ProgramAsync(noDeblur, new EnhanceSettings { Mode = RecombineMode.Screen })).Program;
        (screened.SplitMode, screened.RecombineMode).ShouldBe((RecombineMode.Screen, RecombineMode.Screen));
        var pared = (await ProgramAsync(noDeblur, new EnhanceSettings { Gradient = false, Denoise = false })).Program;
        (pared.GradientCorrection, pared.Denoise).ShouldBe((false, false));
    }

    [Fact(Timeout = 60_000)]
    public async Task TheProgramSaysWhatServesEachOfItsSteps()
    {
        // What the automatic backends resolved to is said once, for the verb and for a step alike: each role the
        // program asks by its enhancer's name, "left out" where no step asks it, and the SCNR the stars end on.
        var enhance = new EnhanceOperation(new SharpenPipeline(
            starRemover: new Scaling("Test/Remover", 0.5f), denoiser: new Scaling("Test/Denoiser", 0.8f), deblurrer: new Scaling("Test/Deblurrer", 0.95f)));
        var said = new List<string>();
        var split = new EnhanceStart(WorkbenchFixtures.Master(seed: 1), EnhanceOptions.Default,
            EnhanceCapabilities.AllRoles(deblur: true) with { GradientCorrection = false });

        await enhance.ProgramAsync(split, new EnhanceSettings(), stretchedInPipeline: false, recombine: true, said.Add, TestContext.Current.CancellationToken);

        said.ShouldContain("served by: deblur Test/Deblurrer; gradient left out; stars Test/Remover; stellar left out; deconvolve left out; "
            + "denoise Test/Denoiser; scnr average");

        // With no star remover the program is whole-frame: no step asks for the stars, and there is no SCNR.
        said.Clear();
        var wholeFrame = split with { Capabilities = EnhanceCapabilities.AllRoles(deblur: false) with { StarRemoval = false, GradientCorrection = false } };

        await enhance.ProgramAsync(wholeFrame, new EnhanceSettings(), stretchedInPipeline: false, recombine: true, said.Add, TestContext.Current.CancellationToken);

        said.ShouldContain("served by: deblur left out; gradient left out; stars left out; stellar left out; deconvolve left out; "
            + "denoise Test/Denoiser; scnr left out");
    }

    /// <summary>
    /// The enhance's default program, step for step, for both shapes of the split program. A change here is other
    /// pixels for the same settings and enhancers: it needs a new <c>Version</c> of <see cref="EnhanceOperation"/>, or a
    /// result of the old program is taken for the new one's.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheDefaultProgramIsPinnedSoAChangeToItIsANewVersionOfTheOperation()
    {
        const string why = "the default program changed: raise EnhanceOperation's Version with it (docs/architecture/workbench-operations.md)";
        new EnhanceOperation(new SharpenPipeline()).Description.Version.ShouldBe(1, "this test pins version 1's program: pin the new one with the new number");

        (await ProgramAsync(EnhanceCapabilities.AllRoles(deblur: false), new EnhanceSettings())).Program.ToSteps().ToArray().ShouldBe(
        [
            new GradientCorrectionStep(),
            new RemoveStarsStep(SplitMode: RecombineMode.Additive),
            new DeconvolveStarlessStep(Blend: 1f),
            new DenoiseStarlessStep(Blend: 1f, Variant: DenoiseVariant.Default),
            new RecombineStep(Mode: RecombineMode.Additive),
        ], why);

        (await ProgramAsync(EnhanceCapabilities.AllRoles(deblur: true), new EnhanceSettings())).Program.ToSteps().ToArray().ShouldBe(
        [
            new DeblurStep(Blend: 1f),
            new GradientCorrectionStep(),
            new RemoveStarsStep(SplitMode: RecombineMode.Additive),
            new DenoiseStarlessStep(Blend: 1f, Variant: DenoiseVariant.Default),
            new ScnrStarsStep(Mode: ScnrMode.Average, Amount: 1f),
            new RecombineStep(Mode: RecombineMode.Additive),
        ], why);
    }

    [Fact]
    public void WhatServesIsNamedUnderTheStepsOwnBackendSoAnInHouseModelIsInTheKeyWhereRcAstroIsLicensed()
    {
        // No model is on these paths and none is loaded: a name is asked of an enhancer that has run nothing. The
        // denoise role is the deferred one a host registers, which picks its backend when it is asked.
        var resolver = new ModelResolver([_folders.Create("no-models-").FullName]);
        using var inHouse = new N2nDenoiser(resolver);
        EnhanceOperation Over(bool licensed) => new(new SharpenPipeline(
            denoiser: new DeferredDenoiser(new StandInCli(licensed), () => new Scaling("RC-Astro NoiseXTerminator (nxt)", 1f), () => inHouse)));

        // RC-Astro licensed: Auto runs it, and a step that says the in-house backend runs the in-house model and is
        // keyed by it, so a retrained model is another key on this computer too.
        Over(licensed: true).ServedBy(new EnhanceSettings()).ShouldContain("denoise RC-Astro NoiseXTerminator (nxt)");
        var inHouseStep = Over(licensed: true).ServedBy(new EnhanceSettings { Backend = EnhanceBackend.TianWen });
        inHouseStep.ShouldContain($"denoise {inHouse.Name}");
        inHouseStep.ShouldContain(N2nDenoiser.ModelFileName);
        inHouseStep.ShouldNotContain("NoiseXTerminator");

        // Not licensed: Auto falls to the in-house model, and a step that forces RC-Astro is keyed by RC-Astro, which
        // is what would run.
        Over(licensed: false).ServedBy(new EnhanceSettings()).ShouldContain(N2nDenoiser.ModelFileName);
        Over(licensed: false).ServedBy(new EnhanceSettings { Backend = EnhanceBackend.ForceRcAstro }).ShouldContain("denoise RC-Astro NoiseXTerminator (nxt)");
    }

    [Fact]
    public void AnRcAstroStepIsKeyedByItsModelAndNotByTheCliThatRunsItAndSaysWhoseModelsServeIt()
    {
        var resolver = new ModelResolver([_folders.Create("no-models-").FullName]);
        using var inHouse = new N2nDenoiser(resolver);
        EnhanceOperation Over(string model, string cliVersion, bool licensed = true)
        {
            var cli = new StandInCli(licensed, model, cliVersion);
            return new(new SharpenPipeline(denoiser: new DeferredDenoiser(cli, () => new RcAstroDenoiser(cli), () => inHouse)));
        }

        var served = Over("3.1", "2.6.11").ServedBy(new EnhanceSettings());
        served.ShouldContain("denoise RC-Astro NoiseXTerminator (nxt) ML3.1");
        // A newer model is other pixels, so another key. A newer CLI that runs the same model is not, and is not named.
        Over("3.2", "2.6.11").ServedBy(new EnhanceSettings()).ShouldNotBe(served);
        Over("3.1", "2.7.0").ServedBy(new EnhanceSettings()).ShouldBe(served);
        served.ShouldNotContain("2.6.11");

        // Whose models, in one word: RC-Astro's under Auto where it is licensed, TianWen's own where the step says so
        // or RC-Astro is not licensed.
        Over("3.1", "2.6.11").ServedTag(new EnhanceSettings()).ShouldBe("rc");
        Over("3.1", "2.6.11").ServedTag(new EnhanceSettings { Backend = EnhanceBackend.TianWen }).ShouldBe("tw");
        Over("3.1", "2.6.11", licensed: false).ServedTag(new EnhanceSettings()).ShouldBe("tw");
        // Stand-ins say nothing of whose they are, so there is no word.
        new EnhanceOperation(new SharpenPipeline(denoiser: new Scaling("Test/Denoiser", 1f))).ServedTag(new EnhanceSettings()).ShouldBeEmpty();
    }

    [Fact]
    public void TheOneWordForWhoseModelsServeIsRcTwMixOrNothing()
    {
        var (rc, tw) = (new EnhancerOrigin(EnhancerOrigin.RcAstro, "2.6.11"), new EnhancerOrigin(EnhancerOrigin.TianWen));

        EnhancerOrigin.TagOf([]).ShouldBeEmpty();
        // A role that does not say whose it is (the gradient corrector, a stand-in) counts for neither.
        EnhancerOrigin.TagOf([null, null]).ShouldBeEmpty();
        EnhancerOrigin.TagOf([rc, null, rc]).ShouldBe("rc");
        EnhancerOrigin.TagOf([tw]).ShouldBe("tw");
        EnhancerOrigin.TagOf([rc, tw, null]).ShouldBe("mix");
    }

    [Fact]
    public void AnEnhanceResultsHeaderSaysWhatServedEachStepWhoseModelsAndTheToolThatRanThem()
    {
        var cli = new StandInCli(licensed: true, model: "3.1", cliVersion: "2.6.11");
        var enhance = new EnhanceOperation(new SharpenPipeline(
            starRemover: new Scaling("Test/Remover", 0.5f),
            denoiser: new DeferredDenoiser(cli, () => new RcAstroDenoiser(cli)),
            gradientCorrector: new ClassicalBackgroundExtractor()));
        // The split program with no deblurrer, no stellar sharpener and no deconvolver: gradient, stars, denoise.
        var program = LinearEnhanceProgram.For(new EnhanceCapabilities(
            Deblur: false, GradientCorrection: true, StarRemoval: true, StellarSharpen: false, Deconvolve: false, Denoise: true));

        var header = enhance.HeaderOf(EnhanceOptions.Default, program);

        header["SWMODIFY"].Value.ShouldBe(SharpenPipeline.SoftwareModifier);
        header["ENHSTARS"].Value.ShouldBe("Test/Remover");
        header["ENHDENOI"].Value.ShouldBe("RC-Astro NoiseXTerminator (nxt) ML3.1");
        header.ShouldContainKey("ENHGRAD");
        header["ENHBY"].Value.ShouldBe("rc");
        // The CLI's version is said in the file and is no part of a name or a key.
        header["ENHTOOL"].Value.ShouldBe("rc-astro 2.6.11");
        // A role no step of the program asks has no card.
        header.ShouldNotContainKey("ENHDEBLR");
        header.ShouldNotContainKey("ENHSTELL");
        header.ShouldNotContainKey("ENHDECON");

        // A name longer than a FITS string card holds is cut to what it holds, never refused by the writer.
        var wordy = new EnhanceOperation(new SharpenPipeline(denoiser: new Scaling(new string('n', 90), 1f)));
        var wholeFrame = LinearEnhanceProgram.For(new EnhanceCapabilities(false, false, false, false, false, Denoise: true));
        var cut = wordy.HeaderOf(EnhanceOptions.Default, wholeFrame);
        cut["ENHDENOI"].Value.ShouldBe(new string('n', EnhanceOperation.MaxCardText));
        // Stand-ins say nothing of whose they are: no word and no tool.
        cut.ShouldNotContainKey("ENHBY");
        cut.ShouldNotContainKey("ENHTOOL");
    }

    [Fact]
    public void ANameLongerThanACardSurvivesTheRealWriterCutAtItsLimitWithItsQuoteIntact()
    {
        var path = Path.Combine(_folders.Create("long-card-").FullName, "out.fits");
        var wordy = new EnhanceOperation(new SharpenPipeline(denoiser: new Scaling(new string('n', 90), 1f)));
        var program = LinearEnhanceProgram.For(new EnhanceCapabilities(false, false, false, false, false, Denoise: true));
        var image = new Image([SyntheticStarFieldRenderer.Render(64, 64, defocusSteps: 0, exposureSeconds: 10, skyBackground: 100, readNoise: 5,
            starCount: 5, seed: 5, noiseSeed: 1)], BitDepth.Float32, 65535f, 0f, 0f, new ImageMeta());

        image.WriteToFitsFile(path, null, wordy.HeaderOf(EnhanceOptions.Default, program));

        // A header is 80-character cards: the card is read off the file the writer made, not off the dictionary.
        var bytes = File.ReadAllBytes(path);
        var cards = Enumerable.Range(0, Math.Min(bytes.Length, 36 * 2880) / 80).Select(i => Encoding.ASCII.GetString(bytes, i * 80, 80)).ToArray();
        var card = cards.Single(c => c.StartsWith("ENHDENOI=", StringComparison.Ordinal));
        card.ShouldContain($"'{new string('n', EnhanceOperation.MaxCardText)}'");
        card.ShouldNotContain(new string('n', EnhanceOperation.MaxCardText + 1));
        cards.ShouldContain(c => c.StartsWith("SWMODIFY=", StringComparison.Ordinal));
    }

    [Fact]
    public void ARefusingRcAstroIsAskedOncePerResolutionAndTheKeyTextAndTheWordAgree()
    {
        var resolver = new ModelResolver([_folders.Create("no-models-").FullName]);
        using var inHouse = new N2nDenoiser(resolver);
        var cli = new StandInCli(licensed: false, model: "3.1");
        var denoiser = new DeferredDenoiser(cli, () => new RcAstroDenoiser(cli), () => inHouse);

        // The enhance step: one resolution of its roles, text and word together.
        var enhance = new EnhanceOperation(new SharpenPipeline(denoiser: denoiser));
        var (by, tag) = enhance.Served(new EnhanceSettings());
        // One ask for the one RC-Astro role the settings name, however many words are made of its answer.
        cli.Probes.ShouldBe(1);
        by.ShouldContain(N2nDenoiser.ModelFileName);
        tag.ShouldBe("tw");

        // A one-role step asks once too, and text and word are those of the one answer.
        var denoise = new DenoiseOperation(denoiser);
        cli.Probes = 0;
        var (name, word) = denoise.Served(new DenoiseSettings());
        cli.Probes.ShouldBe(1);
        name.ShouldContain(N2nDenoiser.ModelFileName);
        word.ShouldBe("tw");
    }

    [Fact(Timeout = 60_000)]
    public async Task AStatusSaysWhoseModelsServeAStepInOneWordBesideItsName()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("served-tag-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", master, WorkbenchFixtures.Edit(DenoiseOperation.Id))), ct);
        var cli = new StandInCli(licensed: true, model: "3.1");
        var catalog = new OperationCatalog([new DenoiseOperation(new DeferredDenoiser(cli, () => new RcAstroDenoiser(cli)))]);
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        var workbench = new WorkbenchSubCommand(host, catalog, new WorkbenchRunner(catalog)).Build();

        (await workbench.Parse(["status", folder]).InvokeAsync(cancellationToken: ct)).ShouldBe(0, string.Join(Environment.NewLine, said));

        said.ShouldContain(line => line.Contains("1 denoise [rc]: will run"), string.Join(Environment.NewLine, said));
        said.ShouldContain("[workbench]     served by: RC-Astro NoiseXTerminator (nxt) ML3.1");
    }

    [Fact]
    public void TheGradientRoleIsNamedUnderTheStepsOwnGradientBackend()
    {
        // The corrector a host registers, on a computer where GraXpert's weights are installed. Nothing is loaded:
        // a name is asked of two correctors that have run nothing.
        using var graXpert = new GraXpertBackgroundExtractor(new EveryModelPresent());
        var classical = new ClassicalBackgroundExtractor();
        var enhance = new EnhanceOperation(new SharpenPipeline(gradientCorrector: new FallbackGradientCorrector(new EveryModelPresent(), graXpert, classical)));

        // Auto picks GraXpert here, and a step that says the classical fit is keyed by the classical fit, which is what
        // runs: its key does not name GraXpert, and does not move when GraXpert is installed or removed.
        enhance.ServedBy(new EnhanceSettings()).ShouldContain($"gradient {graXpert.Name}");
        var classicalStep = enhance.ServedBy(new EnhanceSettings { GradientBackend = GradientBackend.Classical });
        classicalStep.ShouldContain($"gradient {classical.Name}");
        classicalStep.ShouldNotContain("GraXpert");
        enhance.ServedBy(new EnhanceSettings { GradientBackend = GradientBackend.Graxpert }).ShouldContain($"gradient {graXpert.Name}");

        // The other half, on a computer where GraXpert's weights are not installed. Auto is the classical fit there, and
        // a step that says GraXpert is still keyed by GraXpert, which is what its run asks for (and fails on, naming
        // where it looked): with the weights present, as above, auto picks GraXpert too, so that half alone passes
        // whether or not the step's own backend is asked.
        using var absent = new GraXpertBackgroundExtractor(new NoModelPresent());
        var without = new EnhanceOperation(new SharpenPipeline(gradientCorrector: new FallbackGradientCorrector(new NoModelPresent(), absent, classical)));
        var autoStep = without.ServedBy(new EnhanceSettings());
        autoStep.ShouldContain($"gradient {classical.Name}");
        autoStep.ShouldNotContain("GraXpert");
        without.ServedBy(new EnhanceSettings { GradientBackend = GradientBackend.Graxpert }).ShouldContain($"gradient {absent.Name}");
    }

    [Fact]
    public void ARoleTheSettingsLeaveOutIsNoPartOfTheStepsKey()
    {
        // The corrector a host registers, on a computer with GraXpert's weights and on one without, and a denoiser.
        var classical = new ClassicalBackgroundExtractor();
        using var present = new GraXpertBackgroundExtractor(new EveryModelPresent());
        using var absent = new GraXpertBackgroundExtractor(new NoModelPresent());
        EnhanceOperation Over(IModelResolver models, GraXpertBackgroundExtractor graXpert) => new(new SharpenPipeline(
            gradientCorrector: new FallbackGradientCorrector(models, graXpert, classical), denoiser: new Scaling("Test/Denoiser", 1f)));
        var (installed, notInstalled) = (Over(new EveryModelPresent(), present), Over(new NoModelPresent(), absent));

        // A step that keeps its gradient is keyed by what corrects it, so the two computers differ.
        installed.ServedBy(new EnhanceSettings()).ShouldNotBe(notInstalled.ServedBy(new EnhanceSettings()));

        // A step that leaves the gradient out runs no corrector: its key names none, and is the same on both, so
        // installing or removing GraXpert runs no such step again.
        var noGradient = new EnhanceSettings { Gradient = false };
        installed.ServedBy(noGradient).ShouldBe(notInstalled.ServedBy(noGradient));
        installed.ServedBy(noGradient).ShouldContain("gradient left out");
        installed.ServedBy(noGradient).ShouldNotContain("GraXpert");

        // The same for every role with a switch: a denoise and a deconvolution left out are keyed by no enhancer, and
        // the stellar sharpening, off unless asked for, by none until it is asked for.
        installed.ServedBy(new EnhanceSettings()).ShouldContain("denoise Test/Denoiser");
        installed.ServedBy(new EnhanceSettings { Denoise = false }).ShouldContain("denoise left out");
        installed.ServedBy(new EnhanceSettings { Deconvolve = false }).ShouldContain("deconvolve left out");
        installed.ServedBy(new EnhanceSettings()).ShouldContain("stellar left out");
        installed.ServedBy(new EnhanceSettings { StellarSharpen = true }).ShouldContain("stellar none");

        // The deblur and the star removal have no switch: they run wherever they serve, and are always named.
        installed.ServedBy(new EnhanceSettings { Gradient = false, Denoise = false, Deconvolve = false })
            .ShouldBe("deblur none; gradient left out; stars none; stellar left out; deconvolve left out; denoise left out");
    }

    [Fact]
    public void WhatADeblurrerOnEveryImageLeavesOutIsNoPartOfTheStepsKey()
    {
        // A whole-frame deblurrer that serves mono and colour alike runs on every image, and with it the starless
        // deconvolution runs only where its blend is stated and the stellar sharpening never. A status then names
        // neither, as a run names neither.
        static EnhanceOperation Over(IImageDeblurrer? deblurrer, string deconvolver = "Test/Deconvolver") => new(new SharpenPipeline(
            starRemover: new Scaling("Test/Remover", 0.5f), nonStellarDeconvolver: new Scaling(deconvolver, 0.9f),
            denoiser: new Scaling("Test/Denoiser", 0.8f), deblurrer: deblurrer));
        var always = Over(new Scaling("Test/Deblurrer", 0.95f));

        always.ServedBy(new EnhanceSettings()).ShouldBe(
            "deblur Test/Deblurrer; gradient none; stars Test/Remover; stellar left out; deconvolve left out; denoise Test/Denoiser");
        // Another deconvolver on that computer moves no key: it would never run.
        Over(new Scaling("Test/Deblurrer", 0.95f), deconvolver: "Test/Another").ServedBy(new EnhanceSettings()).ShouldBe(always.ServedBy(new EnhanceSettings()));
        // Its blend stated, the deconvolution is kept beside the deblur, and named.
        always.ServedBy(new EnhanceSettings { DeconvolveBlend = 0.6f }).ShouldContain("deconvolve Test/Deconvolver");
        always.ServedBy(new EnhanceSettings { StellarSharpen = true }).ShouldContain("stellar left out");

        // No deblurrer, or one that serves colour alone: on some image the deconvolution runs, so it is named.
        Over(deblurrer: null).ServedBy(new EnhanceSettings()).ShouldContain("deconvolve Test/Deconvolver");
        Over(new ColourOnlyDeblurrer("Test/ColourOnly")).ServedBy(new EnhanceSettings()).ShouldContain("deconvolve Test/Deconvolver");
        Over(new ColourOnlyDeblurrer("Test/ColourOnly")).ServedBy(new EnhanceSettings { StellarSharpen = true }).ShouldContain("stellar none");
    }

    [Fact(Timeout = 600_000)]
    public async Task TheVerbHandsEveryEnhancerTheOptionsAndTheVariantItsStepHandsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("parity-enhance-handed-").FullName;
        var master = Path.Combine(folder, "colour.fits");
        Colour().WriteToFitsFile(master);
        var handed = new List<string>();
        var pipeline = new SharpenPipeline(
            starRemover: new Recording("remover", 0.5f, handed), nonStellarDeconvolver: new Recording("deconvolver", 0.9f, handed),
            denoiser: new Recording("denoiser", 0.8f, handed), deblurrer: new Recording("deblurrer", 0.95f, handed));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("tuned", master, WorkbenchFixtures.Edit(EnhanceOperation.Id, new JsonObject
            {
                ["backend"] = "tianWen",
                ["deblurSharpen"] = 0.4,
                ["denoiseStrength"] = 0.3,
                ["denoiseIterations"] = 2,
                ["denoiseVariant"] = "lite",
                ["deconvKernel"] = "0.77,0.91,0.98",
                ["deconvBeta"] = 3.5,
                ["deconvResample"] = 2,
                ["deconvolveBlend"] = 0.6,
            }))), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new EnhanceOperation(pipeline)]));

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        var byStep = handed.ToArray();
        handed.Clear();
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var image = new ImageSubCommand(
            host,
            pipeline,
            Substitute.For<IStarRemover>(),
            Substitute.For<IGradientCorrector>(),
            Substitute.For<IBackgroundExtractor>(),
            new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance)).Build();
        var tuned = report.Images[0].Steps[0];
        var line = tuned.CommandLine.ToArray();
        var written = Path.Combine(folder, "tuned-verb.fits");
        line[Array.IndexOf(line, "-o") + 1] = written;
        output.WriteLine(StepCommandLine.Display(line));

        var exit = await image.Parse(line[2..]).InvokeAsync(cancellationToken: ct);

        exit.ShouldBe(0, string.Join(Environment.NewLine, said));
        ShouldBeTheSameFile(tuned.Output.ShouldNotBeNull(), written, "tuned");
        var byVerb = handed.ToArray();
        foreach (var each in byStep)
        {
            output.WriteLine(each);
        }

        // What the verb handed each enhancer, call for call, is what the step handed it.
        byVerb.ShouldBe(byStep);
        // And that is what the settings state: two empty lists would agree as well.
        byStep.ShouldAllBe(each => each.Contains("backend TianWen") && each.Contains("deblurSharpen 0.4") && each.Contains("denoiseStrength 0.3")
            && each.Contains("denoiseIterations 2") && each.Contains("kernel 0.77/0.91/0.98 beta 3.5 resample 2"));
        byStep.Count(static each => each.StartsWith("denoiser: variant Lite", StringComparison.Ordinal)).ShouldBe(2, "two passes were stated, so two run");
        byStep.Count(static each => each.StartsWith("deblurrer:", StringComparison.Ordinal)).ShouldBe(1);
        byStep.Count(static each => each.StartsWith("deconvolver:", StringComparison.Ordinal)).ShouldBe(1, "its blend was stated, so it survives the deblurrer");

        // A value past an option's range is refused in the option's own name, and nothing is written or run.
        handed.Clear();
        said.Clear();
        var refusedTo = Path.Combine(folder, "refused.fits");
        (await image.Parse(new[] { "sharpen", master, "-o", refusedTo, "--deblur-sharpen", "1.5" }).InvokeAsync(cancellationToken: ct)).ShouldBe(1);
        string.Join(Environment.NewLine, said).ShouldContain("--deblur-sharpen is 0 to 1");
        File.Exists(refusedTo).ShouldBeFalse();
        handed.ShouldBeEmpty();
    }

    [Fact(Timeout = 60_000)]
    public async Task TheSharpenVerbSaysTheOperationsLinesInItsOwnOptions()
    {
        // Each line as the operation words it today, said as the verb says it: a wording that moves in the operation
        // fails here, where the pair that names it is.
        var enhance = new EnhanceOperation(new SharpenPipeline());
        string Refused(EnhanceSettings settings) => ImageSubCommand.InSharpenOptions(enhance.Refusal(settings).ShouldNotBeNull());

        Refused(new EnhanceSettings { DeblurSharpen = 1.5f }).ShouldStartWith("--deblur-sharpen is 0 to 1");
        Refused(new EnhanceSettings { DenoiseStrength = 1.5f }).ShouldStartWith("--denoise-strength is 0 to 1");
        Refused(new EnhanceSettings { DenoiseIterations = 0 }).ShouldStartWith("--denoise-iterations is 1 or more");
        Refused(new EnhanceSettings { WhiteBalance = [1f, 1f] }).ShouldStartWith("--white-balance is three positive numbers");
        Refused(new EnhanceSettings { Gradient = false, GradientBackend = GradientBackend.Classical }).ShouldStartWith("--gradient chooses");

        var overDeblur = await ProgramAsync(EnhanceCapabilities.AllRoles(deblur: true), new EnhanceSettings { StellarSharpen = true });
        overDeblur.Said.Select(ImageSubCommand.InSharpenOptions).ShouldContain(each => each.StartsWith("--stellar-sharpen ignored: a deblurrer is live"));
        var wholeFrame = await ProgramAsync(EnhanceCapabilities.AllRoles(deblur: false) with { StarRemoval = false }, new EnhanceSettings { Scnr = ScnrMode.Average });
        wholeFrame.Said.Select(ImageSubCommand.InSharpenOptions).ShouldContain(each => each.StartsWith("--scnr ignored"));
        var note = EnhanceOperation.ResolveWhiteBalance(new ColourCalibration(1f, 1f, 1f, ColourCalibrationSource.Spcc), [1.1f, 1f, 0.9f]).Note;
        // The note words its numbers in the current culture, so the expectation is built with the same format: the
        // STATED triple is the one named first, in any locale.
        ImageSubCommand.InSharpenOptions(note.ShouldNotBeNull()).ShouldStartWith($"--white-balance ({1.1f:F3}, {1f:F3}, {0.9f:F3}) replaces the input's own");

        // A line that names no setting is said as it is.
        ImageSubCommand.InSharpenOptions("wrote master.fits").ShouldBe("wrote master.fits");
    }
}
