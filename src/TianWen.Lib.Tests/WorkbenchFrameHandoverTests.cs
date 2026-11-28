using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Workbench;
using TianWen.UI.Abstractions;
using Xunit;
using Scaling = TianWen.Lib.Tests.WorkbenchEnhanceParityTests.Scaling;

namespace TianWen.Lib.Tests;

/// <summary>
/// The frame is the only writer of the selection, of the view and of IsReading (#2554). Each test starts a job, lets it end with NO
/// frame run in between (the tracker is drained, nothing else), and shows the state is exactly what it was before the press; the
/// frames that follow then apply the job's tail. A write from the job's own thread would show here as a changed selection or view
/// before any frame ran, with no race to hope for.
/// </summary>
[Collection("Viewer")]
public sealed class WorkbenchFrameHandoverTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly AddOperation _add = new("add");

    public void Dispose() => _folders.Dispose();

    private static JsonObject Amount(float amount) => new() { ["amount"] = amount };

    // The masters list's fixtures, as WorkbenchMastersE2ETests has them: a fake stack named by its run and the real enhance.
    private (OperationCatalog Catalog, WorkbenchRunner Runner) Registered()
    {
        var (remover, denoiser) = (new Scaling("Test/Remover", 0.5f), new Scaling("Test/Denoiser", 0.8f));
        var enhance = new EnhanceOperation(new SharpenPipeline(starRemover: remover, denoiser: denoiser));
        var catalog = new OperationCatalog(
            [new FakeStackOperation(), _add, new ServedOperation(), enhance, new RemoveStarsOperation(remover), new DenoiseOperation(denoiser), new AddStarsOperation(),
             new BoomOperation("boom-frames", takesFrameSet: true)]);
        return (catalog, new WorkbenchRunner(catalog));
    }

    private async Task<string> StackedProjectAsync(WorkbenchRunner runner, string group)
    {
        var ct = TestContext.Current.CancellationToken;
        var project = _folders.Create("project-").FullName;
        var capture = Path.Combine(_folders.Create("capture-").FullName, "night-1");
        Directory.CreateDirectory(capture);
        File.WriteAllBytes(Path.Combine(capture, group + ".fits"), new byte[2880]);
        (await WorkbenchProjects.NewAsync(project, capture, ct)).Refusal.ShouldBeNull();
        (await runner.RunAsync(project, cancellationToken: ct)).Succeeded.ShouldBeTrue();
        return project;
    }

    // add 1, add 2, add 3: three steps of the one image "m", none made yet.
    private async Task<string> ThreeStepProjectAsync()
    {
        var folder = _folders.Create("steps-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        var project = WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master,
            WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)), WorkbenchFixtures.Edit("add", Amount(3))));
        await WorkbenchProjectFile.WriteAsync(folder, project, TestContext.Current.CancellationToken);
        return folder;
    }

    [Fact(Timeout = 300_000)]
    public async Task ProcessThisMasterLeavesTheSelectionAndTheViewToTheFrameAndKeepsTheChoiceClosedUntilItsOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var project = await StackedProjectAsync(runner, "m31_L");
        await using var e2e = WorkbenchE2E.Start(1f, runner, catalog);
        await e2e.OpenProjectAsync(project, ct);
        var (step, image) = (e2e.State.SelectedStep, e2e.State.View!.ImageName);
        step.ShouldBe(0);

        e2e.Actions.ProcessMaster("m31_L").ShouldBeTrue();
        await e2e.Tracker.DrainAsync();

        // The chain is in the project and the job is over, and no frame has run: nothing the frame reads has moved.
        e2e.State.SelectedStep.ShouldBe(step, "the job never writes the selection");
        e2e.State.View!.ImageName.ShouldBe(image, "the view on show is still the stack image");
        e2e.State.IsReading.ShouldBeFalse("no read was started from the pool");
        e2e.State.IsProcessingMaster.ShouldBeTrue("the press ends where the frame has applied its open");
        e2e.Actions.ChoiceWaits.ShouldNotBeNull("no choice can land between the chain and its open");
        e2e.Host.IsBusy.ShouldBeTrue("a tail waits for the frame");

        await e2e.PumpUntilAsync(() => e2e.State.View is { ImageName: not "night-1" } && !e2e.State.IsReading && !e2e.State.IsProcessingMaster,
            "the chain's result on show", ct);
        e2e.State.MasterRefusal.ShouldBeNull();
        e2e.State.IsProcessingMaster.ShouldBeFalse();
        e2e.Actions.ChoiceWaits.ShouldBeNull();
    }

    [Fact(Timeout = 300_000)]
    public async Task AChainThatThrowsLeavesThePressToTheFrameAndTheFrameShowsTheRefusalAndEndsThePress()
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var project = await StackedProjectAsync(runner, "m31_L");
        await using var e2e = WorkbenchE2E.Start(1f, runner, catalog);
        await e2e.OpenProjectAsync(project, ct);
        var stackImage = e2e.State.View!.ImageName.ShouldNotBeNull();

        // Behind the view on show, the stack image gains a first step whose operation throws when the chain asks the stack's
        // status: the routine then throws rather than refusing in words, which is the press's onError. It goes before the
        // stack and takes a frame set: a step after a stack, or one that takes only an image, is refused in words before its
        // operation is asked anything.
        var read = (await WorkbenchProjectFile.ReadAsync(project, ct)).Project.ShouldNotBeNull();
        var withBoom = read with
        {
            Images = [.. read.Images.Select(each => each.Name == stackImage
                ? each with { Edits = each.Edits.Insert(0, WorkbenchFixtures.Edit("boom-frames", Amount(1))) }
                : each)],
        };
        await WorkbenchProjectFile.WriteAsync(project, withBoom, ct);

        e2e.Actions.ProcessMaster("m31_L").ShouldBeTrue();
        await FailureQueuedAsync(e2e, ct);

        // The routine threw and its tail waits; no frame has run, so the press is still busy and says nothing yet.
        e2e.State.IsProcessingMaster.ShouldBeTrue("the press ends where the frame applies its failure");
        e2e.State.MasterRefusal.ShouldBeNull("the refusal is the frame's to write");

        await e2e.PumpUntilAsync(() => !e2e.State.IsProcessingMaster, "the press ended", ct);
        e2e.State.MasterRefusal.ShouldBe("m31_L was not processed: boom");
        e2e.Actions.ChoiceWaits.ShouldBeNull("the choice is open again");
        e2e.State.View!.ImageName.ShouldBe(stackImage, "no chain was opened");
    }

    [Fact(Timeout = 300_000)]
    public async Task ACancelledPressEndsOnTheFrameWithNoRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var project = await StackedProjectAsync(runner, "m31_L");
        await using var e2e = WorkbenchE2E.Start(1f, runner, catalog);
        await e2e.OpenProjectAsync(project, ct);
        var stackImage = e2e.State.View!.ImageName;

        // The app is closing: the press's token is cancelled, so the routine ends at its first await (the press's onCancel).
        e2e.CancelApp();
        e2e.Actions.ProcessMaster("m31_L").ShouldBeTrue();
        await e2e.Tracker.DrainAsync();

        e2e.State.IsProcessingMaster.ShouldBeTrue("the cancel's tail is the frame's to apply");
        e2e.Actions.HasTailForFrame.ShouldBeTrue();

        await e2e.PumpUntilAsync(() => !e2e.State.IsProcessingMaster, "the press ended", ct);
        e2e.State.MasterRefusal.ShouldBeNull("a cancel has no refusal to show");
        e2e.State.View!.ImageName.ShouldBe(stackImage, "no chain was opened");
    }

    [Fact(Timeout = 120_000)]
    public async Task ATailThatThrowsNeitherEndsTheFrameNorHoldsBackTheTailsBehindIt()
    {
        var (catalog, runner) = WithBoom();
        await using var e2e = WorkbenchE2E.Start(1f, runner, catalog);
        var ranAfter = false;
        e2e.State.TailsForFrame.Enqueue(static () => throw new InvalidOperationException("a bad tail"));
        e2e.State.TailsForFrame.Enqueue(() => ranAfter = true);

        e2e.Frame();

        ranAfter.ShouldBeTrue("the tail behind the bad one ran in the same frame");
        e2e.Actions.HasTailForFrame.ShouldBeFalse();
    }

    [Fact(Timeout = 120_000)]
    public async Task ATreeEditStartsNoReadFromTheJobAndTheFrameSelectsTheNewStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var folder = await ThreeStepProjectAsync();
        await using var e2e = WorkbenchE2E.Start(1f, runner, catalog);
        await e2e.OpenProjectAsync(folder, ct);
        e2e.Actions.SelectStep(0);
        e2e.State.View!.Image!.Steps.Length.ShouldBe(3);

        e2e.Actions.AddStepAfterSelected("served");
        await e2e.Tracker.DrainAsync();

        e2e.State.IsReading.ShouldBeFalse("the job started no read");
        e2e.State.SelectAfterStatus.ShouldBe(-1, "the job did not ask for a selection");
        e2e.State.View!.Image!.Steps.Length.ShouldBe(3, "the answer on show is the old one");
        e2e.State.SelectedStep.ShouldBe(0);
        e2e.Host.IsBusy.ShouldBeTrue("a tail waits for the frame");

        await e2e.PumpUntilAsync(() => e2e.State.View?.Image?.Steps.Length == 4 && !e2e.State.IsReading, "the new answer on show", ct);
        e2e.State.SelectedStep.ShouldBe(1, "the new step is selected, by the frame");
        e2e.State.SelectAfterStatus.ShouldBe(-1);
    }

    [Fact(Timeout = 120_000)]
    public async Task ARunsSettleStartsItsReadOnTheFrameAndTheFrameAdoptsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var folder = await ThreeStepProjectAsync();
        await using var e2e = WorkbenchE2E.Start(1f, runner, catalog);
        await e2e.OpenProjectAsync(folder, ct);
        e2e.State.View!.Image!.Steps.ShouldAllBe(static s => s.State == StepState.WillRun);

        e2e.Actions.Jobs.TryStart(folder, "m", through: null).ShouldBeTrue();
        await e2e.Tracker.DrainAsync();

        e2e.Actions.Jobs.IsRunning.ShouldBeFalse("the run is over");
        e2e.State.IsReading.ShouldBeFalse("the settle started no read from the pool");
        e2e.State.View!.Image!.Steps.ShouldAllBe(static s => s.State == StepState.WillRun, "the answer on show is the one from before the run");
        e2e.Host.IsBusy.ShouldBeTrue("the settle waits for the frame");

        e2e.Frame();
        (e2e.State.IsReading || e2e.State.View!.Image!.Steps.All(static s => s.State is StepState.Kept or StepState.Ran))
            .ShouldBeTrue("the frame started the settle's read");

        await e2e.PumpUntilAsync(() => !e2e.State.IsReading && e2e.State.View!.Image!.Steps.All(static s => s.State is StepState.Kept or StepState.Ran),
            "the run's answer adopted", ct);
        _add.Runs.ShouldBe(3);
    }

    // A project whose status cannot be read: the one edit's operation throws when the runner asks what serves it (a plan asks that of every step).
    private async Task<string> UnreadableProjectAsync()
    {
        var folder = _folders.Create("boom-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        var project = WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master, WorkbenchFixtures.Edit("boom", Amount(1))));
        await WorkbenchProjectFile.WriteAsync(folder, project, TestContext.Current.CancellationToken);
        return folder;
    }

    private (OperationCatalog Catalog, WorkbenchRunner Runner) WithBoom()
    {
        var catalog = new OperationCatalog([_add, new BoomOperation()]);
        return (catalog, new WorkbenchRunner(catalog));
    }

    // Waits, running no frame, until the failed read has queued its refusal for the frame. The tracker is not drained for this:
    // a drain cancels a keyed read that is still going, which would end it with no failure to hand over.
    private static async Task FailureQueuedAsync(WorkbenchE2E e2e, System.Threading.CancellationToken ct)
    {
        while (!e2e.Actions.HasTailForFrame)
        {
            await Task.Delay(10, ct);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task AFailedReadLeavesTheStateToTheFrameAndTheFrameShowsTheRefusalAndEndsTheRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = WithBoom();
        var folder = await UnreadableProjectAsync();
        await using var e2e = WorkbenchE2E.Start(1f, runner, catalog);

        e2e.Host.OpenProject(folder);
        e2e.Frame();
        e2e.State.IsReading.ShouldBeTrue();
        await FailureQueuedAsync(e2e, ct);

        e2e.State.IsReading.ShouldBeTrue("no frame has run: the failure is the frame's to apply");
        e2e.State.View.ShouldBeNull();

        await e2e.PumpUntilAsync(() => e2e.State.View is not null && !e2e.State.IsReading, "the refused view on show", ct);
        e2e.State.View!.Refusal.ShouldNotBeNull().ShouldStartWith("The project could not be read:");
    }

    [Fact(Timeout = 120_000)]
    public async Task ANewerReadStartedAfterAFailureWinsAndIsNotOverwrittenByTheRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = WithBoom();
        var bad = await UnreadableProjectAsync();
        var good = await ThreeStepProjectAsync();
        await using var e2e = WorkbenchE2E.Start(1f, runner, catalog);

        e2e.Host.OpenProject(bad);
        e2e.Frame();
        await FailureQueuedAsync(e2e, ct);
        // The failure's tail is queued and no frame has run; a newer read begins before the frame drains it.
        e2e.Actions.OpenProject(good).ShouldBeTrue();
        await e2e.PumpUntilAsync(() => e2e.State.View?.Image?.Steps.Length == 3 && !e2e.State.IsReading, "the newer answer on show", ct);

        e2e.State.View!.Folder.ShouldBe(Path.GetFullPath(good));
        e2e.State.View.Refusal.ShouldBeNull("the refused view of the older read never replaces the newer answer");
    }
}

/// <summary>
/// An operation whose command line cannot be made, so a status that asks for it fails. It fails when planned. Taking a
/// frame set (under its own name) it can stand first on a stack image, whose original is a folder of frames; the plan
/// refuses any other step there before asking it anything.
/// </summary>
internal sealed class BoomOperation(string id = "boom", bool takesFrameSet = false) : Operation<AddSettings>
{
    public override OperationDescription Description { get; } = new(
        id, Version: 1, Title: "Boom", Verb: "none", OperationOutputKind.Stored,
        ExtraInputs: [], Settings: [new OperationSetting("amount", "Unused.")]) { Summary = "Fails when planned.", TakesFrameSet = takesFrameSet };

    protected override System.Text.Json.Serialization.Metadata.JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override string ServedBy(AddSettings settings) => throw new InvalidOperationException("boom");

    public override System.Collections.Immutable.ImmutableArray<string> CommandLine(AddSettings settings, System.Collections.Generic.IReadOnlyList<string> inputs, string output)
        => throw new InvalidOperationException("boom");

    public override System.Threading.Tasks.ValueTask<OperationOutcome> RunAsync(AddSettings settings, System.Collections.Generic.IReadOnlyList<OperationInput> inputs,
        string outputPath, Action<string>? say, System.Threading.CancellationToken cancellationToken)
        => System.Threading.Tasks.ValueTask.FromResult(OperationOutcome.Done);
}
