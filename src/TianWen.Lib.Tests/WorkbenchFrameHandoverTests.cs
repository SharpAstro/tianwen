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
            [new FakeStackOperation(), _add, new ServedOperation(), enhance, new RemoveStarsOperation(remover), new DenoiseOperation(denoiser), new AddStarsOperation()]);
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
}
