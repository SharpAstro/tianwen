using System;
using System.Collections.Generic;
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
using TianWen.AI.Imaging.Onnx;
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
    /// A verb that dropped either would run another program and write another file. A colour input, since SCNR does
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
            WorkbenchFixtures.Picture("fringed", master, WorkbenchFixtures.Edit(EnhanceOperation.Id, new JsonObject { ["scnr"] = "none" }))), ct);
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

        // And the three are three programs: a stated blend keeps the starless deconvolution a deblurrer would drop, and a
        // stated mode takes out the SCNR the program runs with one. Equal files would make the comparison above empty.
        var program = WorkbenchFixtures.Read(Result("program"));
        Differ(program, WorkbenchFixtures.Read(Result("deconvolved"))).ShouldBeTrue("the starless deconvolution ran where its blend was stated");
        Differ(program, WorkbenchFixtures.Read(Result("fringed"))).ShouldBeTrue("the SCNR did not run where its mode was stated as none");

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

        // What serves is in the key: the pipeline names the enhancer of every role, and "none" where there is none.
        enhance.ServedBy(new EnhanceSettings()).ShouldBe("deblur none; gradient none; stars none; stellar none; deconvolve none; denoise none");
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
}
