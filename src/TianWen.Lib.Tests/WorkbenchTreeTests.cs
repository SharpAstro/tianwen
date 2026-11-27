using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Imaging.Workbench;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The workbench screen's project tree (#2553 section 3), driven through the real host at DPI 1 and 1.5 on fake operations and no
/// wall clock: a frame group and a step are selected, the row's ▶ and the toolbar's button run every step up to the step (the kept
/// ones are not run again), a step with no output says what running it will do, + Step and Remove change the steps through the Lib
/// routines, a switch of workflow asks only after a change by hand, and every mark is the runner's answer.
/// </summary>
[Collection("Viewer")]
public sealed class WorkbenchTreeTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly AddOperation _add = new("add");
    private readonly ServedOperation _served = new();

    public void Dispose() => _folders.Dispose();

    private OperationCatalog Catalog() => new([_add, _served]);

    private WorkbenchRunner Runner() => new(Catalog());

    private static JsonObject Amount(float amount) => new() { ["amount"] = amount };

    // add 1, add 2, served: three steps of the one image "m".
    private async Task<string> ProjectAsync(string name, bool fromAWorkflow = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create(name + "-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        var project = WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master,
            WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)), WorkbenchFixtures.Edit("served")));
        if (fromAWorkflow)
        {
            project = project with { Workflow = BuiltInWorkflows.QuickLook, WorkflowOrigin = WorkbenchWorkflows.SignatureOf(project.Images) };
        }

        await WorkbenchProjectFile.WriteAsync(folder, project, ct);
        return folder;
    }

    private static async Task SettleAsync(WorkbenchE2E e2e, System.Threading.CancellationToken ct)
        => await e2e.PumpUntilAsync(() => !e2e.Actions.Jobs.IsRunning && !e2e.State.IsReading && e2e.State.Plan is not null, "the run to end and the tree to show the runner's answer", ct);

    private static async Task SelectAsync(WorkbenchE2E e2e, int index, System.Threading.CancellationToken ct)
    {
        e2e.Click(e2e.StepCells()[index]);
        await e2e.PumpUntilAsync(() => e2e.State.Plan is { } plan && plan.Index == index, $"the status through step {index + 1}", ct);
    }

    private static string Joined(WorkbenchE2E e2e) => string.Join(' ', string.Join(' ', e2e.Texts().Select(static t => t.Text)).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task EveryMarkIsTheRunnersAnswerAndAPressChangesNone(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ProjectAsync("marks");
        (await Runner().RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 0 }, ct)).Refusal.ShouldBeNull();
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);

        var answer = (await Runner().StatusAsync(folder, "m", ct)).Images.Single().Steps;
        var cells = e2e.StepCells();
        cells.Length.ShouldBe(3);
        for (var i = 0; i < cells.Length; i++)
        {
            e2e.TextsIn(cells[i]).ShouldContain(WorkbenchActions.TreeMarkFor(answer[i].State, answer[i].ChangedSinceLastRun), $"step {i + 1} wears the mark of the runner's state");
            e2e.StepWords(i).ShouldBe(answer[i].Words);
        }

        e2e.TextsIn(cells[0]).ShouldContain("✓", "a kept step has a tick");
        e2e.TextsIn(cells[1]).ShouldNotContain("✓", "a step that is not made has none");
        var runs = _add.Runs;
        e2e.Click(cells[1]);
        e2e.Click(cells[2]);
        _add.Runs.ShouldBe(runs, "a press runs nothing");
        (await Runner().StatusAsync(folder, "m", ct)).Images.Single().Steps.Select(static s => s.State).ShouldBe(answer.Select(static s => s.State));
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AFrameGroupAndAStepAreSelectedFromTheTree(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ProjectAsync("select");
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog(), height: 1100);
        await e2e.OpenProjectAsync(folder, ct);

        // The capture folder is read off the render thread; a project with none shows the groups the test gives it.
        e2e.State.Frames = new WorkbenchFrames(folder,
        [
            new("Lights", ["light_0001.fits", "light_0002.fits"]),
            new("Darks", ["dark_0001.fits"]),
            new("Flats", []),
            new("Bias", []),
        ]);
        e2e.Frame();
        var texts = e2e.Texts().Select(static t => t.Text).ToArray();
        texts.ShouldContain("Lights");
        texts.ShouldContain("2", "a group's count");

        e2e.Click(e2e.Region("Frames:Lights"));
        e2e.State.SelectedFrames.ShouldBe("Lights");
        Joined(e2e).ShouldContain("light_0001.fits");
        Joined(e2e).ShouldContain("light_0002.fits");

        await SelectAsync(e2e, 1, ct);
        e2e.State.SelectedFrames.ShouldBeNull("a step takes the selection");
        e2e.State.SelectedStep.ShouldBe(1);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheRowsPlayRunsEveryStepUpToItAndAKeptStepIsNotRunAgain(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ProjectAsync("play");
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);

        e2e.Click(e2e.Region($"{WorkbenchScreen<RgbaImage>.PlayHitPrefix}m:1"));
        await SettleAsync(e2e, ct);
        _add.Runs.ShouldBe(2, "steps 1 and 2 ran, and nothing after");
        var answer = (await Runner().StatusAsync(folder, "m", ct)).Images.Single().Steps;
        answer[0].State.ShouldBe(StepState.Kept);
        answer[1].State.ShouldBe(StepState.Kept);
        answer[2].State.ShouldBe(StepState.WillRun);
        e2e.StepWords(1).ShouldBe(answer[1].Words, "the tree shows the runner's answer after the run");
        e2e.State.SelectedStep.ShouldBe(1, "the row is selected too");

        // Again: both are kept, so nothing runs.
        e2e.Click(e2e.Region($"{WorkbenchScreen<RgbaImage>.PlayHitPrefix}m:1"));
        await SettleAsync(e2e, ct);
        _add.Runs.ShouldBe(2, "kept steps are not run again");

        // Up to the last: only the one that is not kept runs.
        e2e.Click(e2e.Region($"{WorkbenchScreen<RgbaImage>.PlayHitPrefix}m:2"));
        await SettleAsync(e2e, ct);
        _add.Runs.ShouldBe(2, "the served step is made, the adds before it kept");
        (await Runner().StatusAsync(folder, "m", ct)).Images.Single().Steps[2].State.ShouldBe(StepState.Kept);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheToolbarsRunButtonRunsToTheSelectedStepAndNamesIt(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ProjectAsync("toolbar");
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);
        await SelectAsync(e2e, 1, ct);

        var run = e2e.Texts().Where(static t => t.Text == "▶ Run to Add").OrderBy(static t => t.Bounds.Y).First().Bounds;
        run.Y.ShouldBeLessThan(e2e.Host.Screen.CanvasRect.Y, "the toolbar's button is above the canvas");
        e2e.Click(run);
        await SettleAsync(e2e, ct);

        _add.Runs.ShouldBe(2, "steps 1 and 2 ran, as the row's ▶ would have run them");
        var answer = (await Runner().StatusAsync(folder, "m", ct)).Images.Single().Steps;
        answer.Take(2).Select(static s => s.State).ShouldBe([StepState.Kept, StepState.Kept]);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AStepWithNoOutputSaysWhatRunningItWillDoAndOffersItsPlay(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ProjectAsync("empty");
        (await Runner().RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 0 }, ct)).Refusal.ShouldBeNull();
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);

        await SelectAsync(e2e, 2, ct);
        var words = e2e.Actions.EmptyStateWords();
        words.ShouldBe("Runs Add, then Served. Step 1 is kept.", "what running it does, with the kept step numbers");
        e2e.Actions.ShowsEmptyState.ShouldBeTrue();
        Joined(e2e).ShouldContain("Served has no output yet");
        Joined(e2e).ShouldContain(words);

        // Its own play is on the canvas, beside the toolbar's: pressing it runs the same run.
        var plays = e2e.Texts().Where(static t => t.Text == "▶ Run to Served").OrderBy(static t => t.Bounds.Y).ToArray();
        plays.Length.ShouldBe(2, "the toolbar's button and the canvas's");
        e2e.Click(plays[1].Bounds);
        await SettleAsync(e2e, ct);
        _add.Runs.ShouldBe(2, "the second add ran; the first was kept");
        (await Runner().StatusAsync(folder, "m", ct)).Images.Single().Steps[2].State.ShouldBe(StepState.Kept);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task PlusStepAddsAfterTheSelectedStepAndRemoveTakesItOut(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ProjectAsync("addremove");
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog(), height: 1100);
        await e2e.OpenProjectAsync(folder, ct);
        await SelectAsync(e2e, 0, ct);

        e2e.Click(e2e.Region(WorkbenchScreen<RgbaImage>.AddStepHit));
        var titles = e2e.MenuRows().Select(row => string.Join(' ', e2e.TextsIn(row))).ToArray();
        titles.ShouldContain("Served", "the operations of the catalogue are offered");
        e2e.Click(e2e.MenuRows()[titles.ToList().IndexOf("Served")]);
        await e2e.PumpUntilAsync(() => e2e.State.View?.Image?.Steps.Length == 4 && !e2e.State.IsReading, "the new step in the answer", ct);

        var project = (await WorkbenchProjectFile.ReadAsync(folder, ct)).Project.ShouldNotBeNull();
        project.Images.Single().Edits.Select(static e => e.Operation).ShouldBe(["add", "served", "add", "served"], "added after the selected step");
        e2e.State.SelectedStep.ShouldBe(1, "the new step is selected");

        e2e.ClickText("Remove");
        await e2e.PumpUntilAsync(() => e2e.State.View?.Image?.Steps.Length == 3 && !e2e.State.IsReading, "the step gone from the answer", ct);
        (await WorkbenchProjectFile.ReadAsync(folder, ct)).Project.ShouldNotBeNull().Images.Single().Edits.Select(static e => e.Operation)
            .ShouldBe(["add", "add", "served"]);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task SwitchingWorkflowAsksOnlyAfterAChangeByHandAndSaysWhatIsReplaced(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ProjectAsync("switch", fromAWorkflow: true);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog(), height: 1100);
        await e2e.OpenProjectAsync(folder, ct);
        e2e.Actions.WorkflowTitle.ShouldBe("Quick look", "the tree's title is the workflow's name");
        Joined(e2e).ShouldContain("Quick look");
        WorkbenchWorkflows.HandChanges(e2e.State.View!.Project!).ShouldBe(0);

        // Nothing was changed by hand: no question (the project here has no capture folder, so the apply says so instead).
        e2e.Actions.SwitchWorkflow(BuiltInWorkflows.DeepSkyColour);
        e2e.State.PendingSwitch.ShouldBeNull("nothing of the person's is replaced, so nothing is asked");

        // A step added by hand is a change: now the question comes first, with the Lib's wording.
        await SelectAsync(e2e, 0, ct);
        e2e.Actions.AddStepAfterSelected("served");
        await e2e.PumpUntilAsync(() => e2e.State.View?.Image?.Steps.Length == 4 && !e2e.State.IsReading, "the added step", ct);
        WorkbenchWorkflows.HandChanges(e2e.State.View!.Project!).ShouldBe(1);

        e2e.Actions.SwitchWorkflow(BuiltInWorkflows.DeepSkyColour);
        e2e.State.PendingSwitch.ShouldNotBeNull().Changes.ShouldBe(1);
        e2e.Frame();
        Joined(e2e).ShouldContain(WorkbenchActions.SwitchWords(1));
        Joined(e2e).ShouldContain("results already made are kept");

        e2e.ClickText("Keep my steps");
        e2e.State.PendingSwitch.ShouldBeNull();
        e2e.State.View!.Image!.Steps.Length.ShouldBe(4, "the steps are as they were");
    }
}
