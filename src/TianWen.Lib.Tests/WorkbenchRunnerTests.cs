using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>Masters and projects for the workbench tests: small, synthetic, written through the real FITS writer.</summary>
internal static class WorkbenchFixtures
{
    public const int W = 256;
    public const int H = 192;

    public static ImageMeta Meta() => new(
        Instrument: "synthetic", ExposureStartTime: DateTimeOffset.UnixEpoch,
        ExposureDuration: TimeSpan.FromSeconds(60), FrameType: FrameType.Light,
        Telescope: "TestScope", PixelSizeX: 3.76f, PixelSizeY: 3.76f, FocalLength: 400, FocusPos: FocuserPositions.None,
        Filter: Filter.Luminance, BinX: 1, BinY: 1, CCDTemperature: -10f,
        SensorType: SensorType.Monochrome, BayerOffsetX: 0, BayerOffsetY: 0,
        RowOrder: RowOrder.TopDown, Latitude: float.NaN, Longitude: float.NaN);

    public static WCS Wcs() => new WCS(10.0 / 15.0, 20.0)
    {
        CRPix1 = (W / 2.0) - 0.5,
        CRPix2 = (H / 2.0) - 0.5,
        CD1_1 = -0.001,
        CD1_2 = 0,
        CD2_1 = 0,
        CD2_2 = 0.001,
    };

    /// <summary>
    /// A linear sky with a ramp across it, noise, and a dozen round sources at fixed places: enough for a background
    /// fit to have a gradient to take out and for a linear fit to have something two masters share.
    /// </summary>
    /// <param name="seed">The noise; the sources are the same for every seed.</param>
    /// <param name="gain">What the sources and the sky are multiplied by, so two masters relate by a line.</param>
    /// <param name="ring">A border of exact zeros this wide, as a stacked master's uncovered ring.</param>
    public static Image Master(int seed, float gain = 1f, int ring = 0)
    {
        var noise = new Random(seed);
        var sources = new Random(4711);
        var plane = new float[H, W];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                plane[y, x] = gain * (0.010f + (0.004f * x / (W - 1)) + (0.002f * y / (H - 1))) + ((float)(noise.NextDouble() - 0.5) * 4e-4f);
            }
        }

        for (var s = 0; s < 12; s++)
        {
            var (cx, cy) = (30 + sources.Next(W - 60), 30 + sources.Next(H - 60));
            var amplitude = gain * (0.05f + (0.02f * s));
            for (var y = cy - 9; y <= cy + 9; y++)
            {
                for (var x = cx - 9; x <= cx + 9; x++)
                {
                    var r2 = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                    plane[y, x] += amplitude * MathF.Exp(-r2 / 18f);
                }
            }
        }

        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                if (x < ring || y < ring || x >= W - ring || y >= H - ring)
                {
                    plane[y, x] = 0f;
                }
            }
        }

        return new Image([plane], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: Meta());
    }

    public static string WriteMaster(string folder, string name, Image master)
    {
        var path = Path.Combine(folder, name + ".fits");
        master.WriteToFitsFile(path, Wcs());
        return path;
    }

    public static Image Read(string path)
    {
        Image.TryReadFitsFile(path, out var image).ShouldBeTrue($"{path} reads as a FITS image");
        return image;
    }

    public static ProjectEdit Edit(string operation, JsonObject? settings = null, params string[] inputs)
        => new() { Operation = operation, Settings = settings, Inputs = [.. inputs] };

    public static ProjectImage Picture(string name, string path, params ProjectEdit[] edits)
        => new() { Name = name, Path = path, Edits = [.. edits] };

    /// <summary>An image that starts from another image of the project, as that one's edits leave it.</summary>
    public static ProjectImage Starting(string name, string origin, params ProjectEdit[] edits)
        => new() { Name = name, From = origin, Edits = [.. edits] };

    /// <summary>An image that starts from an extra result of another image's step.</summary>
    public static ProjectImage StartingFromResult(string name, string origin, string result, params ProjectEdit[] edits)
        => new() { Name = name, From = origin, Result = result, Edits = [.. edits] };

    public static WorkbenchProject Project(params ProjectImage[] images) => new() { Images = [.. images] };
}

/// <summary>What <see cref="AddOperation"/> adds.</summary>
internal sealed record AddSettings
{
    public float Amount { get; set; } = 1f;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AddSettings))]
internal sealed partial class WorkbenchTestJsonContext : JsonSerializerContext
{
}

/// <summary>A stored operation that adds a number to every pixel, plus a second image's pixels when it takes one, and
/// counts how often it ran. What it writes is beside the point; that it ran is what the tests read.</summary>
internal sealed class AddOperation(string id, bool takesAnother = false) : Operation<AddSettings>
{
    public int Runs { get; private set; }

    /// <summary>Called at the start of each run, after it is counted: what happens elsewhere while a step works.</summary>
    public Action? DuringRun { get; set; }

    public override OperationDescription Description { get; } = new(
        id, Version: 1, Title: "Add", Verb: "none", OperationOutputKind.Stored,
        ExtraInputs: takesAnother ? [new OperationInputRole("other", "The image added to this one.")] : [],
        Settings: [new OperationSetting("amount", "What is added to every pixel.")]);

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override string? Refusal(AddSettings settings) => float.IsFinite(settings.Amount) ? null : "amount is a number";

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output)
        => [Description.Id, .. inputs, "-o", output, "--amount", StepCommandLine.Number(settings.Amount)];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        Runs++;
        DuringRun?.Invoke();
        if (!inputs[0].TryRead(out var image, out var wcs, out var refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        Image? other = null;
        if (inputs.Count > 1 && !inputs[1].TryRead(out other, out _, out refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        var plane = new float[image.Height, image.Width];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                plane[y, x] = image[0, y, x] + settings.Amount + (other is null ? 0f : other[0, y, x]);
            }
        }

        say?.Invoke($"added {settings.Amount}");
        OperationOutput.Write(new Image([plane], BitDepth.Float32, maxValue: 100f, minValue: 0f, pedestal: 0f, imageMeta: image.ImageMeta), outputPath, wcs);
        return ValueTask.FromResult(OperationOutcome.Done);
    }
}

/// <summary>
/// A stored operation several images go through in one run. Every image gets its own pixels plus the amount plus the
/// sum of every image's first pixel (so a result depends on which images ran together, and not on their order), and as
/// an extra result the image as it was handed in. It counts its runs.
/// </summary>
internal sealed class TogetherOperation : JointOperation<AddSettings>
{
    public const string Twin = "twin";

    public int Runs { get; private set; }

    public override OperationDescription Description { get; } = new(
        "together", Version: 1, Title: "Together", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [], Settings: [])
    {
        Joint = true,
        ExtraResults = [new OperationResultRole(Twin, "The image as it was handed in.")],
    };

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> members, IReadOnlyList<StepOutputs> outputs)
        => [Description.Id, .. members, .. outputs.Select(static output => output.Path)];

    public override ValueTask<OperationOutcome> RunJointAsync(AddSettings settings, IReadOnlyList<OperationInput> members, IReadOnlyList<StepOutputs> outputs,
        Action<string>? say, CancellationToken cancellationToken)
    {
        Runs++;
        var read = new List<(Image Image, WCS? Wcs)>(members.Count);
        foreach (var member in members)
        {
            if (!member.TryRead(out var handed, out var handedWcs, out var refusal))
            {
                return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
            }

            read.Add((handed, handedWcs));
        }

        var shared = read.Sum(static each => each.Image[0, 0, 0]);
        for (var i = 0; i < read.Count; i++)
        {
            var (image, wcs) = read[i];
            var plane = new float[image.Height, image.Width];
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    plane[y, x] = image[0, y, x] + settings.Amount + shared;
                }
            }

            OperationOutput.Write(new Image([plane], BitDepth.Float32, maxValue: 1000f, minValue: 0f, pedestal: 0f, imageMeta: image.ImageMeta), outputs[i].Path, wcs);
            if (outputs[i].Extra.TryGetValue(Twin, out var twin))
            {
                OperationOutput.Write(image, twin, wcs);
            }
        }

        return ValueTask.FromResult(OperationOutcome.Done);
    }
}

/// <summary>A stored operation that writes half a result and then fails, and counts how often it ran.</summary>
internal sealed class BreakOperation : Operation<AddSettings>
{
    public const string WhyItFails = "the fit did not converge";

    public int Runs { get; private set; }

    public override OperationDescription Description { get; } = new(
        "break", Version: 1, Title: "Break", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [], Settings: []);

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output)
        => [Description.Id, inputs[0], "-o", output];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        Runs++;
        File.WriteAllText(outputPath, "half a result");
        return ValueTask.FromResult(OperationOutcome.Failed(WhyItFails));
    }
}

/// <summary>
/// The runner's contract (docs/plans/stacking-workbench.md, W1; #1641), with no window: a project run twice runs
/// nothing the second time, a changed setting runs its step and the steps after it and none before, a derived step
/// stores nothing, and nothing is decided by a file's date.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchRunnerTests : IDisposable
{
    private const float Tolerance = 1e-6f;

    private readonly TempFolders _folders = new();
    private readonly AddOperation _add = new("add");
    private readonly AddOperation _blend = new("blend", takesAnother: true);
    private readonly WorkbenchRunner _runner;

    public WorkbenchRunnerTests() => _runner = new WorkbenchRunner(new OperationCatalog([_add, _blend, new CropOperation()]));

    public void Dispose() => _folders.Dispose();

    private static JsonObject Amount(float amount) => new() { ["amount"] = amount };

    private async Task<(string Folder, string Master)> ProjectAsync(string name, Func<string, WorkbenchProject> project)
    {
        var folder = _folders.Create(name + "-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, project(master), TestContext.Current.CancellationToken);
        return (folder, master);
    }

    private static async Task EditAsync(string folder, Func<WorkbenchProject, WorkbenchProject> change)
    {
        var read = await WorkbenchProjectFile.ReadAsync(folder, TestContext.Current.CancellationToken);
        await WorkbenchProjectFile.WriteAsync(folder, change(read.Project.ShouldNotBeNull(read.Refusal)), TestContext.Current.CancellationToken);
    }

    // The project with one edit of its first image replaced.
    private static WorkbenchProject WithEdit(WorkbenchProject project, int index, Func<ProjectEdit, ProjectEdit> change)
    {
        var image = project.Images[0];
        return project with { Images = project.Images.SetItem(0, image with { Edits = image.Edits.SetItem(index, change(image.Edits[index])) }) };
    }

    private static StepState[] States(WorkbenchReport report, int image = 0) => [.. report.Images[image].Steps.Select(static step => step.State)];

    [Fact(Timeout = 120_000)]
    public async Task ARunMakesEveryStepAndASecondRunRunsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, master) = await ProjectAsync("twice", m => WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", m, WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)))));

        var first = await _runner.RunAsync(folder, cancellationToken: ct);

        first.Succeeded.ShouldBeTrue();
        States(first).ShouldBe([StepState.Ran, StepState.Ran]);
        _add.Runs.ShouldBe(2);
        var result = WorkbenchFixtures.Read(first.Images[0].Steps[1].Output.ShouldNotBeNull());
        result[0, 40, 50].ShouldBe(WorkbenchFixtures.Read(master)[0, 40, 50] + 3f, Tolerance, "the second step read the first step's file");

        var second = await _runner.RunAsync(folder, cancellationToken: ct);

        second.Succeeded.ShouldBeTrue();
        States(second).ShouldBe([StepState.Kept, StepState.Kept]);
        _add.Runs.ShouldBe(2, "a step whose key is in the store is not run again");
        second.Images[0].Steps.Select(static step => step.Key).ShouldBe(first.Images[0].Steps.Select(static step => step.Key));
    }

    [Fact(Timeout = 120_000)]
    public async Task AChangedSettingRunsItsStepAndTheStepsAfterItAndNoneBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("changed", m => WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", m,
            WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)), WorkbenchFixtures.Edit("add", Amount(4)))));
        var before = await _runner.RunAsync(folder, cancellationToken: ct);
        _add.Runs.ShouldBe(3);

        await EditAsync(folder, project => WithEdit(project, 1, edit => edit with { Settings = Amount(3) }));
        var status = await _runner.StatusAsync(folder, cancellationToken: ct);
        var after = await _runner.RunAsync(folder, cancellationToken: ct);

        States(status).ShouldBe([StepState.Kept, StepState.WillRun, StepState.WillRun], "a status says what a run will do");
        status.Images[0].Steps.Select(static step => step.ChangedSinceLastRun).ShouldBe([false, true, true]);
        States(after).ShouldBe([StepState.Kept, StepState.Ran, StepState.Ran]);
        _add.Runs.ShouldBe(5, "the step before the changed one did not run");
        after.Images[0].Steps[0].Key.ShouldBe(before.Images[0].Steps[0].Key);
        after.Images[0].Steps[1].Key.ShouldNotBe(before.Images[0].Steps[1].Key);
        after.Images[0].Steps[2].Key.ShouldNotBe(before.Images[0].Steps[2].Key, "a step's key is made from the keys before it");

        // Back to the first setting: those results are still in the store, so nothing runs.
        await EditAsync(folder, project => WithEdit(project, 1, edit => edit with { Settings = Amount(2) }));
        var back = await _runner.RunAsync(folder, cancellationToken: ct);

        States(back).ShouldBe([StepState.Kept, StepState.Kept, StepState.Kept]);
        _add.Runs.ShouldBe(5);
    }

    [Fact(Timeout = 120_000)]
    public async Task AnEditSwitchedOffIsPassedOverAndSwitchedOnAgainRunsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, master) = await ProjectAsync("switch", m => WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", m,
            WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)))));
        await _runner.RunAsync(folder, cancellationToken: ct);

        await EditAsync(folder, project => WithEdit(project, 0, edit => edit with { Enabled = false }));
        var off = await _runner.RunAsync(folder, cancellationToken: ct);

        States(off).ShouldBe([StepState.Off, StepState.Ran]);
        _add.Runs.ShouldBe(3, "the second edit now takes the original, which is another step");
        WorkbenchFixtures.Read(off.Images[0].Steps[1].Output.ShouldNotBeNull())[0, 40, 50]
            .ShouldBe(WorkbenchFixtures.Read(master)[0, 40, 50] + 2f, Tolerance);

        await EditAsync(folder, project => WithEdit(project, 0, edit => edit with { Enabled = true }));
        var on = await _runner.RunAsync(folder, cancellationToken: ct);

        States(on).ShouldBe([StepState.Kept, StepState.Kept]);
        _add.Runs.ShouldBe(3, "both results were in the store from the first run");
    }

    [Fact(Timeout = 120_000)]
    public async Task ADerivedStepStoresNothingAndTheStepAfterItTakesItsPixelsAndItsSolution()
    {
        var ct = TestContext.Current.CancellationToken;
        var crop = new JsonObject { ["x"] = 20, ["y"] = 10, ["width"] = 100, ["height"] = 80 };
        var (folder, master) = await ProjectAsync("derived", m => WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", m,
            WorkbenchFixtures.Edit(CropOperation.Id, crop), WorkbenchFixtures.Edit("add", Amount(1)))));

        var report = await _runner.RunAsync(folder, cancellationToken: ct);

        report.Succeeded.ShouldBeTrue();
        States(report).ShouldBe([StepState.Derived, StepState.Ran]);
        report.Images[0].Steps[0].Output.ShouldBeNull("a derived step has no file");
        Directory.GetFiles(Path.Combine(folder, StepStore.FolderName), "*.fits").ShouldHaveSingleItem("only the stored step's result is in the store");

        Image.TryReadFitsFile(report.Images[0].Steps[1].Output.ShouldNotBeNull(), out var result, out var wcs).ShouldBeTrue();
        var image = result.ShouldNotBeNull();
        (image.Width, image.Height).ShouldBe((100, 80));
        image[0, 5, 7].ShouldBe(WorkbenchFixtures.Read(master)[0, 5 + 10, 7 + 20] + 1f, Tolerance);
        // The crop moved the plate solution with the pixels: the sky at (60, 40) of the master is at (40, 30) of the result.
        var sky = WorkbenchFixtures.Wcs().PixelToSky(60.0, 40.0);
        sky.ShouldNotBeNull();
        wcs.ShouldNotBeNull();
        var moved = wcs.Value.SkyToPixel(sky.Value.RA, sky.Value.Dec);
        moved.ShouldNotBeNull();
        moved.Value.X.ShouldBe(40.0, 1e-6);
        moved.Value.Y.ShouldBe(30.0, 1e-6);

        // A changed rectangle is a changed setting: the step after it runs again.
        await EditAsync(folder, project => WithEdit(project, 0, edit => edit with { Settings = new JsonObject { ["x"] = 21, ["y"] = 10, ["width"] = 100, ["height"] = 80 } }));
        States(await _runner.RunAsync(folder, cancellationToken: ct)).ShouldBe([StepState.Derived, StepState.Ran]);
        _add.Runs.ShouldBe(2);
    }

    [Fact(Timeout = 120_000)]
    public async Task AnImageAnotherTakesIsMadeForItAndAChangeToItRunsTheTaker()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, master) = await ProjectAsync("takes", m => WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("taker", m, WorkbenchFixtures.Edit("blend", Amount(0), "taken")),
            WorkbenchFixtures.Picture("taken", m, WorkbenchFixtures.Edit("add", Amount(5)))));

        var first = await _runner.RunAsync(folder, new WorkbenchRunOptions { Image = "taker" }, ct);

        first.Succeeded.ShouldBeTrue();
        States(first, image: 0).ShouldBe([StepState.Ran]);
        States(first, image: 1).ShouldBe([StepState.Ran], "the image the taker takes was made for it, though only the taker was asked for");
        var value = WorkbenchFixtures.Read(master)[0, 40, 50];
        WorkbenchFixtures.Read(first.Images[0].Steps[0].Output.ShouldNotBeNull())[0, 40, 50].ShouldBe(value + value + 5f, Tolerance);

        await EditAsync(folder, project => project with { Images = project.Images.SetItem(1, project.Images[1] with { Edits = [WorkbenchFixtures.Edit("add", Amount(6))] }) });
        var second = await _runner.RunAsync(folder, cancellationToken: ct);

        States(second, image: 0).ShouldBe([StepState.Ran], "its input's key changed, so its own did");
        States(second, image: 1).ShouldBe([StepState.Ran]);
        (_add.Runs, _blend.Runs).ShouldBe((2, 2));
        States(await _runner.RunAsync(folder, cancellationToken: ct), image: 0).ShouldBe([StepState.Kept]);
    }

    [Fact(Timeout = 120_000)]
    public async Task AStepTwoImagesShareRunsOnceAndAnExportNeverWritesOverAnOriginalOrIntoTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, master) = await ProjectAsync("shared", m => WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("one", m, WorkbenchFixtures.Edit("add", Amount(3))),
            WorkbenchFixtures.Picture("other", m, WorkbenchFixtures.Edit("add", Amount(3)))));

        var report = await _runner.RunAsync(folder, cancellationToken: ct);

        _add.Runs.ShouldBe(1, "the same operation on the same bytes with the same settings is one step");
        States(report, image: 0).ShouldBe([StepState.Ran]);
        States(report, image: 1).ShouldBe([StepState.Kept]);
        report.Images[1].Steps[0].Output.ShouldBe(report.Images[0].Steps[0].Output);

        var original = await File.ReadAllBytesAsync(master, ct);
        var overOriginal = await _runner.RunAsync(folder, new WorkbenchRunOptions { Image = "one", ExportPath = master }, ct);
        var intoStore = await _runner.RunAsync(folder, new WorkbenchRunOptions { Image = "one", ExportPath = Path.Combine(folder, StepStore.FolderName, "mine.fits") }, ct);

        overOriginal.Refusal.ShouldNotBeNull().ShouldContain("never writes over an original");
        overOriginal.Exported.ShouldBeNull();
        (await File.ReadAllBytesAsync(master, ct)).ShouldBe(original);
        intoStore.Refusal.ShouldNotBeNull().ShouldContain("never writes into the step store");
        File.Exists(Path.Combine(folder, StepStore.FolderName, "mine.fits")).ShouldBeFalse();
    }

    // A run can last minutes, and a person (or the application) may save the project meanwhile. What they saved is
    // kept: the run puts its keys only on the edits that are still the ones it planned (the review of #2470).
    [Fact(Timeout = 120_000)]
    public async Task AnEditMadeToTheProjectFileDuringARunSurvivesItAndTakesNoKeyOfAResultItDidNotMake()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, master) = await ProjectAsync("meanwhile", m => WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", m, WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)))));
        // What is saved while the first step runs: another amount for the second edit, and one more image.
        var saved = JsonSerializer.Serialize(
            WorkbenchFixtures.Project(
                WorkbenchFixtures.Picture("m", master, WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(9))),
                WorkbenchFixtures.Picture("late", master, WorkbenchFixtures.Edit("add", Amount(7)))),
            WorkbenchJsonContext.Default.WorkbenchProject);
        _add.DuringRun = () =>
        {
            if (_add.Runs == 1)
            {
                File.WriteAllText(WorkbenchProjectFile.PathIn(folder), saved);
            }
        };

        var report = await _runner.RunAsync(folder, cancellationToken: ct);

        States(report).ShouldBe([StepState.Ran, StepState.Ran], "the run made what it had planned");
        var onDisk = (await WorkbenchProjectFile.ReadAsync(folder, ct)).Project.ShouldNotBeNull();
        onDisk.Images.Select(static image => image.Name).ShouldBe(["m", "late"], "the image added meanwhile is still there");
        var changed = onDisk.Images[0].Edits[1];
        changed.Settings.ShouldNotBeNull()["amount"].ShouldNotBeNull().GetValue<float>().ShouldBe(9f, "and so is the changed setting");
        changed.Key.ShouldBeNull("an edit changed meanwhile takes no key of a result it did not make");
        onDisk.Images[0].Edits[0].Key.ShouldBe(report.Images[0].Steps[0].Key, "an edit that is still the planned one takes its key");
        onDisk.Images[1].Edits[0].Key.ShouldBeNull();

        // The next run works on what was saved.
        _add.DuringRun = null;
        var next = await _runner.RunAsync(folder, cancellationToken: ct);
        States(next, image: 0).ShouldBe([StepState.Kept, StepState.Ran]);
        States(next, image: 1).ShouldBe([StepState.Ran]);
    }

    [Fact(Timeout = 120_000)]
    public async Task AProjectFileThatCannotBeReadWhenARunEndsIsLeftAsItIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("half-edit", m => WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", m, WorkbenchFixtures.Edit("add", Amount(1)))));
        const string halfWay = "{ \"images\": [ { \"name\": \"m\", ";
        _add.DuringRun = () => File.WriteAllText(WorkbenchProjectFile.PathIn(folder), halfWay);

        var report = await _runner.RunAsync(folder, cancellationToken: ct);

        States(report).ShouldBe([StepState.Ran]);
        (await File.ReadAllTextAsync(WorkbenchProjectFile.PathIn(folder), ct)).ShouldBe(halfWay, "an edit caught half way is not written over with the run's own copy");
        report.Succeeded.ShouldBeTrue();
        report.Note.ShouldNotBeNull().ShouldContain("not recorded", Case.Insensitive, "a person who saved a broken file is told");
    }

    [Fact(Timeout = 120_000)]
    public async Task AProjectFileRemovedWhileARunWorksIsNotWrittenBackFromTheRunsCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("removed", m => WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", m, WorkbenchFixtures.Edit("add", Amount(1)))));
        _add.DuringRun = () => File.Delete(WorkbenchProjectFile.PathIn(folder));

        var report = await _runner.RunAsync(folder, cancellationToken: ct);

        File.Exists(WorkbenchProjectFile.PathIn(folder)).ShouldBeFalse("the run's start-of-run copy is not put back over what the person did");
        report.Note.ShouldNotBeNull().ShouldContain("removed", Case.Insensitive, "the note says the file is gone, not only unreadable");
    }

    [Fact(Timeout = 120_000)]
    public async Task ARunThatRecordsEverythingHasNoNote()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("no-note", m => WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", m, WorkbenchFixtures.Edit("add", Amount(1)))));

        (await _runner.RunAsync(folder, cancellationToken: ct)).Note.ShouldBeNull();
    }

    [Fact(Timeout = 120_000)]
    public async Task AnExportThatCannotBeWrittenIsARefusalAndTheRunIsStillRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("export-refused", m => WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("stored", m, WorkbenchFixtures.Edit("add", Amount(1))),
            WorkbenchFixtures.Picture("derived", m, WorkbenchFixtures.Edit(CropOperation.Id, new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 10, ["height"] = 10 }))));
        // A folder stands where the file should go, so nothing can be written under that name.
        var taken = Directory.CreateDirectory(Path.Combine(folder, "taken.fits")).FullName;

        var stored = await _runner.RunAsync(folder, new WorkbenchRunOptions { Image = "stored", ExportPath = taken }, ct);
        var derived = await _runner.RunAsync(folder, new WorkbenchRunOptions { Image = "derived", ExportPath = taken }, ct);
        var overProject = await _runner.RunAsync(folder, new WorkbenchRunOptions { Image = "stored", ExportPath = WorkbenchProjectFile.PathIn(folder) }, ct);

        stored.Refusal.ShouldNotBeNull().ShouldContain("cannot be written");
        stored.Exported.ShouldBeNull();
        stored.Succeeded.ShouldBeFalse();
        derived.Refusal.ShouldNotBeNull().ShouldContain("cannot be written");
        Directory.Exists(taken).ShouldBeTrue();
        overProject.Refusal.ShouldNotBeNull().ShouldContain("never writes over the project file");
        var project = (await WorkbenchProjectFile.ReadAsync(folder, ct)).Project.ShouldNotBeNull("the project file is still a project");
        project.Images[0].Edits[0].Key.ShouldBe(stored.Images[0].Steps[0].Key, "the step that ran is recorded, whatever became of the export");
    }

    [Fact(Timeout = 120_000)]
    public async Task AnOriginalThatCannotBeReadIsARefusalOfThatImageAndNeverAThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, master) = await ProjectAsync("unreadable", m => WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("held", m, WorkbenchFixtures.Edit("add", Amount(1)))));
        // A name that will not open as a file has no key: a folder is refused as a permission is, and a missing file as an I/O error.
        StepKey.OfFile(folder).ShouldBeEmpty();
        StepKey.OfFile(Path.Combine(folder, "none.fits")).ShouldBeEmpty();

        WorkbenchReport status;
        await using (new FileStream(master, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Another program holds the original and shares nothing.
            status = await _runner.StatusAsync(folder, cancellationToken: ct);
        }

        status.Images[0].Refusal.ShouldNotBeNull().ShouldContain("cannot be read");
        States(status).ShouldBe([StepState.Blocked]);
        status.Succeeded.ShouldBeFalse();
    }

    [Fact(Timeout = 120_000)]
    public async Task AStepThatFailsLeavesNothingInTheStoreAndIsTriedAgainNextTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var breaking = new BreakOperation();
        var runner = new WorkbenchRunner(new OperationCatalog([breaking, _add]));
        var (folder, _) = await ProjectAsync("failed", m => WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", m, WorkbenchFixtures.Edit("break"), WorkbenchFixtures.Edit("add"))));
        var store = Path.Combine(folder, StepStore.FolderName);

        var report = await runner.RunAsync(folder, cancellationToken: ct);

        States(report).ShouldBe([StepState.Refused, StepState.Blocked]);
        report.Images[0].Steps[0].Message.ShouldBe(BreakOperation.WhyItFails);
        report.Images[0].Steps[0].Output.ShouldBeNull();
        Directory.GetFiles(store).ShouldBeEmpty("no result, and no index naming one");
        Directory.GetFileSystemEntries(Path.Combine(store, ".staging")).ShouldBeEmpty("the half-written result went with its staging folder");
        _add.Runs.ShouldBe(0, "the step after a failed one does not run");

        (await runner.RunAsync(folder, cancellationToken: ct)).Images[0].Steps[0].State.ShouldBe(StepState.Refused);
        breaking.Runs.ShouldBe(2, "nothing was kept, so it is tried again");
    }

    // Deblur, denoise and star removal put several masters through one run, on one scale (#2465). Each master's edit
    // list names the others, in whatever order, and the run happens once.
    [Fact(Timeout = 120_000)]
    public async Task ImagesThatGoThroughOneRunShareItAndEachTakesItsOwnResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var together = new TogetherOperation();
        var runner = new WorkbenchRunner(new OperationCatalog([together, _add]));
        var (folder, master) = await ProjectAsync("one-run", m => WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("a", m, WorkbenchFixtures.Edit("add", Amount(1))),
            WorkbenchFixtures.Picture("b", m, WorkbenchFixtures.Edit("add", Amount(2))),
            WorkbenchFixtures.Picture("c", m, WorkbenchFixtures.Edit("add", Amount(3))),
            WorkbenchFixtures.Starting("a-out", "a", WorkbenchFixtures.Edit("together", Amount(10), "b", "c")),
            WorkbenchFixtures.Starting("b-out", "b", WorkbenchFixtures.Edit("together", Amount(10), "c", "a")),
            WorkbenchFixtures.Starting("c-out", "c", WorkbenchFixtures.Edit("together", Amount(10), "a", "b"))));

        var report = await runner.RunAsync(folder, cancellationToken: ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        together.Runs.ShouldBe(1, "three edit lists ask for the same run, whatever order each names the others in");
        var outs = report.Images.Skip(3).Select(static image => image.Steps[0]).ToArray();
        outs.Count(static step => step.State == StepState.Ran).ShouldBe(1, "the list that asked first ran it");
        outs.Count(static step => step.State == StepState.Kept).ShouldBe(2, "the other two found their results in the store");
        outs.Select(static step => step.Key).Distinct().Count().ShouldBe(3, "each image's result has a key of its own");
        outs.Select(static step => step.CommandLine).Distinct(EqualityComparer<ImmutableArray<string>>.Create(static (a, b) => a.SequenceEqual(b), static line => line.Length))
            .Count().ShouldBe(1, "and all three say the same command");

        var original = WorkbenchFixtures.Read(master);
        var (first, value) = (original[0, 0, 0], original[0, 40, 50]);
        var shared = (3f * first) + 6f;
        WorkbenchFixtures.Read(outs[0].Output.ShouldNotBeNull())[0, 40, 50].ShouldBe(value + 1f + 10f + shared, 1e-3f);
        WorkbenchFixtures.Read(outs[1].Output.ShouldNotBeNull())[0, 40, 50].ShouldBe(value + 2f + 10f + shared, 1e-3f);
        WorkbenchFixtures.Read(outs[2].Output.ShouldNotBeNull())[0, 40, 50].ShouldBe(value + 3f + 10f + shared, 1e-3f);

        var again = await runner.RunAsync(folder, cancellationToken: ct);
        again.Images.Skip(3).Select(static image => image.Steps[0].State).ShouldBe([StepState.Kept, StepState.Kept, StepState.Kept]);
        together.Runs.ShouldBe(1);

        // Another image in the run is another run: its key is made from every image of it.
        await EditAsync(folder, project => project with { Images = project.Images.SetItem(3, WorkbenchFixtures.Starting("a-out", "a", WorkbenchFixtures.Edit("together", Amount(10), "b"))) });
        var fewer = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "a-out" }, ct);
        fewer.Images[3].Steps[0].State.ShouldBe(StepState.Ran);
        together.Runs.ShouldBe(2);
    }

    [Fact(Timeout = 120_000)]
    public async Task AnImageStartsFromAnotherOrFromAnExtraResultOfOneOfItsSteps()
    {
        var ct = TestContext.Current.CancellationToken;
        var together = new TogetherOperation();
        var runner = new WorkbenchRunner(new OperationCatalog([together, _add]));
        var (folder, master) = await ProjectAsync("starts-from", m => WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("a", m, WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("together", Amount(10))),
            WorkbenchFixtures.Starting("after", "a", WorkbenchFixtures.Edit("add", Amount(100))),
            WorkbenchFixtures.StartingFromResult("twin", "a", TogetherOperation.Twin, WorkbenchFixtures.Edit("add", Amount(1000)))));
        var original = WorkbenchFixtures.Read(master);
        var (first, value) = (original[0, 0, 0], original[0, 40, 50]);

        // Asked for alone, the image that starts from an extra result has the step that gives it run.
        var twinOnly = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "twin" }, ct);

        twinOnly.Succeeded.ShouldBeTrue(string.Join("; ", twinOnly.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        States(twinOnly, image: 0).ShouldBe([StepState.Ran, StepState.Ran]);
        States(twinOnly, image: 1).ShouldBe([StepState.NotNeeded], "nothing asked for the image that goes on from 'a'");
        States(twinOnly, image: 2).ShouldBe([StepState.Ran]);
        together.Runs.ShouldBe(1);
        twinOnly.Images[1].Path.ShouldBe("from a");
        twinOnly.Images[2].Path.ShouldBe("from a (twin)");
        // The twin is the image as the run was handed it: the original plus 1.
        WorkbenchFixtures.Read(twinOnly.Images[2].Steps[0].Output.ShouldNotBeNull())[0, 40, 50].ShouldBe(value + 1f + 1000f, 1e-2f);

        var all = await runner.RunAsync(folder, cancellationToken: ct);

        States(all, image: 0).ShouldBe([StepState.Kept, StepState.Kept]);
        States(all, image: 1).ShouldBe([StepState.Ran]);
        States(all, image: 2).ShouldBe([StepState.Kept]);
        together.Runs.ShouldBe(1);
        // It goes on from where 'a' stops: the original plus 1, through the run of one (plus 10, plus its own first pixel), plus 100.
        WorkbenchFixtures.Read(all.Images[1].Steps[0].Output.ShouldNotBeNull())[0, 40, 50].ShouldBe(value + 1f + 10f + (first + 1f) + 100f, 1e-3f);

        // An image with no edit of its own that starts from an extra result exports that result.
        await EditAsync(folder, project => project with { Images = project.Images.Add(WorkbenchFixtures.StartingFromResult("bare", "a", TogetherOperation.Twin)) });
        var exported = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "bare", ExportPath = Path.Combine(folder, "bare.fits") }, ct);
        WorkbenchFixtures.Read(exported.Exported.ShouldNotBeNull(exported.Refusal))[0, 40, 50].ShouldBe(value + 1f, 1e-5f);
    }

    [Fact(Timeout = 120_000)]
    public async Task AStartThatCannotBeFoundIsRefusedInWords()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new WorkbenchRunner(new OperationCatalog([new TogetherOperation(), _add]));
        var (folder, _) = await ProjectAsync("bad-starts", m => WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("a", m, WorkbenchFixtures.Edit("add")),
            WorkbenchFixtures.Starting("stranger", "nobody", WorkbenchFixtures.Edit("add")),
            WorkbenchFixtures.StartingFromResult("no-such-result", "a", "stars"),
            new ProjectImage { Name = "both", Path = m, From = "a" },
            new ProjectImage { Name = "result-alone", Path = m, Result = TogetherOperation.Twin },
            WorkbenchFixtures.Starting("x", "y"),
            WorkbenchFixtures.Starting("y", "x"),
            WorkbenchFixtures.Picture("same", m),
            WorkbenchFixtures.Picture("twice", m, WorkbenchFixtures.Edit("together", null, "same"))));

        var status = await runner.StatusAsync(folder, cancellationToken: ct);

        ImageReport Of(string name) => status.Images.Single(image => image.Name == name);
        status.Succeeded.ShouldBeFalse();
        Of("a").Refusal.ShouldBeNull();
        Of("stranger").Refusal.ShouldNotBeNull().ShouldContain("starts from the image 'nobody'");
        Of("stranger").Steps[0].State.ShouldBe(StepState.Blocked);
        Of("no-such-result").Refusal.ShouldNotBeNull().ShouldContain("gives a result named 'stars'");
        Of("both").Refusal.ShouldNotBeNull().ShouldContain("one or the other");
        Of("result-alone").Refusal.ShouldNotBeNull().ShouldContain("say which it starts from");
        Of("y").Refusal.ShouldNotBeNull().ShouldContain("in a circle: x -> y -> x");
        Of("x").Refusal.ShouldNotBeNull().ShouldContain("'y', which it starts from, cannot be made");
        Of("twice").Steps[0].State.ShouldBe(StepState.Refused);
        Of("twice").Steps[0].Message.ShouldNotBeNull().ShouldContain("handed one image twice");
    }

    [Fact(Timeout = 120_000)]
    public async Task AStepWhoseLaterResultIsKeptIsNotMadeAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("pull", m => WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", m,
            WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)))));
        var first = await _runner.RunAsync(folder, cancellationToken: ct);
        File.Delete(first.Images[0].Steps[0].Output.ShouldNotBeNull());

        var status = await _runner.StatusAsync(folder, cancellationToken: ct);
        var second = await _runner.RunAsync(folder, cancellationToken: ct);

        States(status).ShouldBe([StepState.NotNeeded, StepState.Kept]);
        States(second).ShouldBe([StepState.NotNeeded, StepState.Kept]);
        _add.Runs.ShouldBe(2, "the step after it has its result, so nothing asks for the missing one");
        second.Succeeded.ShouldBeTrue();
    }

    [Fact(Timeout = 120_000)]
    public async Task TheOriginalFileChangingRunsEveryStepWhateverItsDateSays()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, master) = await ProjectAsync("source", m => WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", m, WorkbenchFixtures.Edit("add", Amount(1)))));
        await _runner.RunAsync(folder, cancellationToken: ct);
        var written = File.GetLastWriteTimeUtc(master);

        // The same bytes under a new date run nothing: a key is a digest, never a date.
        File.SetLastWriteTimeUtc(master, written.AddDays(1));
        States(await _runner.RunAsync(folder, cancellationToken: ct)).ShouldBe([StepState.Kept]);

        // Other bytes under the OLD date run the step.
        WorkbenchFixtures.Master(seed: 2).WriteToFitsFile(master, WorkbenchFixtures.Wcs());
        File.SetLastWriteTimeUtc(master, written);
        var status = await _runner.StatusAsync(folder, cancellationToken: ct);
        var changed = await _runner.RunAsync(folder, cancellationToken: ct);

        status.Images[0].FileChangedSinceLastRun.ShouldBeTrue();
        States(changed).ShouldBe([StepState.Ran]);
        _add.Runs.ShouldBe(2);
    }

    [Fact(Timeout = 120_000)]
    public async Task AStatusRunsNothingAndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("status", m => WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", m,
            WorkbenchFixtures.Edit(CropOperation.Id, new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 10, ["height"] = 10 }),
            WorkbenchFixtures.Edit("add"))));
        var projectFile = WorkbenchProjectFile.PathIn(folder);
        var before = await File.ReadAllBytesAsync(projectFile, ct);

        var status = await _runner.StatusAsync(folder, cancellationToken: ct);

        States(status).ShouldBe([StepState.Derived, StepState.WillRun]);
        _add.Runs.ShouldBe(0);
        Directory.Exists(Path.Combine(folder, StepStore.FolderName)).ShouldBeFalse("a status creates no store");
        (await File.ReadAllBytesAsync(projectFile, ct)).ShouldBe(before);
        // Rule 3: every step says the command that does it, naming the file it reads.
        status.Images[0].Steps[0].CommandLine.ShouldContain("crop");
        status.Images[0].Steps[1].CommandLine[0].ShouldBe("add");
        status.Images[0].Steps[1].CommandLine[1].ShouldEndWith(".fits");
    }

    [Fact(Timeout = 120_000)]
    public async Task ARunRecordsEachStepsKeyAndResultInTheProjectFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, master) = await ProjectAsync("record", m => WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", m,
            WorkbenchFixtures.Edit(CropOperation.Id, new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 10, ["height"] = 10 }),
            WorkbenchFixtures.Edit("add"))));

        var report = await _runner.RunAsync(folder, cancellationToken: ct);

        var project = (await WorkbenchProjectFile.ReadAsync(folder, ct)).Project.ShouldNotBeNull();
        var image = project.Images[0];
        image.Digest.ShouldBe(StepKey.OfFile(master));
        image.Edits[0].Key.ShouldBe(report.Images[0].Steps[0].Key);
        image.Edits[0].Output.ShouldBeNull("a derived step has no file to record");
        image.Edits[1].Key.ShouldBe(report.Images[0].Steps[1].Key);
        var output = image.Edits[1].Output.ShouldNotBeNull();
        // Named relative to the project, so the folder can be moved.
        output.ShouldStartWith(StepStore.FolderName + "/");
        File.Exists(Path.Combine(folder, output)).ShouldBeTrue();

        // And a run that changes nothing leaves the file as it is.
        var bytes = await File.ReadAllBytesAsync(WorkbenchProjectFile.PathIn(folder), ct);
        await _runner.RunAsync(folder, cancellationToken: ct);
        (await File.ReadAllBytesAsync(WorkbenchProjectFile.PathIn(folder), ct)).ShouldBe(bytes);
    }

    [Fact(Timeout = 120_000)]
    public async Task WhatCannotBePlannedIsRefusedInWordsAndBlocksOnlyWhatIsAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("refused", m => WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("unknown", m, WorkbenchFixtures.Edit("add"), WorkbenchFixtures.Edit("sharpen-harder"), WorkbenchFixtures.Edit("add")),
            WorkbenchFixtures.Picture("misspelt", m, WorkbenchFixtures.Edit("add", new JsonObject { ["amont"] = 2 })),
            WorkbenchFixtures.Picture("alone", m, WorkbenchFixtures.Edit("blend")),
            WorkbenchFixtures.Picture("stranger", m, WorkbenchFixtures.Edit("blend", null, "nobody")),
            WorkbenchFixtures.Picture("a", m, WorkbenchFixtures.Edit("blend", null, "b")),
            WorkbenchFixtures.Picture("b", m, WorkbenchFixtures.Edit("blend", null, "a")),
            WorkbenchFixtures.Picture("gone", Path.Combine(Path.GetDirectoryName(m).ShouldNotBeNull(), "no-such-master.fits"), WorkbenchFixtures.Edit("add")),
            WorkbenchFixtures.Picture("fine", m, WorkbenchFixtures.Edit("add", Amount(7)))));

        var report = await _runner.RunAsync(folder, cancellationToken: ct);

        report.Succeeded.ShouldBeFalse();
        ImageReport Of(string name) => report.Images.Single(image => image.Name == name);
        States(report, 0).ShouldBe([StepState.Ran, StepState.Refused, StepState.Blocked], "the step before the unknown one still ran");
        Of("unknown").Steps[1].Message.ShouldNotBeNull().ShouldContain("no operation named 'sharpen-harder'");
        Of("misspelt").Steps[0].State.ShouldBe(StepState.Refused);
        Of("misspelt").Steps[0].Message.ShouldNotBeNull().ShouldContain("amont");
        Of("alone").Steps[0].Message.ShouldNotBeNull().ShouldContain("takes 1 other image(s) (other), and 0 are named");
        Of("stranger").Steps[0].Message.ShouldNotBeNull().ShouldContain("'nobody'");
        Of("a").Steps[0].State.ShouldBeOneOf(StepState.Refused, StepState.Blocked);
        (Of("a").Steps[0].Message + Of("b").Steps[0].Message).ShouldContain("in a circle: a -> b -> a");
        Of("gone").Refusal.ShouldNotBeNull().ShouldContain("no-such-master.fits");
        Of("gone").Steps[0].State.ShouldBe(StepState.Blocked);
        Of("fine").Steps[0].State.ShouldBe(StepState.Ran, "one image's refusal does not stop another");
    }

    [Fact(Timeout = 120_000)]
    public async Task AnOperationTakesItsOptionalImagesOrLeavesThemOutAndNoOtherNumber()
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("optional", m => WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("few", m, WorkbenchFixtures.Edit(LuminanceOperation.Id, null, "a")),
            WorkbenchFixtures.Picture("many", m, WorkbenchFixtures.Edit(LuminanceOperation.Id, null, "a", "a", "a", "a", "a", "a")),
            WorkbenchFixtures.Picture("a", m)));
        var runner = new WorkbenchRunner(new OperationCatalog([new LuminanceOperation()]));

        var status = await runner.StatusAsync(folder, cancellationToken: ct);

        status.Images[0].Steps[0].Message.ShouldNotBeNull().ShouldContain("takes 2 to 5 other image(s)");
        status.Images[0].Steps[0].Message.ShouldNotBeNull().ShouldContain("and 1 are named");
        status.Images[1].Steps[0].Message.ShouldNotBeNull().ShouldContain("and 6 are named");
    }

    [Fact]
    public void ASettingLeftOutIsItsDefaultAndASettingTheOperationDoesNotHaveIsRefused()
    {
        IOperation add = _add;

        add.TryReadSettings(null, out var absent, out _).ShouldBeTrue();
        add.TryReadSettings("{}", out var empty, out _).ShouldBeTrue();
        add.TryReadSettings("""{ "amount": 1 }""", out var stated, out _).ShouldBeTrue();
        add.TryReadSettings("""{ "amount": 2 }""", out var other, out _).ShouldBeTrue();

        absent.ShouldBe(add.DefaultSettings);
        empty.ShouldBe(absent);
        stated.ShouldBe(absent, "a default written out is the same settings, so the same step");
        other.ShouldNotBe(absent);
        add.TryReadSettings("""{ "amont": 2 }""", out _, out var refusal).ShouldBeFalse();
        refusal.ShouldNotBeNull().ShouldContain("amont");
        add.TryReadSettings("""{ "amount": "much" }""", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void AStepsKeyChangesWithEverythingThatMakesItsResultAndWithNothingElse()
    {
        var description = _add.Description;
        var key = StepKey.OfStep(description, """{"amount":1}""", "", ["xxh128:AA", "xxh128:BB"]);

        key.ShouldStartWith(ContentDigest.Algorithm + ":");
        StepKey.OfStep(description, """{"amount":1}""", "", ["xxh128:AA", "xxh128:BB"]).ShouldBe(key);
        StepKey.OfStep(description, """{"amount":2}""", "", ["xxh128:AA", "xxh128:BB"]).ShouldNotBe(key, "the settings");
        StepKey.OfStep(description with { Version = 2 }, """{"amount":1}""", "", ["xxh128:AA", "xxh128:BB"]).ShouldNotBe(key, "the operation's version");
        StepKey.OfStep(description with { Id = "blend" }, """{"amount":1}""", "", ["xxh128:AA", "xxh128:BB"]).ShouldNotBe(key, "the operation");
        StepKey.OfStep(description, """{"amount":1}""", "GraXpert", ["xxh128:AA", "xxh128:BB"]).ShouldNotBe(key, "what served it");
        StepKey.OfStep(description, """{"amount":1}""", "", ["xxh128:BB", "xxh128:AA"]).ShouldNotBe(key, "which input is which");
        StepKey.OfStep(description, """{"amount":1}""", "", ["xxh128:AA"]).ShouldNotBe(key, "the inputs");
        StepKey.OfStep(description with { Title = "Sum" }, """{"amount":1}""", "", ["xxh128:AA", "xxh128:BB"]).ShouldBe(key, "a title is not part of a result");
    }

    [Fact]
    public void ACatalogRefusesTwoOperationsOfOneNameAndOneWhoseKindAndTypeDisagree()
    {
        Should.Throw<ArgumentException>(() => new OperationCatalog([new AddOperation("add"), new AddOperation("add")])).Message.ShouldContain("'add'");
        var catalog = new OperationCatalog([new CropOperation(), new LinearFitOperation(), _add]);
        catalog.All.Select(static operation => operation.Description.Id).ShouldBe(["add", CropOperation.Id, LinearFitOperation.Id]);
        catalog.TryGet(CropOperation.Id, out var crop).ShouldBeTrue();
        crop.ShouldBeAssignableTo<IDerivedOperation>();
        crop.ShouldNotBeNull().Description.Output.ShouldBe(OperationOutputKind.Derived);
    }
}
