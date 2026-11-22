using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
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
/// A step of a project and its command-line verb are one routine (docs/plans/stacking-workbench.md, rules 1 and 3;
/// #1641): every step's printed command line is run through the real <c>tianwen image</c> command, and the file it
/// writes is the step's result to the byte. So what a project made can be made again from a terminal, and a bug
/// report can carry the lines.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchVerbParityTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    /// <summary>An enhancer that scales what it is handed: the same answer for the same input, which is all a step and
    /// its verb need to be held to one another.</summary>
    private sealed class ScalingEnhancer(string name, float factor) : IStarRemover, IImageDeblurrer, IDenoiseEnhancer
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

    private static readonly ScalingEnhancer Remover = new("Test/Remover", 0.5f);
    private static readonly ScalingEnhancer Deblurrer = new("Test/Deblurrer", 0.9f);
    private static readonly ScalingEnhancer Denoiser = new("Test/Denoiser", 0.8f);

    private static OperationCatalog Catalog() => new(
    [
        new MatchPsfOperation(),
        new DeblurOperation(Deblurrer),
        new DenoiseOperation(Denoiser),
        new RemoveStarsOperation(Remover),
        new CombineOperation(Remover, Deblurrer, Denoiser),
        new CropOperation(),
        new LinearFitOperation(),
        new GradientOperation(new ClassicalBackgroundExtractor(), Substitute.For<IGradientCorrector>()),
        new AlignOperation(),
        new ContinuumOperation(),
        new AddLineOperation(),
        new LuminanceOperation(),
        new LrgbOperation(),
        new AddStarsOperation(),
        new FillDigsOperation(),
        new ProtectCoresOperation(),
    ]);

    // The real image command over the classical extractor, with what it said.
    private static (Command Command, List<string> Said) ImageCommand()
    {
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var command = new ImageSubCommand(
            host,
            new SharpenPipeline(),
            Remover,
            Substitute.For<IGradientCorrector>(),
            new ClassicalBackgroundExtractor(),
            new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance),
            Deblurrer,
            Denoiser).Build();
        return (command, said);
    }

    /// <summary>
    /// Runs every step's command line, in order, through the real verbs: each writes a file of its own, and a later
    /// line that names an earlier step's result is handed the file that step's verb wrote. So the chain is verbs only.
    /// </summary>
    /// <returns>For each step's own result path as its command line names it, the file its verb wrote.</returns>
    private async Task<Dictionary<string, string>> ReplayThroughTheVerbsAsync(WorkbenchReport report, string folder, CancellationToken ct)
    {
        var (image, said) = ImageCommand();
        var store = Path.Combine(folder, StepStore.FolderName) + Path.DirectorySeparatorChar;
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var step in report.Images.SelectMany(static each => each.Steps))
        {
            var line = step.CommandLine.ToArray();
            line[..2].ShouldBe([StepCommandLine.Program, "image"]);
            line.ShouldContain("-o", "every step's command line names its output with -o");
            for (var i = 2; i < line.Length; i++)
            {
                if (written.TryGetValue(line[i], out var earlier))
                {
                    // A result an earlier line wrote (or this same line, where several edit lists share one run).
                    line[i] = earlier;
                }
                else if (line[i].StartsWith(store, StringComparison.Ordinal))
                {
                    // A file of the store no earlier line wrote is one this line writes: the verb gets a file of its own.
                    var own = Path.Combine(folder, $"verb-{count++}.fits");
                    written[line[i]] = own;
                    line[i] = own;
                }
            }

            output.WriteLine(StepCommandLine.Display(line));
            said.Clear();
            var exit = await image.Parse(line[2..]).InvokeAsync(cancellationToken: ct);
            exit.ShouldBe(0, string.Join(Environment.NewLine, said));
        }

        return written;
    }

    // The path a step's own command line writes to.
    private static string NamedOutput(StepReport step) => step.CommandLine[step.CommandLine.IndexOf("-o") + 1];

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

    [Fact(Timeout = 300_000)]
    public async Task EveryStepsResultIsTheFileItsVerbWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("parity-").FullName;
        var green = WorkbenchFixtures.WriteMaster(folder, "green", WorkbenchFixtures.Master(seed: 1));
        var red = WorkbenchFixtures.WriteMaster(folder, "red", WorkbenchFixtures.Master(seed: 2, gain: 0.5f));
        JsonObject Rectangle() => new() { ["x"] = 16, ["y"] = 8, ["width"] = 200, ["height"] = 160 };
        // An image another takes is listed before it, so the lines replay in an order a terminal could run them in.
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            // A linear fit of two crops: both of its inputs are derived, handed over in memory.
            WorkbenchFixtures.Picture("green", green, WorkbenchFixtures.Edit(CropOperation.Id, Rectangle())),
            WorkbenchFixtures.Picture("red", red, WorkbenchFixtures.Edit(CropOperation.Id, Rectangle()), WorkbenchFixtures.Edit(LinearFitOperation.Id, null, "green")),
            // The same fit of two files, and one whose reference is a stored step's file.
            WorkbenchFixtures.Picture("green-file", green),
            WorkbenchFixtures.Picture("red-file", red, WorkbenchFixtures.Edit(LinearFitOperation.Id, new JsonObject { ["rejectHigh"] = 0.8 }, "green-file")),
            // A gradient fit of a file, and of a crop.
            WorkbenchFixtures.Picture("plain", green, WorkbenchFixtures.Edit(GradientOperation.Id, new JsonObject { ["backend"] = "classical" })),
            WorkbenchFixtures.Picture("cropped-flat", green,
                WorkbenchFixtures.Edit(CropOperation.Id, Rectangle()),
                WorkbenchFixtures.Edit(GradientOperation.Id, new JsonObject { ["backend"] = "classical", ["degree"] = 1, ["downsample"] = 2 })),
            WorkbenchFixtures.Picture("red-on-flat", red,
                WorkbenchFixtures.Edit(GradientOperation.Id, new JsonObject { ["backend"] = "classical" }),
                WorkbenchFixtures.Edit(LinearFitOperation.Id, null, "plain"))), ct);
        var runner = new WorkbenchRunner(Catalog());

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        var verbs = await ReplayThroughTheVerbsAsync(report, folder, ct);
        var stored = 0;
        foreach (var image in report.Images)
        {
            foreach (var step in image.Steps)
            {
                if (step.Output is { } result)
                {
                    ShouldBeTheSameFile(result, verbs[NamedOutput(step)], $"{image.Name}, step {step.Index + 1} ({step.Operation})");
                    stored++;
                }
            }
        }

        stored.ShouldBe(6, "three linear fits and three gradient fits were stored and compared");

        // A crop has no file in the store. Written when asked for, it is the file image crop writes.
        var exported = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "green", ExportPath = Path.Combine(folder, "green-exported.fits") }, ct);
        var crop = exported.Images.Single(static image => image.Name == "green").Steps[0];
        crop.State.ShouldBe(StepState.Derived);
        ShouldBeTheSameFile(exported.Exported.ShouldNotBeNull(exported.Refusal), verbs[NamedOutput(crop)], "the exported crop");

        // A stored result exported is that result.
        var fit = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "red", ExportPath = Path.Combine(folder, "red-exported.fits") }, ct);
        ShouldBeTheSameFile(fit.Exported.ShouldNotBeNull(fit.Refusal), fit.Images.Single(static image => image.Name == "red").Steps[1].Output.ShouldNotBeNull(), "the exported fit");
    }

    private const int FieldSize = 512;

    // One sky seen through another filter or on another night: the same stars, noise of its own.
    private static Image Field(int noiseSeed)
        => new([SyntheticStarFieldRenderer.Render(FieldSize, FieldSize, defocusSteps: 0, exposureSeconds: 10, skyBackground: 100, readNoise: 5,
            starCount: 250, seed: 5, noiseSeed: noiseSeed)], BitDepth.Float32, 65535f, 0f, 0f, new ImageMeta());

    private static string WriteField(string folder, string name, Image field)
    {
        var path = Path.Combine(folder, name + ".fits");
        field.WriteToFitsFile(path);
        return path;
    }

    /// <summary>
    /// The colour steps that take fixed images and give one result (#2465), each as an edit, each held to its verb's
    /// file. What the fields are does not matter here, only that a step and its verb make the same file from them.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task TheColourStepsAreTheFilesTheirVerbsWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("parity-colour-").FullName;
        var red = WriteField(folder, "red", Field(noiseSeed: 1));
        var green = WriteField(folder, "green", Field(noiseSeed: 2));
        var blue = WriteField(folder, "blue", Field(noiseSeed: 3));
        var line = WriteField(folder, "ha", Field(noiseSeed: 4));
        // Another night of the same sky, turned and shifted.
        var turn = Matrix3x2.CreateRotation(3f * MathF.PI / 180f, new Vector2(FieldSize / 2f, FieldSize / 2f)) * Matrix3x2.CreateTranslation(14.5f, -9.25f);
        var moved = WriteField(folder, "moved", await Field(noiseSeed: 5).WarpToReferenceGridAsync(turn, FieldSize, FieldSize, ct));
        // An image another takes is listed before it.
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("red", red),
            WorkbenchFixtures.Picture("green", green),
            WorkbenchFixtures.Picture("blue", blue),
            WorkbenchFixtures.Picture("ha", line),
            WorkbenchFixtures.Picture("aligned", moved, WorkbenchFixtures.Edit(AlignOperation.Id, null, "red")),
            WorkbenchFixtures.Picture("ha-pure", line, WorkbenchFixtures.Edit(ContinuumOperation.Id, new JsonObject { ["scale"] = 0.25, ["method"] = "photometric" }, "red")),
            WorkbenchFixtures.Picture("red-with-line", red, WorkbenchFixtures.Edit(AddLineOperation.Id, new JsonObject { ["scale"] = 0.25, ["weight"] = 0.5 }, "ha")),
            WorkbenchFixtures.Picture("luminance", green, WorkbenchFixtures.Edit(LuminanceOperation.Id, null, "red", "blue")),
            WorkbenchFixtures.Picture("red-lrgb", red, WorkbenchFixtures.Edit(LrgbOperation.Id, new JsonObject { ["colourSigma"] = 2 }, "luminance")),
            WorkbenchFixtures.Picture("red-with-stars", red, WorkbenchFixtures.Edit(AddStarsOperation.Id, null, "blue")),
            WorkbenchFixtures.Picture("red-filled", red, WorkbenchFixtures.Edit(FillDigsOperation.Id, new JsonObject { ["keepOvershoot"] = true }, "green")),
            WorkbenchFixtures.Picture("red-cores", red, WorkbenchFixtures.Edit(ProtectCoresOperation.Id, new JsonObject { ["catalogue"] = "none" }, "green"))), ct);
        var runner = new WorkbenchRunner(Catalog());

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        var verbs = await ReplayThroughTheVerbsAsync(report, folder, ct);
        var compared = new List<string>();
        foreach (var image in report.Images)
        {
            foreach (var step in image.Steps)
            {
                ShouldBeTheSameFile(step.Output.ShouldNotBeNull(), verbs[NamedOutput(step)], $"{image.Name} ({step.Operation})");
                compared.Add(step.Operation);
            }
        }

        compared.ShouldBe(
        [
            AlignOperation.Id, ContinuumOperation.Id, AddLineOperation.Id, LuminanceOperation.Id, LrgbOperation.Id, AddStarsOperation.Id,
            FillDigsOperation.Id, ProtectCoresOperation.Id,
        ]);
    }

    /// <summary>
    /// The steps several masters go through in one run (#2465), each held to its verb's files: every master's result,
    /// and the stars a star removal took out. Stand-in enhancers serve the three roles, the same ones in the catalog
    /// and in the image command.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task TheStepsSeveralMastersGoThroughTogetherAreTheFilesTheirVerbsWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("parity-together-").FullName;
        var red = WriteField(folder, "red", Field(noiseSeed: 1));
        var green = WriteField(folder, "green", Field(noiseSeed: 2));
        var blue = WriteField(folder, "blue", Field(noiseSeed: 3));
        JsonObject NoCatalogue() => new() { ["catalogue"] = "none" };
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("red", red),
            WorkbenchFixtures.Picture("green", green),
            WorkbenchFixtures.Picture("blue", blue),
            // Two edit lists of one run of three: the second finds its result in the store.
            WorkbenchFixtures.Starting("red-psf", "red", WorkbenchFixtures.Edit(MatchPsfOperation.Id, null, "green", "blue")),
            WorkbenchFixtures.Starting("green-psf", "green", WorkbenchFixtures.Edit(MatchPsfOperation.Id, null, "blue", "red")),
            WorkbenchFixtures.Starting("red-deblur", "red", WorkbenchFixtures.Edit(DeblurOperation.Id, new JsonObject { ["headroom"] = 2 }, "green")),
            WorkbenchFixtures.Starting("red-denoise", "red", WorkbenchFixtures.Edit(DenoiseOperation.Id, new JsonObject { ["strength"] = 0.5 }, "blue")),
            // A run of one, its stars as an image of their own, and the two put back together.
            WorkbenchFixtures.Starting("red-starless", "red", WorkbenchFixtures.Edit(RemoveStarsOperation.Id, NoCatalogue())),
            WorkbenchFixtures.StartingFromResult("red-stars", "red-starless", RemoveStarsOperation.Stars),
            WorkbenchFixtures.Starting("red-back", "red-starless", WorkbenchFixtures.Edit(AddStarsOperation.Id, null, "red-stars")),
            // And a star removal of two on one scale, with nothing protected and the remover's overshoot kept.
            WorkbenchFixtures.Starting("green-starless", "green",
                WorkbenchFixtures.Edit(RemoveStarsOperation.Id, new JsonObject { ["protectCores"] = false, ["fillDigs"] = false, ["keepOvershoot"] = true }, "blue"))), ct);
        var runner = new WorkbenchRunner(Catalog());

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        ImageReport Of(string name) => report.Images.Single(image => image.Name == name);
        Of("red-psf").Steps[0].State.ShouldBe(StepState.Ran);
        Of("green-psf").Steps[0].State.ShouldBe(StepState.Kept, "the run of three happened once");
        var verbs = await ReplayThroughTheVerbsAsync(report, folder, ct);
        var compared = new List<string>();
        foreach (var image in report.Images)
        {
            foreach (var step in image.Steps)
            {
                var result = step.Output.ShouldNotBeNull();
                ShouldBeTheSameFile(result, verbs[result], $"{image.Name} ({step.Operation})");
                compared.Add(step.Operation);
            }
        }

        compared.ShouldBe(
        [
            MatchPsfOperation.Id, MatchPsfOperation.Id, DeblurOperation.Id, DenoiseOperation.Id, RemoveStarsOperation.Id, AddStarsOperation.Id,
            RemoveStarsOperation.Id,
        ]);

        // The stars are a result of their own in the store, and the file the verb wrote for them.
        var stars = Path.Combine(folder, StepStore.FolderName, StepStore.FileNameOf(Of("red-stars").Key));
        ShouldBeTheSameFile(stars, verbs[stars], "the stars a star removal took out");
        // Plate plus stars is the input: the remover's half and the other half.
        var back = WorkbenchFixtures.Read(Of("red-back").Steps[0].Output.ShouldNotBeNull());
        var original = WorkbenchFixtures.Read(red);
        back[0, 100, 100].ShouldBe(original[0, 100, 100], original[0, 100, 100] * 1e-4f);
    }

    /// <summary>
    /// The recipe as one step (#2465): red, green and blue made one colour image, held to the file image combine writes,
    /// once as it is by default and once with the steps a stand-in enhancer can serve switched on.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task TheColourRecipeAsOneStepIsTheFileImageCombineWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("parity-combine-").FullName;
        var red = WriteField(folder, "red", Field(noiseSeed: 1));
        var green = WriteField(folder, "green", Field(noiseSeed: 2));
        var blue = WriteField(folder, "blue", Field(noiseSeed: 3));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("green", green),
            WorkbenchFixtures.Picture("blue", blue),
            WorkbenchFixtures.Picture("plain", red, WorkbenchFixtures.Edit(CombineOperation.Id, null, "green", "blue")),
            WorkbenchFixtures.Picture("worked", red, WorkbenchFixtures.Edit(CombineOperation.Id,
                new JsonObject { ["linearFit"] = "red", ["matchPsf"] = true, ["starless"] = true, ["catalogue"] = "none", ["lrgb"] = true, ["colourSigma"] = 2, ["denoise"] = true, ["denoiseStrength"] = 0.4 },
                "green", "blue"))), ct);
        var runner = new WorkbenchRunner(Catalog());

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        var verbs = await ReplayThroughTheVerbsAsync(report, folder, ct);
        foreach (var image in report.Images.Where(static image => image.Steps.Length > 0))
        {
            var result = image.Steps[0].Output.ShouldNotBeNull();
            ShouldBeTheSameFile(result, verbs[result], image.Name);
            WorkbenchFixtures.Read(result).ChannelCount.ShouldBe(3, "one colour image");
        }

        report.Images[3].Steps[0].CommandLine.ShouldContain("--starless");
        report.Images[3].Steps[0].Key.ShouldNotBe(report.Images[2].Steps[0].Key);
    }

    [Fact(Timeout = 300_000)]
    public async Task AnAutoCropIsTheRuleResolvedAgainstItsInputAsImageAutocropResolvesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("parity-auto-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "ringed", WorkbenchFixtures.Master(seed: 3, ring: 12));
        JsonObject Auto() => new() { ["auto"] = true, ["trimDeclined"] = 0.1 };
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("auto", master, WorkbenchFixtures.Edit(CropOperation.Id, Auto())),
            WorkbenchFixtures.Picture("auto-flat", master,
                WorkbenchFixtures.Edit(CropOperation.Id, Auto()),
                WorkbenchFixtures.Edit(GradientOperation.Id, new JsonObject { ["backend"] = "classical" }))), ct);
        var runner = new WorkbenchRunner(Catalog());

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "auto", ExportPath = Path.Combine(folder, "auto-exported.fits"), Say = output.WriteLine }, ct);
        var all = await runner.RunAsync(folder, cancellationToken: ct);

        all.Succeeded.ShouldBeTrue(string.Join("; ", all.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        var verbs = await ReplayThroughTheVerbsAsync(all, folder, ct);
        var crop = all.Images[0].Steps[0];
        crop.CommandLine.ShouldContain("autocrop");
        ShouldBeTheSameFile(report.Exported.ShouldNotBeNull(report.Refusal), verbs[NamedOutput(crop)], "the exported auto-crop");
        var cropped = WorkbenchFixtures.Read(verbs[NamedOutput(crop)]);
        cropped.Width.ShouldBeLessThanOrEqualTo(WorkbenchFixtures.W - 24, "the ring is gone");
        cropped.Height.ShouldBeLessThanOrEqualTo(WorkbenchFixtures.H - 24);

        var flat = all.Images[1].Steps[1];
        ShouldBeTheSameFile(flat.Output.ShouldNotBeNull(), verbs[NamedOutput(flat)], "the gradient fit of an auto-crop");
    }

    // Every option of a line, not only the ones a default run prints: a wrong --surface, --divide, --exclude or --margin
    // makes a different file, and this holds each to its verb's, to the byte. (backend graxpert is not here: it needs
    // the model's weights, which a unit test has not; its line is `--backend graxpert` alone.)
    [Fact(Timeout = 300_000)]
    public async Task EveryOptionOfAGradientAndAnAutoCropLineIsHeldToItsVerb()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("parity-options-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "ringed", WorkbenchFixtures.Master(seed: 5, ring: 12));
        JsonObject Classical(Action<JsonObject> tune)
        {
            var settings = new JsonObject { ["backend"] = "classical", ["degree"] = 1, ["downsample"] = 2 };
            tune(settings);
            return settings;
        }

        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("surface", master, WorkbenchFixtures.Edit(GradientOperation.Id, Classical(s => s["surface"] = true))),
            WorkbenchFixtures.Picture("divide", master, WorkbenchFixtures.Edit(GradientOperation.Id, Classical(s => s["divide"] = true))),
            WorkbenchFixtures.Picture("exclude", master, WorkbenchFixtures.Edit(GradientOperation.Id,
                Classical(s => s["exclusions"] = new JsonArray(new JsonArray(40, 40, 120, 40, 120, 100, 40, 100))))),
            WorkbenchFixtures.Picture("margin", master, WorkbenchFixtures.Edit(CropOperation.Id,
                new JsonObject { ["auto"] = true, ["trimDeclined"] = 0.1, ["margin"] = 0.03 })),
            WorkbenchFixtures.Picture("margin-flat", master,
                WorkbenchFixtures.Edit(CropOperation.Id, new JsonObject { ["auto"] = true, ["margin"] = 0.02 }),
                WorkbenchFixtures.Edit(GradientOperation.Id, Classical(s => s["surface"] = true)))), ct);
        var runner = new WorkbenchRunner(Catalog());

        var all = await runner.RunAsync(folder, cancellationToken: ct);

        all.Succeeded.ShouldBeTrue(string.Join("; ", all.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        var lines = all.Images.SelectMany(static image => image.Steps).Select(static step => string.Join(' ', step.CommandLine)).ToList();
        lines.ShouldContain(line => line.Contains("--surface"));
        lines.ShouldContain(line => line.Contains("--divide"));
        lines.ShouldContain(line => line.Contains("--exclude"));
        lines.ShouldContain(line => line.Contains("--margin 0.03"));
        var verbs = await ReplayThroughTheVerbsAsync(all, folder, ct);
        foreach (var image in all.Images)
        {
            foreach (var step in image.Steps)
            {
                // A stored step is held to its file; a crop is derived, so the exported file is what the verb is held to.
                if (step.Output is { } result)
                {
                    ShouldBeTheSameFile(result, verbs[NamedOutput(step)], $"{image.Name}, step {step.Index + 1} ({step.Operation})");
                }
            }
        }

        var exported = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "margin", ExportPath = Path.Combine(folder, "margin-exported.fits") }, ct);
        var crop = exported.Images.Single(static image => image.Name == "margin").Steps[0];
        ShouldBeTheSameFile(exported.Exported.ShouldNotBeNull(exported.Refusal), verbs[NamedOutput(crop)], "the exported auto-crop with a margin");
    }

    [Fact(Timeout = 300_000)]
    public async Task TheWorkbenchVerbSaysWhatWillRunRunsItAndThenKeepsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("workbench-verb-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", master,
                WorkbenchFixtures.Edit(CropOperation.Id, new JsonObject { ["x"] = 16, ["y"] = 8, ["width"] = 200, ["height"] = 160 }),
                WorkbenchFixtures.Edit(GradientOperation.Id, new JsonObject { ["backend"] = "classical" }))), ct);
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var catalog = Catalog();
        var workbench = new WorkbenchSubCommand(host, catalog, new WorkbenchRunner(catalog)).Build();

        async Task<(int Exit, string Said)> InvokeAsync(params string[] args)
        {
            said.Clear();
            var exit = await workbench.Parse(args).InvokeAsync(cancellationToken: ct);
            var text = string.Join(Environment.NewLine, said);
            output.WriteLine($"> workbench {string.Join(' ', args)} (exit {exit})");
            output.WriteLine(text);
            return (exit, text);
        }

        var status = await InvokeAsync("status", folder);
        status.Exit.ShouldBe(0);
        status.Said.ShouldContain("1 crop: derived");
        status.Said.ShouldContain("2 gradient: will run");
        status.Said.ShouldContain("tianwen image crop");
        status.Said.ShouldContain("tianwen image flatten");
        status.Said.ShouldContain("--backend classical --degree 2 --downsample 4");

        var first = await InvokeAsync("run", folder);
        first.Exit.ShouldBe(0);
        first.Said.ShouldContain("2 gradient: ran");
        // A step's own lines are said with the step named.
        first.Said.ShouldContain("[m 2 gradient]");

        var second = await InvokeAsync("run", folder);
        second.Exit.ShouldBe(0);
        second.Said.ShouldContain("2 gradient: kept");
        second.Said.ShouldNotContain(": ran");

        var exportedTo = Path.Combine(folder, "out", "m.fits");
        var export = await InvokeAsync("run", folder, "--image", "m", "--export", exportedTo);
        export.Exit.ShouldBe(0);
        export.Said.ShouldContain("exported");
        File.Exists(exportedTo).ShouldBeTrue();

        (await InvokeAsync("run", folder, "--export", exportedTo)).Exit.ShouldBe(1, "an export names its image");
        var missing = await InvokeAsync("status", Path.Combine(folder, "no-such-project"));
        missing.Exit.ShouldBe(1);
        missing.Said.ShouldContain("there is no project.json");

        var operations = await InvokeAsync("operations");
        operations.Exit.ShouldBe(0);
        operations.Said.ShouldContain("crop (version 1)");
        operations.Said.ShouldContain("gradient (version 1)");
        operations.Said.ShouldContain("linear-fit (version 1)");
        operations.Said.ShouldContain("\"rejectHigh\":0.92");

        // A step that gives no result is exit 2, said in words: a rectangle outside the image.
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", master,
                WorkbenchFixtures.Edit(CropOperation.Id, new JsonObject { ["x"] = 200, ["y"] = 8, ["width"] = 200, ["height"] = 160 }),
                WorkbenchFixtures.Edit(GradientOperation.Id, new JsonObject { ["backend"] = "classical" }))), ct);
        var refused = await InvokeAsync("run", folder);
        refused.Exit.ShouldBe(2);
        refused.Said.ShouldContain("2 gradient: refused");
        refused.Said.ShouldContain("past the image width 256");
    }

    [Fact(Timeout = 300_000)]
    public async Task TheWorkbenchRunVerbPrintsTheNoteWhenTheProjectFileCannotBeRecordedInto()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("workbench-verb-note-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", master,
                WorkbenchFixtures.Edit(GradientOperation.Id, new JsonObject { ["backend"] = "classical" }))), ct);
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call =>
        {
            var line = call.Arg<string>();
            said.Add(line);
            // The step speaks while the run works, before its results are recorded: the person removes project.json then.
            if (line.Contains("[m 1 gradient]") && File.Exists(WorkbenchProjectFile.PathIn(folder)))
            {
                File.Delete(WorkbenchProjectFile.PathIn(folder));
            }
        });
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var catalog = Catalog();
        var workbench = new WorkbenchSubCommand(host, catalog, new WorkbenchRunner(catalog)).Build();

        var exit = await workbench.Parse(["run", folder]).InvokeAsync(cancellationToken: ct);

        var text = string.Join(Environment.NewLine, said);
        output.WriteLine(text);
        exit.ShouldBe(0, "a run that only could not record is still a success");
        text.ShouldContain("[workbench] note: ");
        text.ShouldContain("removed or saved half way");
        File.Exists(WorkbenchProjectFile.PathIn(folder)).ShouldBeFalse();
    }
}
