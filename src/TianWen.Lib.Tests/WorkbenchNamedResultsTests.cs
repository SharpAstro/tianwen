using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Workbench;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A stored operation whose results are named by its run: it takes a set of frames by its folder and gives a result for
/// each frame at the folder's top, named as the frame is, holding the frame's first byte plus the amount. Beside each
/// it writes a file that belongs to it. It counts its runs.
/// </summary>
internal sealed class SplitFramesOperation : RunNamedOperation<AddSettings>
{
    public int Runs { get; private set; }

    public override OperationDescription Description { get; } = new(
        "split-frames", Version: 1, Title: "Split", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [], Settings: [])
    {
        NamedByRun = true,
        // It reads a folder of frames, as the stack does; any operation that does not say so is refused one.
        TakesFrameSet = true,
    };

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output)
        => [Description.Id, inputs[0], "-o", output];

    public override ValueTask<NamedResults> RunIntoAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string folder, Action<string>? say,
        CancellationToken cancellationToken)
    {
        Runs++;
        if (inputs[0].Path is not { } frames || !Directory.Exists(frames))
        {
            return ValueTask.FromResult(NamedResults.Refused(OperationOutcome.BadInput($"{inputs[0].Name} is no folder of frames")));
        }

        var results = ImmutableArray.CreateBuilder<NamedResult>();
        foreach (var frame in FileEnumeration.EnumerateFiles(frames, ".fits", recursive: false).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(frame);
            var plane = new float[4, 4];
            plane[0, 0] = File.ReadAllBytes(frame)[0] + settings.Amount;
            // Named as a verb names its masters, and never as the store will name them.
            var master = Path.Combine(folder, $"master_{name}.fits");
            OperationOutput.Write(new Image([plane], BitDepth.Float32, maxValue: 100f, minValue: 0f, pedestal: 0f, imageMeta: WorkbenchFixtures.Meta()), master, null);
            var coverage = Path.Combine(folder, $"master_{name}.coverage.fits");
            File.WriteAllText(coverage, name);
            results.Add(new NamedResult(name, master, [coverage]));
        }

        return ValueTask.FromResult(new NamedResults(OperationOutcome.Done, results.ToImmutable()));
    }
}

/// <summary>An operation that says its results are named by its run and has no way to name them: a catalog refuses it.</summary>
internal sealed class SaysNamedOperation : Operation<AddSettings>
{
    public override OperationDescription Description { get; } = new(
        "says-named", Version: 1, Title: "Says", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [], Settings: [])
    {
        NamedByRun = true,
    };

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output) => [Description.Id];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(OperationOutcome.Done);
}

/// <summary>
/// A step whose results are named by its run (#2467, for the stack of a set of frames): each result is in the store
/// under its name with the files that belong beside it, the step keeps the list of them, and an image of the project
/// starts from one by its name.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchNamedResultsTests : IDisposable
{
    private static readonly DateTime Written = new(2026, 11, 20, 21, 0, 0, DateTimeKind.Utc);

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static void WriteFrame(string folder, string place, byte fill)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, place);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, 2880).ToArray());
        File.SetLastWriteTimeUtc(path, Written);
    }

    private static string Said(WorkbenchReport report)
        => string.Join("; ", report.Images.Select(static image => image.Refusal).Concat(report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));

    private static ImageReport Of(WorkbenchReport report, string name) => report.Images.Single(image => image.Name == name);

    [Fact(Timeout = 120_000)]
    public async Task AStepNamesItsResultsAndAnImageStartsFromOneByItsName()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = _folders.Create("named-").FullName;
        var frames = Path.Combine(_folders.Create("frames-").FullName, "night");
        WriteFrame(frames, "a.fits", 1);
        WriteFrame(frames, "b.fits", 2);
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit("split-frames")),
            WorkbenchFixtures.StartingFromResult("a", "night", "a", WorkbenchFixtures.Edit("add", new JsonObject { ["amount"] = 1 })),
            WorkbenchFixtures.StartingFromResult("b", "night", "b"),
            // The run names no such result.
            WorkbenchFixtures.StartingFromResult("c", "night", "c")), ct);
        var split = new SplitFramesOperation();
        var runner = new WorkbenchRunner(new OperationCatalog([split, new AddOperation("add")]));
        var store = Path.Combine(project, StepStore.FolderName);

        var first = await runner.RunAsync(project, cancellationToken: ct);

        var night = Of(first, "night").Steps[0];
        night.State.ShouldBe(StepState.Ran, Said(first));
        split.Runs.ShouldBe(1);
        // The step keeps the list of what its run named, and its line names the folder the results would go into.
        night.Output.ShouldNotBeNull().ShouldEndWith(StepResultsList.Ending);
        StepResultsList.TryRead(night.Output.ShouldNotBeNull(), out var names).ShouldBeTrue();
        names.ToArray().ShouldBe(["a", "b"]);
        night.CommandLine[^1].ShouldBe(Path.Combine(store, Path.GetFileNameWithoutExtension(StepStore.FileNameOf(night.Key))));

        // Each result is in the store under its own key, as the store names a result, with what belongs beside it.
        var storedB = Path.Combine(store, StepStore.FileNameOf(StepKey.OfResult(night.Key, "b")));
        Of(first, "b").Key.ShouldBe(StepKey.OfResult(night.Key, "b"));
        Of(first, "b").Refusal.ShouldBeNull();
        WorkbenchFixtures.Read(storedB)[0, 0, 0].ShouldBe(3f, "the frame's byte, 2, and the amount, 1");
        var besideB = Path.ChangeExtension(storedB, null) + ".coverage.fits";
        File.ReadAllText(besideB).ShouldBe("b", "a file that belongs beside a result is beside it in the store, under the result's name there");
        Directory.GetFiles(store, "master_*").ShouldBeEmpty("nothing keeps the name its run gave it");

        // An image goes on from a result by its name.
        var a = Of(first, "a").Steps[0];
        a.State.ShouldBe(StepState.Ran);
        WorkbenchFixtures.Read(a.Output.ShouldNotBeNull())[0, 0, 0].ShouldBe(3f, "the frame's byte, 1, the amount, 1, and the edit's 1");

        // A name the run did not give is said, with the names it gave.
        Of(first, "c").Refusal.ShouldNotBeNull().ShouldContain("gave no 'c'");
        Of(first, "c").Refusal.ShouldNotBeNull().ShouldContain("it gave a, b");
        first.Succeeded.ShouldBeFalse();

        var second = await runner.RunAsync(project, cancellationToken: ct);

        Of(second, "night").Steps[0].State.ShouldBe(StepState.Kept);
        Of(second, "a").Steps[0].State.ShouldBe(StepState.Kept);
        // A status reads the same off the list: nothing has to run to know what the step gave.
        Of(second, "c").Refusal.ShouldNotBeNull().ShouldContain("it gave a, b");
        split.Runs.ShouldBe(1);

        // With a file that belongs beside a result gone, the result is not there, so the step is not whole and runs again.
        File.Delete(besideB);
        var status = await runner.StatusAsync(project, cancellationToken: ct);
        var third = await runner.RunAsync(project, cancellationToken: ct);

        Of(status, "night").Steps[0].State.ShouldBe(StepState.WillRun);
        Of(third, "night").Steps[0].State.ShouldBe(StepState.Ran);
        Of(third, "a").Steps[0].State.ShouldBe(StepState.Kept, "the step after a result has its own, and needs nothing before it");
        split.Runs.ShouldBe(2);
        File.Exists(besideB).ShouldBeTrue();

        // Another frame in the set is another step, and what it names is there to start from.
        WriteFrame(frames, "c.fits", 5);
        var fourth = await runner.RunAsync(project, cancellationToken: ct);

        Of(fourth, "night").Steps[0].State.ShouldBe(StepState.Ran);
        Of(fourth, "c").Refusal.ShouldBeNull();
        fourth.Succeeded.ShouldBeTrue(Said(fourth));
        split.Runs.ShouldBe(3);
    }

    [Fact(Timeout = 120_000)]
    public async Task AResultWhoseNameHoldsADotKeepsWhatBelongsBesideItAndIsFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = _folders.Create("named-dot-").FullName;
        var frames = Path.Combine(_folders.Create("frames-").FullName, "night");
        WriteFrame(frames, "m42_0.5s.fits", 2);
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit("split-frames")),
            WorkbenchFixtures.StartingFromResult("m42", "night", "m42_0.5s")), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new SplitFramesOperation(), new AddOperation("add")]));
        var store = Path.Combine(project, StepStore.FolderName);

        var first = await runner.RunAsync(project, cancellationToken: ct);

        var night = Of(first, "night").Steps[0];
        night.State.ShouldBe(StepState.Ran, Said(first));
        var resultKey = StepKey.OfResult(night.Key, "m42_0.5s");
        var storedName = StepStore.FileNameOf(resultKey);
        var stored = Path.Combine(store, storedName);
        File.Exists(stored).ShouldBeTrue();
        // The companion is named after the stored file without its ending, so the dot in the group's name costs it nothing.
        var beside = Path.Combine(store, Path.GetFileNameWithoutExtension(storedName) + ".coverage.fits");
        File.ReadAllText(beside).ShouldBe("m42_0.5s");

        var second = await runner.RunAsync(project, cancellationToken: ct);

        Of(second, "night").Steps[0].State.ShouldBe(StepState.Kept);
        Of(second, "m42").Refusal.ShouldBeNull(Said(second));
        var opened = await StepStore.OpenAsync(store, ct);
        opened.Find(resultKey).ShouldBe(stored);
    }

    [Fact(Timeout = 120_000)]
    public async Task WhatWouldTakeTheStepItselfAsAnImageIsRefusedInWords()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = _folders.Create("named-refusals-").FullName;
        var frames = Path.Combine(_folders.Create("frames-").FullName, "night");
        WriteFrame(frames, "a.fits", 1);
        var master = WorkbenchFixtures.WriteMaster(project, "master", WorkbenchFixtures.Master(seed: 1));
        var capture = Path.Combine(project, "capture.ser");
        File.WriteAllBytes(capture, Enumerable.Repeat((byte)3, 512).ToArray());
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit("split-frames")),
            // An edit after it in the same list.
            WorkbenchFixtures.Picture("after", frames, WorkbenchFixtures.Edit("split-frames"), WorkbenchFixtures.Edit("add")),
            // An image that starts from it and names none of its results.
            WorkbenchFixtures.Starting("whole", "night"),
            // An edit that takes it as its other image.
            WorkbenchFixtures.Picture("m", master, WorkbenchFixtures.Edit("add-other", null, "night")),
            // And a file that is no FITS by its name, handed to an operation that reads an image.
            WorkbenchFixtures.Picture("ser", capture, WorkbenchFixtures.Edit("add"))), ct);
        var split = new SplitFramesOperation();
        var runner = new WorkbenchRunner(new OperationCatalog([split, new AddOperation("add"), new AddOperation("add-other", takesAnother: true)]));

        var report = await runner.RunAsync(project, cancellationToken: ct);

        Of(report, "night").Steps[0].State.ShouldBe(StepState.Ran, Said(report));
        Of(report, "after").Steps[0].State.ShouldBe(StepState.Kept, "the same set under the same settings is the same step");
        Of(report, "after").Steps[1].State.ShouldBe(StepState.Refused);
        Of(report, "after").Steps[1].Message.ShouldNotBeNull().ShouldContain("has no image to take");
        Of(report, "whole").Refusal.ShouldNotBeNull().ShouldContain("say which one");
        Of(report, "m").Steps[0].State.ShouldBe(StepState.Refused);
        Of(report, "m").Steps[0].Message.ShouldNotBeNull().ShouldContain("no image of its own");
        Of(report, "ser").Steps[0].State.ShouldBe(StepState.Refused);
        Of(report, "ser").Steps[0].Message.ShouldNotBeNull().ShouldContain("is no FITS image");
        split.Runs.ShouldBe(1);

        // The step has no image to export either.
        var export = await runner.RunAsync(project, new WorkbenchRunOptions { Image = "night", ExportPath = Path.Combine(project, "out", "night.fits") }, ct);

        export.Refusal.ShouldNotBeNull().ShouldContain("export an image that starts from one of them");
        File.Exists(Path.Combine(project, "out", "night.fits")).ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task TheOperationsListingSaysWhatAStepNamedByItsRunGives()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Substitute.For<IConsoleHost>();
        var catalog = new OperationCatalog([new SplitFramesOperation(), new AddOperation("add")]);
        var workbench = new WorkbenchSubCommand(host, catalog, new WorkbenchRunner(catalog)).Build();

        (await workbench.Parse(["operations"]).InvokeAsync(cancellationToken: ct)).ShouldBe(0);

        // Once, for split-frames: the add operation names its results beforehand and gets no such line.
        host.Received(1).WriteScrollable(Arg.Is<string>(line => line.Contains("named by the run", StringComparison.Ordinal)));
        host.Received(1).WriteScrollable(Arg.Is<string>(line => line.Contains(
            "gives one result for each group the scan finds, named by the run; an image takes one with from and result, and the step has no image of its own",
            StringComparison.Ordinal)));
    }

    [Fact]
    public void ACatalogRefusesAnOperationThatSaysItsResultsAreNamedByItsRunAndCannotNameThem()
    {
        Should.Throw<ArgumentException>(() => new OperationCatalog([new SaysNamedOperation()])).Message.ShouldContain("'says-named'");
        new OperationCatalog([new SplitFramesOperation()]).TryGet("split-frames", out var split).ShouldBeTrue();
        split.ShouldBeAssignableTo<IRunNamedOperation>();
    }
}
