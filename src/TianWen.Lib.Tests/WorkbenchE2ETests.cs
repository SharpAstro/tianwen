using System;
using System.Collections.Immutable;
using System.IO;
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
/// <c>tianwen-edit</c>'s guided screen, driven through the real host at DPI 1 and 1.5 (#1642, W2): a project folder is opened and
/// the image's edits are drawn left to right, each step in the state the runner answered, a press selects a step and draws its
/// panel, and the original is shown through the viewer.
/// </summary>
[Collection("Viewer")]
public sealed class WorkbenchE2ETests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly AddOperation _add = new("add");
    private readonly ServedOperation _served = new();

    public void Dispose() => _folders.Dispose();

    private readonly GateOperation _gate = new();

    private OperationCatalog Catalog() => new([_add, _served, _gate]);

    private WorkbenchRunner Runner() => new(Catalog());

    private static JsonObject Amount(float amount) => new() { ["amount"] = amount };

    // add 1, add 2, served, ghost (no such operation: refused), add 5 (after it: blocked).
    private async Task<(string Folder, string Master)> ProjectAsync(string name = "edit", bool refusedStep = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create(name + "-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        ProjectEdit[] edits = refusedStep
            ?
            [
                WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)),
                WorkbenchFixtures.Edit("served"), WorkbenchFixtures.Edit("ghost"), WorkbenchFixtures.Edit("add", Amount(5)),
            ]
            : [WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)), WorkbenchFixtures.Edit("served")];
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master, edits)), ct);
        return (folder, master);
    }

    private static ImageReport Image(WorkbenchReport report) => report.Images.Single();

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheEditsAreDrawnLeftToRightEachNamedByItsOperationsTitle(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync();
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());

        await e2e.OpenProjectAsync(folder, ct);

        var cells = e2e.StepCells();
        cells.Length.ShouldBe(5, "one cell for each edit of the image");
        for (var i = 1; i < cells.Length; i++)
        {
            cells[i].X.ShouldBeGreaterThan(cells[i - 1].X, "the steps run left to right");
            cells[i].Y.ShouldBe(cells[0].Y, 0.5f, "on one row");
            cells[i].Height.ShouldBe(cells[0].Height, 0.5f, "of one height");
        }

        // Named by the operation's title, numbered from one; an operation this computer does not know is named by its id.
        e2e.TextsIn(cells[0]).ShouldContain("1  Add");
        e2e.TextsIn(cells[1]).ShouldContain("2  Add");
        e2e.TextsIn(cells[2]).ShouldContain("3  Served");
        e2e.TextsIn(cells[3]).ShouldContain("4  ghost");
        e2e.TextsIn(cells[4]).ShouldContain("5  Add");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task EachStepsStateComesFromTheRunnersAnswerInWordsAndMarksAndNotFromAPress(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync();
        // Two steps made, so the runner can say kept for them.
        (await Runner().RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 1 }, ct)).Refusal.ShouldBeNull();
        var runsBefore = _add.Runs;
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());

        await e2e.OpenProjectAsync(folder, ct);

        var expected = Image(await Runner().StatusAsync(folder, "m", ct)).Steps;
        expected.Select(static s => s.State).ShouldBe(
            [StepState.Kept, StepState.Kept, StepState.WillRun, StepState.Refused, StepState.Blocked],
            "the project holds two kept steps, a step that will run, one the runner refuses and one behind it");
        var cells = e2e.StepCells();
        for (var i = 0; i < expected.Length; i++)
        {
            var texts = e2e.TextsIn(cells[i]).ToArray();
            texts.ShouldContain(expected[i].Words, $"step {i + 1} says its state in the runner's words");
            texts.ShouldContain(WorkbenchActions.MarkFor(expected[i].State), $"step {i + 1} wears the mark of its state");
        }

        e2e.TextsIn(cells[0]).ShouldContain("✓", "a kept step has a tick");
        e2e.TextsIn(cells[2]).ShouldNotContain("✓", "a step that will run has none");

        // A press selects and asks nothing: no step ran, and no state moved.
        e2e.Click(cells[2]);
        e2e.Click(cells[3]);
        _add.Runs.ShouldBe(runsBefore, "a press runs nothing");
        Image(await Runner().StatusAsync(folder, "m", ct)).Steps.Select(static s => s.State).ShouldBe(expected.Select(static s => s.State));
        e2e.State.View!.Image!.Steps.Select(static s => s.State).ShouldBe(expected.Select(static s => s.State), "the bar still shows the runner's answer");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ARefusedAndABlockedStepSayWhyInTheirPanels(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync();
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);
        var expected = Image(await Runner().StatusAsync(folder, "m", ct)).Steps;
        expected[3].Message.ShouldNotBeNullOrWhiteSpace("the runner says why a step is refused");
        expected[4].Message.ShouldNotBeNullOrWhiteSpace("and why one behind it is blocked");

        e2e.Click(e2e.StepCells()[3]);
        Normalise(e2e.PanelText()).ShouldContain(Normalise(expected[3].Message!), Case.Insensitive, "a refused step's panel carries the runner's reason");

        e2e.Click(e2e.StepCells()[4]);
        Normalise(e2e.PanelText()).ShouldContain(Normalise(expected[4].Message!), Case.Insensitive, "and a blocked one's");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task APressSelectsAStepAndDrawsItsSummarySettingsServedByAndCommandLine(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("panel", refusedStep: false);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog(), height: 1500);
        await e2e.OpenProjectAsync(folder, ct);
        var report = Image(await Runner().StatusAsync(folder, "m", ct));

        // The second step: an add by 2.
        e2e.Click(e2e.StepCells()[1]);
        e2e.State.SelectedStep.ShouldBe(1);
        var panel = Normalise(e2e.PanelText());
        panel.ShouldContain("2 Add", Case.Insensitive);
        panel.ShouldContain(Normalise(_add.Description.Summary), Case.Insensitive, "what the operation does");
        panel.ShouldContain("Settings");
        panel.ShouldContain("amount", Case.Insensitive, "a setting by its name");
        panel.ShouldContain(Normalise(_add.Description.Settings[0].Summary), Case.Insensitive, "and what it does");
        e2e.TextsIn(PanelRect(e2e)).ShouldContain("2", "its value as the operation reads it");
        panel.ShouldContain("Command line");
        // A long argument is broken into pieces that each fit, so every character is painted: without the spaces between
        // pieces the panel holds the whole line, and no word lies outside the panel.
        Compact(e2e.PanelText()).ShouldContain(Compact(StepCommandLine.Display(report.Steps[1].CommandLine)), Case.Insensitive,
            "the command line, wrapped into the panel and not cut");
        var panelRect = PanelRect(e2e);
        e2e.Texts().Where(t => t.Bounds.Y >= panelRect.Y && t.Bounds.X < panelRect.Width)
            .ShouldAllBe(t => t.Bounds.X + t.Bounds.Width <= panelRect.Width + 0.5f, "no word of the panel runs past its edge");

        // Shown, not edited: no field and nothing to press beside the settings but the Copy button.
        var inputs = new System.Collections.Generic.List<TextInputState>();
        e2e.Host.Screen.CollectRegisteredTextInputs(inputs);
        inputs.ShouldBeEmpty("a setting is shown and not edited in W2");

        // The third step is served: the panel says by what.
        e2e.Click(e2e.StepCells()[2]);
        var served = Normalise(e2e.PanelText());
        served.ShouldContain("Served by", Case.Insensitive);
        served.ShouldContain("Test/Backend", Case.Insensitive);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task CopyPutsTheSelectedStepsCommandLineOnTheClipboard(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("copy", refusedStep: false);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog(), height: 1500);
        await e2e.OpenProjectAsync(folder, ct);
        var report = Image(await Runner().StatusAsync(folder, "m", ct));
        e2e.Click(e2e.StepCells()[1]);

        e2e.Click(e2e.Region("Copy"));

        e2e.Clipboard.ShouldBe([StepCommandLine.Display(report.Steps[1].CommandLine)], "the whole line, not what the panel could show");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheOriginalViewShowsTheImagesFileThroughTheViewer(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, master) = await ProjectAsync("original", refusedStep: false);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());

        await e2e.OpenProjectAsync(folder, ct);
        await e2e.PumpUntilAsync(() => e2e.Controller.Document is { } document
            && string.Equals(document.FilePath, master, StringComparison.OrdinalIgnoreCase), "the original to open", ct);

        string.Equals(e2e.Controller.Document!.FilePath, master, StringComparison.OrdinalIgnoreCase).ShouldBeTrue("the viewer holds the image's original");
        var canvas = e2e.Host.Screen.CanvasRect;
        canvas.Width.ShouldBeGreaterThan(0f);
        e2e.Viewer.ImageArea.X.ShouldBeGreaterThanOrEqualTo(canvas.X - 0.5f, "the viewer paints inside the canvas the layout placed");
        e2e.Viewer.ImageArea.Width.ShouldBeLessThanOrEqualTo(canvas.Width + 0.5f);
        e2e.State.OriginalNote.ShouldBeNull();
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AnOriginalThatIsAFolderOfFramesSaysSoInWords(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("frames-").FullName;
        var frames = Directory.CreateDirectory(Path.Combine(folder, "lights")).FullName;
        await WorkbenchProjectFile.WriteAsync(folder,
            WorkbenchFixtures.Project(WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit("add", Amount(1)))), ct);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());

        await e2e.OpenProjectAsync(folder, ct);

        e2e.State.OriginalNote.ShouldNotBeNull();
        Normalise(string.Join(' ', e2e.Texts().Select(static t => t.Text))).ShouldContain("folder of frames", Case.Insensitive);
        e2e.Controller.Document.ShouldBeNull("nothing was given to the viewer");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheTopBarHoldsTheProjectAndOpenProjectAndNothingForScreensThatDoNotExist(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("topbar", refusedStep: false);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);

        var cells = e2e.StepCells();
        var topBar = new RectF32(0, 0, e2e.Renderer.Width, cells[0].Y);
        var words = string.Join(' ', e2e.TextsIn(topBar));
        words.ShouldContain(Path.GetFileName(folder), Case.Sensitive, "the project folder");
        words.ShouldContain("Open project");
        foreach (var absent in new[] { "Guided", "Stack", "Process", "Workflow" })
        {
            words.ShouldNotContain(absent, Case.Insensitive, $"nothing in the top bar for {absent}");
        }

        // One image: no choice of image is offered.
        e2e.Regions().Any(static r => r.Result is HitResult.ButtonHit hit && hit.Action.StartsWith("Image:", StringComparison.Ordinal))
            .ShouldBeFalse("an image choice only where the project has more than one");
    }

    // Selects a step by pressing its cell and pumps until the runner's status through it is on the bar.
    private static async Task SelectAsync(WorkbenchE2E e2e, int index, System.Threading.CancellationToken ct)
    {
        e2e.Click(e2e.StepCells()[index]);
        await e2e.PumpUntilAsync(() => e2e.State.Plan is { } plan && plan.Index == index, $"the status through step {index + 1}", ct);
    }

    private static async Task SettleAsync(WorkbenchE2E e2e, System.Threading.CancellationToken ct)
        => await e2e.PumpUntilAsync(() => !e2e.Actions.Jobs.IsRunning && e2e.State.Plan is not null && !e2e.State.IsReading, "the run to end and the bar to show the runner's answer", ct);

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task SelectingAStepSaysBeforeAnythingRunsWhichEarlierStepsWillRunAndWhichAreKeptAndTheButtonSaysWhatItWillDo(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("plan", refusedStep: false);
        (await Runner().RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 0 }, ct)).Refusal.ShouldBeNull();
        var runs = _add.Runs;
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);
        e2e.State.SelectedStep.ShouldBe(0);
        await e2e.PumpUntilAsync(() => e2e.State.Plan is not null, "the first plan", ct);

        await SelectAsync(e2e, 2, ct);

        var cells = e2e.StepCells();
        e2e.TextsIn(cells[0]).ShouldContain("kept", "step 1 is in the store");
        e2e.TextsIn(cells[1]).ShouldContain("will run", "step 2 is not, and step 3 needs it");
        e2e.TextsIn(cells[2]).ShouldContain("will run");
        Normalise(e2e.PanelText()).ShouldContain("Run to here (runs 2 first)", Case.Sensitive, "the button says what runs before");
        _add.Runs.ShouldBe(runs, "selecting ran nothing");

        await SelectAsync(e2e, 0, ct);
        Normalise(e2e.PanelText()).ShouldContain("Nothing to run: this step is kept", Case.Sensitive);
        e2e.Texts().ShouldNotContain(t => t.Text == "Run to here", "no button to press where nothing needs to run");

        await SelectAsync(e2e, 1, ct);
        e2e.Texts().ShouldContain(t => t.Text == "Run to here", "a step that will run, with nothing before it to run first, says so plainly");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task RunToHereMakesTheStepsResultAndTheBarThenShowsTheRunnersAnswer(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("runhere", refusedStep: false);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);
        await SelectAsync(e2e, 1, ct);

        e2e.ClickText("Run to here (runs 1 first)");
        await SettleAsync(e2e, ct);

        _add.Runs.ShouldBe(2, "steps 1 and 2 ran, and nothing after");
        var cells = e2e.StepCells();
        var answer = Image(await Runner().StatusAsync(folder, "m", ct)).Steps;
        for (var i = 0; i < cells.Length; i++)
        {
            e2e.TextsIn(cells[i]).ShouldContain(answer[i].Words, $"step {i + 1} shows the runner's state after the run");
        }

        answer[0].State.ShouldBe(StepState.Kept);
        answer[1].State.ShouldBe(StepState.Kept);
        answer[2].State.ShouldBe(StepState.WillRun);
        Normalise(e2e.PanelText()).ShouldContain("The run finished.", Case.Sensitive, "the job panel says how it ended");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task RunOnGoesToTheEndOfTheListAndStopsAtTheFirstRefusedStepAndSaysWhy(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("runon");
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);
        e2e.State.RunOn.ShouldBeTrue("on by default");
        await e2e.PumpUntilAsync(() => e2e.State.Plan is not null, "the first plan", ct);

        e2e.ClickText("Run on from step 1");
        await SettleAsync(e2e, ct);

        _add.Runs.ShouldBe(2, "the adds before the refused step ran, and the one behind it did not");
        var reason = Image(await Runner().StatusAsync(folder, "m", ct)).Steps[3].Message!;
        e2e.State.SelectedStep.ShouldBe(3, "the step that stopped the run is selected");
        var panel = Normalise(e2e.PanelText());
        panel.ShouldContain("Stopped at step 4", Case.Sensitive);
        panel.ShouldContain(Normalise(reason), Case.Insensitive, "and why");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task WithTheSwitchOffARunMakesOnlyTheNextStepThatIsNotKeptAndStopsThere(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("runoff", refusedStep: false);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);
        await e2e.PumpUntilAsync(() => e2e.State.Plan is not null, "the first plan", ct);

        e2e.Click(e2e.Region("RunOn"));
        e2e.State.RunOn.ShouldBeFalse();
        e2e.ClickText("Run step 1");
        await SettleAsync(e2e, ct);

        _add.Runs.ShouldBe(1, "one step");
        e2e.State.SelectedStep.ShouldBe(0, "stopped with that step selected");
        e2e.Texts().ShouldContain(t => t.Text == "Run step 2", "the next step is the one the button now offers");

        e2e.ClickText("Run step 2");
        await SettleAsync(e2e, ct);
        _add.Runs.ShouldBe(2);
        e2e.State.SelectedStep.ShouldBe(1);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task OneRunAtATimeTheJobPanelShowsTheStepItsFractionAndItsLinesAndCancelEndsItWithTheBarShowingTheRunnersState(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("gate-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master,
            WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("gate"))), ct);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);
        await SelectAsync(e2e, 1, ct);

        e2e.ClickText("Run to here (runs 1 first)");
        await e2e.PumpFramesUntilAsync(() => _gate.Started.Task.IsCompleted && e2e.Actions.Jobs.State.Fraction is not null, "the step at its gate", ct);

        var panel = Normalise(e2e.PanelText());
        panel.ShouldContain("Step 2, Gate: 50 percent", Case.Sensitive, "the running step, and its fraction in words");
        panel.ShouldContain("at the gate", Case.Sensitive, "the run's line");
        e2e.Texts().ShouldContain(t => t.Text == "Cancel");
        e2e.Texts().ShouldNotContain(t => t.Text.StartsWith("Run to here", StringComparison.Ordinal) || t.Text.StartsWith("Run on", StringComparison.Ordinal),
            "no second start is offered while one runs");
        e2e.Actions.Jobs.TryStart(folder, "m", through: null).ShouldBeFalse();

        e2e.ClickText("Cancel");
        await SettleAsync(e2e, ct);

        e2e.Actions.Jobs.State.Outcome.ShouldBe(WorkbenchJobOutcome.Cancelled);
        Normalise(e2e.PanelText()).ShouldContain("The run was cancelled.", Case.Sensitive);
        var answer = Image(await Runner().StatusAsync(folder, "m", ct)).Steps;
        answer[0].State.ShouldBe(StepState.Kept, "step 1 finished before the gate");
        var cells = e2e.StepCells();
        e2e.TextsIn(cells[1]).ShouldContain(answer[1].Words, "the bar shows what the runner says after the cancel");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ResultShowsTheSelectedStepsStoredResultAndComparisonItsInputAgainstItsResultThroughTheSplit(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, master) = await ProjectAsync("views", refusedStep: false);
        (await Runner().RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 1 }, ct)).Refusal.ShouldBeNull();
        var steps = Image(await Runner().StatusAsync(folder, "m", ct)).Steps;
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);
        await e2e.PumpUntilAsync(() => e2e.Controller.Document is not null, "the original", ct);
        await SelectAsync(e2e, 1, ct);

        e2e.Click(e2e.Region("View:Result"));
        await e2e.PumpUntilAsync(() => SameFile(e2e.Controller.Document?.FilePath, steps[1].Output), "step 2's result to open", ct);
        e2e.Viewer.Split.IsOn.ShouldBeFalse();

        e2e.Click(e2e.Region("View:Comparison"));
        await e2e.PumpUntilAsync(() => e2e.Viewer.Split.IsOn && SameFile(e2e.Controller.Document?.FilePath, steps[1].Output), "the comparison", ct);
        e2e.Viewer.Split.Mode.ShouldBe(SplitCompare.BeforePixels, "the input's pixels were retained, so the split compares pixels");
        e2e.Viewer.Retentions.ShouldBe(1);
        e2e.Viewer.HasBeforeImageTextures.ShouldBeTrue();
        e2e.Viewer.Split.PixelsGeneration.ShouldBe(e2e.ViewerState.SourceGeneration, "the before pixels belong to what is on show");

        // Step 1's input is the original file; step 2's is step 1's stored result.
        e2e.Click(e2e.Region("View:Original"));
        await e2e.PumpUntilAsync(() => SameFile(e2e.Controller.Document?.FilePath, master) && !e2e.Viewer.Split.IsOn, "the original again", ct);
        e2e.Viewer.HasBeforeImageTextures.ShouldBeFalse("leaving the comparison frees the retained pixels");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AStepWithNoFileOfItsOwnSaysSoInWordsAndTheChoiceIsNotOffered(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("nofile", refusedStep: false);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);
        await SelectAsync(e2e, 1, ct);

        e2e.Texts().ShouldContain(t => t.Text == "Original");
        e2e.Texts().ShouldNotContain(t => t.Text == "Result", "step 2 is not made yet");
        e2e.Texts().ShouldNotContain(t => t.Text == "Comparison");
        var panel = Normalise(e2e.PanelText());
        panel.ShouldContain("Step 2 has not been made yet.", Case.Sensitive, "said in words");
        panel.ShouldContain("Step 2 has no result to compare", Case.Sensitive);

        // The unknown operation is refused: nothing was made, and the words say so.
        var (refusedFolder, _) = await ProjectAsync("nofile2");
        await e2e.OpenProjectAsync(refusedFolder, ct);
        await SelectAsync(e2e, 3, ct);
        Normalise(e2e.PanelText()).ShouldContain("Step 4 made nothing: it is refused.", Case.Sensitive);
        e2e.Texts().ShouldNotContain(t => t.Text == "Result");
    }

    private static bool SameFile(string? a, string? b)
        => a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static RectF32 PanelRect(WorkbenchE2E e2e)
    {
        var cells = e2e.StepCells();
        var top = cells.Max(static c => c.Y + c.Height);
        return new RectF32(0, top, e2e.Host.Screen.CanvasRect.X, e2e.Renderer.Height - top);
    }

    private static string Compact(string text) => string.Concat(text.Where(static c => !char.IsWhiteSpace(c)));

    // Words painted one leaf at a time are joined by single spaces; a message's own line breaks and doubled spaces are not what is checked.
    private static string Normalise(string text) => string.Join(' ', text.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
}
