using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The built-in workflows (#2553, "Choose a workflow"): each fit rule on synthetic detections, the best fit, applying one into a
/// project, hand changes counted and reported, a step added and removed, and the tree the screen draws. In memory and in a temporary
/// folder; no frame is read.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchWorkflowTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static WorkflowFacts Facts(string folder = "night", int lights = 40, bool? colour = true, string[]? filters = null, string? track = null)
        => new(folder, lights, colour, [.. filters ?? ["IDAS LPS-D3"]], track, "10P_60s", "10P/Tempel 2");

    private static OperationCatalog Catalog() => new(
    [
        new StackOperation(),
        new CometStackOperation(),
        new WhiteBalanceOperation(),
        new GradientOperation(Substitute.For<IBackgroundExtractor>(), Substitute.For<IGradientCorrector>()),
        new RemoveStarsOperation(),
        new DenoiseOperation(),
        new ScnrOperation(),
        new AddStarsOperation(),
    ]);

    private static WorkflowAnswer Of(WorkflowFacts facts, string id) => BuiltInWorkflows.Answer(facts).Single(each => each.Template.Id == id);

    private string NewCapture()
    {
        var folder = Path.Combine(_folders.Create("capture-").FullName, "night");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.fits"), "x");
        return folder;
    }

    [Fact]
    public void ACometWorkflowFitsAColourCameraWithOneFilterAndATrackAndSaysWhy()
    {
        var answer = Of(Facts(track: "c/track.json"), BuiltInWorkflows.CometColour);

        answer.Fit.Fits.ShouldBeTrue();
        answer.Fit.Why.ShouldContain("colour camera");
        answer.Fit.Why.ShouldContain("one filter (IDAS LPS-D3)");
        answer.Fit.Why.ShouldContain("track.json");
    }

    [Fact]
    public void ACometWorkflowDoesNotFitWithoutATrackAndSaysSo()
    {
        var fit = Of(Facts(track: null), BuiltInWorkflows.CometColour).Fit;

        fit.Fits.ShouldBeFalse();
        fit.Why.ShouldContain("no comet track");
    }

    [Fact]
    public void AMonoCameraFitsNeitherColourWorkflowButFitsAQuickLook()
    {
        var facts = Facts(colour: false, track: "t.json");

        var comet = Of(facts, BuiltInWorkflows.CometColour).Fit;
        var deep = Of(facts, BuiltInWorkflows.DeepSkyColour).Fit;
        comet.Fits.ShouldBeFalse();
        comet.Why.ShouldContain("mono");
        deep.Fits.ShouldBeFalse();
        deep.Why.ShouldContain("mono");
        Of(facts, BuiltInWorkflows.QuickLook).Fit.Fits.ShouldBeTrue();
    }

    [Fact]
    public void SeveralFiltersRuleOutTheColourWorkflowsAndNameTheFilters()
    {
        var fit = Of(Facts(filters: ["L-eXtreme", "UV/IR"], track: "t.json"), BuiltInWorkflows.DeepSkyColour).Fit;

        fit.Fits.ShouldBeFalse();
        fit.Why.ShouldContain("2 filters (L-eXtreme, UV/IR)");
    }

    [Fact]
    public void AnUnstatedSensorIsNotTakenForColour()
    {
        var fit = Of(Facts(colour: null), BuiltInWorkflows.DeepSkyColour).Fit;

        fit.Fits.ShouldBeFalse();
        fit.Why.ShouldContain("do not say whether the camera is colour");
    }

    [Fact]
    public void NoLightsFitOnlyTheEmptyWorkflow()
    {
        var answers = BuiltInWorkflows.Answer(Facts(lights: 0));

        answers.Where(static each => each.Fit.Fits).Select(static each => each.Template.Id).ShouldBe([BuiltInWorkflows.Empty]);
        answers.Single(static each => each.Best).Template.Id.ShouldBe(BuiltInWorkflows.Empty);
    }

    [Fact]
    public void TheBestFitIsTheFirstThatFitsInTheStatedOrder()
    {
        BuiltInWorkflows.Answer(Facts(track: "t.json")).Single(static each => each.Best).Template.Id.ShouldBe(BuiltInWorkflows.CometColour);
        BuiltInWorkflows.Answer(Facts()).Single(static each => each.Best).Template.Id.ShouldBe(BuiltInWorkflows.DeepSkyColour);
        BuiltInWorkflows.Answer(Facts(colour: false)).Single(static each => each.Best).Template.Id.ShouldBe(BuiltInWorkflows.QuickLook);
        BuiltInWorkflows.Answer(Facts()).Count(static each => each.Best).ShouldBe(1);
        BuiltInWorkflows.All.Select(static each => each.Id).ShouldBe(
            [BuiltInWorkflows.CometColour, BuiltInWorkflows.DeepSkyColour, BuiltInWorkflows.MonoLrgb, BuiltInWorkflows.Narrowband, BuiltInWorkflows.QuickLook, BuiltInWorkflows.Empty]);
    }

    [Fact]
    public void EveryTemplateUsesOnlyOperationsTheCatalogHasAndInputsItsImagesHave()
    {
        var catalog = Catalog();
        foreach (var template in BuiltInWorkflows.All)
        {
            var images = template.Build(Facts(track: "t.json"));
            foreach (var edit in images.SelectMany(static image => image.Edits))
            {
                catalog.TryGet(edit.Operation, out _).ShouldBeTrue($"{template.Id} names {edit.Operation}");
                foreach (var input in edit.Inputs)
                {
                    images.Any(image => image.Name == input).ShouldBeTrue($"{template.Id} takes {input}");
                }
            }
        }
    }

    [Fact]
    public async Task ApplyingAWorkflowWritesItsEditsAndNameIntoTheWorkdirAndLeavesTheStoreAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = NewCapture();
        var workdir = WorkbenchProjects.WorkdirOf(frames);
        var kept = Path.Combine(workdir, StepStore.FolderName, "result.fits");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllText(kept, "made");

        var done = await WorkbenchWorkflows.ApplyAsync(frames, BuiltInWorkflows.DeepSkyColour, Facts(frames), ct);

        done.Refusal.ShouldBeNull();
        done.Replaced.ShouldBe(0);
        var (project, _) = await WorkbenchProjectFile.ReadAsync(workdir, ct);
        project.ShouldNotBeNull().Workflow.ShouldBe(BuiltInWorkflows.DeepSkyColour);
        project.Images.Select(static image => image.Name).ShouldBe(["night-stack", "night-starless", "night-stars", "night"]);
        project.Images[0].Edits.Select(static edit => edit.Operation).ShouldBe([StackOperation.Id]);
        project.Images[1].Edits.Select(static edit => edit.Operation).ShouldBe(
            [WhiteBalanceOperation.Id, GradientOperation.Id, RemoveStarsOperation.Id, DenoiseOperation.Id]);
        project.Images[1].Result.ShouldBe("10P_60s");
        project.WorkflowOrigin.Length.ShouldBe(project.Images.Sum(static image => image.Edits.Length));
        File.ReadAllText(kept).ShouldBe("made");
        WorkbenchWorkflows.HandChanges(project).ShouldBe(0);
    }

    [Fact]
    public async Task AnUnknownWorkflowIsRefusedInWordsAndNothingIsWritten()
    {
        var frames = NewCapture();

        var done = await WorkbenchWorkflows.ApplyAsync(frames, "nope", Facts(frames), TestContext.Current.CancellationToken);

        done.Project.ShouldBeNull();
        done.Refusal.ShouldNotBeNull().ShouldContain("there is no workflow 'nope'");
        File.Exists(WorkbenchProjectFile.PathIn(WorkbenchProjects.WorkdirOf(frames))).ShouldBeFalse();
    }

    [Fact]
    public async Task AHandChangeIsCountedAndSwitchingReportsItAsReplaced()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = NewCapture();
        var workdir = WorkbenchProjects.WorkdirOf(frames);
        (await WorkbenchWorkflows.ApplyAsync(frames, BuiltInWorkflows.DeepSkyColour, Facts(frames), ct)).Refusal.ShouldBeNull();

        // Three changes by hand: a setting, a step added, a step removed.
        var (project, _) = await WorkbenchProjectFile.ReadAsync(workdir, ct);
        var starless = project.ShouldNotBeNull().Images[1];
        var changed = starless with
        {
            Edits =
            [
                starless.Edits[0] with { Settings = new JsonObject { ["whiteBalance"] = new JsonArray(1, 1, 1) } },
                .. starless.Edits.Skip(1).Take(2),
            ],
        };
        var edited = project with { Images = [project.Images[0], changed, .. project.Images.Skip(2)] };
        await WorkbenchProjectFile.WriteAsync(workdir, edited, ct);
        var added = await WorkbenchWorkflows.AddStepAsync(workdir, Catalog(), "night-stars", 0, ScnrOperation.Id, cancellationToken: ct);
        added.Refusal.ShouldBeNull();

        (await WorkbenchWorkflows.HandChangesAsync(frames, ct)).ShouldBe(3);

        var switched = await WorkbenchWorkflows.ApplyAsync(frames, BuiltInWorkflows.QuickLook, Facts(frames), ct);

        switched.Refusal.ShouldBeNull();
        switched.Replaced.ShouldBe(3);
        (await WorkbenchWorkflows.HandChangesAsync(frames, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task ASwitchKeepsTheReassignmentsOfFrames()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = NewCapture();
        (await WorkbenchWorkflows.ApplyAsync(frames, BuiltInWorkflows.QuickLook, Facts(frames), ct)).Refusal.ShouldBeNull();
        (await WorkbenchProjects.ReassignAsync(frames, "a.fits", FrameType.Flat, ct)).Refusal.ShouldBeNull();
        (await WorkbenchWorkflows.HandChangesAsync(frames, ct)).ShouldBe(0, "a reassignment is the capture's, not a change to the steps");

        (await WorkbenchWorkflows.ApplyAsync(frames, BuiltInWorkflows.DeepSkyColour, Facts(frames), ct)).Refusal.ShouldBeNull();

        var kept = await WorkbenchProjects.ReassignmentsAsync(frames, ct);
        kept.Select(static each => each.Path).ShouldBe(["a.fits"]);
        (await WorkbenchWorkflows.HandChangesAsync(frames, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task AStepIsAddedAfterAnotherAndRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = NewCapture();
        var workdir = WorkbenchProjects.WorkdirOf(frames);
        await WorkbenchWorkflows.ApplyAsync(frames, BuiltInWorkflows.QuickLook, Facts(frames), ct);

        var added = await WorkbenchWorkflows.AddStepAsync(workdir, Catalog(), "night-look", 0, GradientOperation.Id, cancellationToken: ct);

        added.Project.ShouldNotBeNull().Images.Single(static image => image.Name == "night-look").Edits.Select(static edit => edit.Operation)
            .ShouldBe([WhiteBalanceOperation.Id, GradientOperation.Id]);
        var first = await WorkbenchWorkflows.AddStepAsync(workdir, Catalog(), "night-look", -1, DenoiseOperation.Id, cancellationToken: ct);
        first.Project.ShouldNotBeNull().Images.Single(static image => image.Name == "night-look").Edits[0].Operation.ShouldBe(DenoiseOperation.Id);

        var removed = await WorkbenchWorkflows.RemoveStepAsync(workdir, Catalog(), "night-look", 0, ct);

        removed.Project.ShouldNotBeNull().Images.Single(static image => image.Name == "night-look").Edits.Select(static edit => edit.Operation)
            .ShouldBe([WhiteBalanceOperation.Id, GradientOperation.Id]);
    }

    [Fact]
    public async Task AddAndRemoveRefuseInWords()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = NewCapture();
        var workdir = WorkbenchProjects.WorkdirOf(frames);
        await WorkbenchWorkflows.ApplyAsync(frames, BuiltInWorkflows.DeepSkyColour, Facts(frames), ct);
        var catalog = Catalog();

        (await WorkbenchWorkflows.AddStepAsync(workdir, catalog, "night", 0, "no-such", cancellationToken: ct)).Refusal.ShouldNotBeNull().ShouldContain("there is no operation 'no-such'");
        (await WorkbenchWorkflows.AddStepAsync(workdir, catalog, "nobody", 0, ScnrOperation.Id, cancellationToken: ct)).Refusal.ShouldNotBeNull().ShouldContain("no image named nobody");
        (await WorkbenchWorkflows.AddStepAsync(workdir, catalog, "night", 5, ScnrOperation.Id, cancellationToken: ct)).Refusal.ShouldNotBeNull().ShouldContain("there is no step 5");
        (await WorkbenchWorkflows.AddStepAsync(workdir, catalog, "night", 0, AddStarsOperation.Id, cancellationToken: ct)).Refusal.ShouldNotBeNull().ShouldContain("takes 1 other image(s): stars");
        (await WorkbenchWorkflows.AddStepAsync(workdir, catalog, "night", 0, AddStarsOperation.Id, ["ghost"], ct)).Refusal.ShouldNotBeNull().ShouldContain("no image named ghost");
        (await WorkbenchWorkflows.RemoveStepAsync(workdir, catalog, "night", 4, ct)).Refusal.ShouldNotBeNull().ShouldContain("no step 4");

        // The star removal's result is the start of the stars image: it is removed after that image is.
        (await WorkbenchWorkflows.RemoveStepAsync(workdir, catalog, "night-starless", 2, ct)).Refusal.ShouldNotBeNull().ShouldContain("remove night-stars first");
    }

    [Fact]
    public void TheCometWorkflowsTreeSplitsAfterTheStackAndJoinsBackOut()
    {
        var project = new WorkbenchProject { Images = BuiltInWorkflows.Find(BuiltInWorkflows.CometColour)!.Build(Facts("night", track: "c/track.json")) };

        var rows = WorkbenchWorkflows.Tree(project, Catalog()).Select(static row => (row.Kind, row.Depth, row.Image, row.EditIndex)).ToArray();

        rows.ShouldBe(
        [
            (WorkflowRowKind.Source, 0, "comet-track", -1),
            (WorkflowRowKind.Split, 0, "night-comet-stack", 0),
            (WorkflowRowKind.Step, 1, "night-comet", 0),
            (WorkflowRowKind.Step, 1, "night-comet", 1),
            (WorkflowRowKind.Step, 1, "night-stars", 0),
            (WorkflowRowKind.Step, 1, "night-stars", 1),
            (WorkflowRowKind.Step, 1, "night-stars", 2),
            (WorkflowRowKind.Join, 0, "night", 0),
        ]);
    }

    [Fact]
    public void TheDeepSkyTreeSplitsAtRemoveStarsAndJoinsAtAddStars()
    {
        var project = new WorkbenchProject { Images = BuiltInWorkflows.Find(BuiltInWorkflows.DeepSkyColour)!.Build(Facts("night")) };

        var rows = WorkbenchWorkflows.Tree(project, Catalog()).Select(static row => (row.Kind, row.Depth, row.Image, row.EditIndex)).ToArray();

        rows.ShouldBe(
        [
            (WorkflowRowKind.Step, 0, "night-stack", 0),
            (WorkflowRowKind.Step, 0, "night-starless", 0),
            (WorkflowRowKind.Step, 0, "night-starless", 1),
            (WorkflowRowKind.Split, 0, "night-starless", 2),
            (WorkflowRowKind.Step, 1, "night-stars", 0),
            (WorkflowRowKind.Step, 0, "night-starless", 3),
            (WorkflowRowKind.Join, 0, "night", 0),
        ]);
    }
}
