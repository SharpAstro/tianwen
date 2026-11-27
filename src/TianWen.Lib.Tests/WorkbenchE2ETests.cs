using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DIR.Lib;
using NSubstitute;
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
        cells.Length.ShouldBe(5, "one row for each edit of the image");
        for (var i = 1; i < cells.Length; i++)
        {
            cells[i].Y.ShouldBeGreaterThan(cells[i - 1].Y, "the steps run top to bottom");
            cells[i].X.ShouldBe(cells[0].X, 0.5f, "in one column");
            cells[i].Height.ShouldBe(cells[0].Height, 0.5f, "of one height");
        }

        // Numbered from one and named by the operation's title; an operation this computer does not know is named by its id.
        e2e.TextsIn(cells[0]).ShouldBe(["1", "✓", "Add", "", "▶"], ignoreOrder: true);
        e2e.TextsIn(cells[1]).ShouldContain("2");
        e2e.TextsIn(cells[1]).ShouldContain("Add");
        e2e.TextsIn(cells[2]).ShouldContain("Served");
        e2e.TextsIn(cells[3]).ShouldContain("ghost");
        e2e.TextsIn(cells[4]).ShouldContain("Add");
        e2e.TextsIn(cells[4]).ShouldContain("5");
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
            e2e.StepWords(i).ShouldBe(expected[i].Words, $"step {i + 1} says its state in the runner's words");
            texts.ShouldContain(WorkbenchActions.TreeMarkFor(expected[i].State, expected[i].ChangedSinceLastRun), $"step {i + 1} wears the mark of its state");
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
        e2e.Texts().Where(t => t.Bounds.Y >= panelRect.Y && t.Bounds.Y < panelRect.Y + panelRect.Height && t.Bounds.X >= panelRect.X)
            .Where(t => t.Bounds.X < panelRect.X + panelRect.Width)
            .ShouldAllBe(t => t.Bounds.X + t.Bounds.Width <= panelRect.X + panelRect.Width + 0.5f, "no word of the panel runs past its edge");

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
        e2e.Click(e2e.Region("View:Original"));
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

        var topBar = new RectF32(0, 0, e2e.Renderer.Width, e2e.Region("View:Result").Y);
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
        e2e.StepWords(0).ShouldBe("kept", "step 1 is in the store");
        e2e.StepWords(1).ShouldBe("will run", "step 2 is not, and step 3 needs it");
        e2e.StepWords(2).ShouldBe("will run");
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
            e2e.StepWords(i).ShouldBe(answer[i].Words, $"step {i + 1} shows the runner's state after the run");
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
        e2e.ClickText("Log ⌄");
        Normalise(string.Join(' ', e2e.Texts().Select(static t => t.Text))).ShouldContain("at the gate", Case.Sensitive, "the run's line, in the log behind the disclosure");
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
        e2e.StepWords(1).ShouldBe(answer[1].Words, "the tree shows what the runner says after the cancel");
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
        e2e.State.ChosenView.ShouldBe(WorkbenchViewKind.Result, "a press on the choice chooses it");
        await e2e.PumpUntilAsync(() => SameFile(e2e.Controller.Document?.FilePath, steps[1].Output), "step 2's result to open", ct);
        e2e.Viewer.Split.IsOn.ShouldBeFalse();

        e2e.Click(e2e.Region("View:Comparison"));
        await e2e.PumpUntilAsync(() => e2e.Viewer.Split.IsOn && SameFile(e2e.Controller.Document?.FilePath, steps[1].Output), "the comparison", ct);
        e2e.Viewer.Split.Mode.ShouldBe(SplitCompare.BeforePixels, "the input's pixels were retained, so the split compares pixels");
        e2e.Viewer.Retentions.ShouldBe(1);
        e2e.Viewer.HasBeforeImageTextures.ShouldBeTrue();
        e2e.Viewer.Split.PixelsGeneration.ShouldBe(e2e.ViewerState.SourceGeneration, "the before pixels belong to what is on show");

        // Step 1's input is the original file; step 2's is step 1's stored result, which is what Input shows for step 2.
        e2e.Click(e2e.Region("View:Original"));
        await e2e.PumpUntilAsync(() => SameFile(e2e.Controller.Document?.FilePath, steps[0].Output) && !e2e.Viewer.Split.IsOn, "step 2's input", ct);
        e2e.Viewer.HasBeforeImageTextures.ShouldBeFalse("leaving the comparison frees the retained pixels");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AComparisonTheViewerDeclinedToKeepSaysSoInWordsAndShowsTheResultAlone(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (folder, _) = await ProjectAsync("declined", refusedStep: false);
        (await Runner().RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 1 }, ct)).Refusal.ShouldBeNull();
        var steps = Image(await Runner().StatusAsync(folder, "m", ct)).Steps;
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        e2e.Viewer.Declines = true;
        await e2e.OpenProjectAsync(folder, ct);
        await e2e.PumpUntilAsync(() => e2e.Controller.Document is not null, "the original", ct);
        await SelectAsync(e2e, 1, ct);

        e2e.Click(e2e.Region("View:Comparison"));
        await e2e.PumpUntilAsync(() => e2e.Actions.ComparisonDeclined && SameFile(e2e.Controller.Document?.FilePath, steps[1].Output), "the decline", ct);

        e2e.Viewer.Split.IsOn.ShouldBeFalse("no before pixels, so no split");
        Normalise(e2e.PanelText()).ShouldContain("not kept for want of memory", Case.Sensitive, "said in words");

        e2e.Click(e2e.Region("View:Original"));
        await e2e.PumpUntilAsync(() => !e2e.Actions.ComparisonDeclined, "the note to clear with another picture", ct);
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

        e2e.Texts().ShouldContain(t => t.Text == "Input");
        e2e.Texts().ShouldContain(t => t.Text == "Output", "the output is always chosen, and says so on the canvas when there is none");
        e2e.Texts().ShouldNotContain(t => t.Text == "Before and after", "step 2 is not made yet");
        e2e.Texts().ShouldContain(t => t.Text == "Add has no output yet", "the canvas says so");
        var panel = Normalise(e2e.PanelText());
        panel.ShouldContain("Step 2 has not been made yet.", Case.Sensitive, "said in words");
        panel.ShouldContain("Step 2 has no result to compare", Case.Sensitive);

        // The unknown operation is refused: nothing was made, and the words say so.
        var (refusedFolder, _) = await ProjectAsync("nofile2");
        await e2e.OpenProjectAsync(refusedFolder, ct);
        await SelectAsync(e2e, 3, ct);
        Normalise(e2e.PanelText()).ShouldContain("Step 4 made nothing: it is refused.", Case.Sensitive);
        e2e.Texts().ShouldNotContain(t => t.Text == "Before and after");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheImageDropdownListsEveryImageAndAPickShowsThatImageDirectly(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ImagesProjectAsync("images-", ct);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct, "a");
        e2e.MenuRows().ShouldBeEmpty("the menu is closed until the box is pressed");

        foreach (var (pick, expected) in new[] { (2, "c"), (0, "a"), (1, "b") })
        {
            e2e.Click(e2e.Region(WorkbenchScreen<RgbaImage>.ImageChoiceHit));
            var rows = e2e.MenuRows();
            rows.Length.ShouldBe(3, "every image of the project is a row");
            e2e.TextsIn(rows[pick]).ShouldContain(expected);

            e2e.Click(rows[pick]);
            await e2e.PumpUntilAsync(() => e2e.State.View?.ImageName == expected && !e2e.State.IsReading, $"image {expected} on show", ct);
            e2e.MenuRows().ShouldBeEmpty("a pick closes the menu");
            e2e.State.SelectedStep.ShouldBe(0, "a new image's first step is selected");
            e2e.TextsIn(e2e.Region(WorkbenchScreen<RgbaImage>.ImageChoiceHit)).ShouldContain(expected, "the box names the image on show");
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task WhileARunGoesNoOtherImageOrProjectIsReadAndTheViewStaysOnTheRunsImage(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("during-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", master, WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("gate")),
            WorkbenchFixtures.Picture("n", master, WorkbenchFixtures.Edit("add", Amount(2)))), ct);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct, "m");
        await SelectAsync(e2e, 1, ct);

        e2e.ClickText("Run to here (runs 1 first)");
        await e2e.PumpFramesUntilAsync(() => _gate.Started.Task.IsCompleted, "the step at its gate", ct);

        // The run writes the store, so no status is asked of it: the box does not open, and the actions refuse.
        e2e.Click(e2e.Region(WorkbenchScreen<RgbaImage>.ImageChoiceHit));
        e2e.MenuRows().ShouldBeEmpty("the image choice is dimmed while a run goes");
        e2e.Actions.ChooseImage("n");
        e2e.Actions.OpenProject(folder, "n").ShouldBeFalse();
        e2e.State.IsReading.ShouldBeFalse("nothing was asked of the runner");
        var choice = e2e.Regions().Where(static r => r.Result is HitResult.ButtonHit { Action: WorkbenchScreen<RgbaImage>.ImageChoiceHit })
            .Select(static r => r.Tooltip ?? string.Empty).ToArray();
        choice.ShouldContain(tooltip => tooltip.Contains(WorkbenchActions.ChoiceWaitsForTheRun, StringComparison.Ordinal),
            "the dimmed choice says why on hover");

        _gate.Release.SetResult();
        await SettleAsync(e2e, ct);

        e2e.State.View.ShouldNotBeNull().ImageName.ShouldBe("m", "the run's own image stays on show");
        var answer = (await Runner().StatusAsync(folder, "m", ct)).Images.Single(static i => i.Name == "m").Steps;
        e2e.TextsIn(e2e.StepCells()[1]).ShouldContain(answer[1].Words, "and is read afresh after the run");
        e2e.Click(e2e.Region(WorkbenchScreen<RgbaImage>.ImageChoiceHit));
        e2e.MenuRows().Length.ShouldBe(2, "once the run ended the choice is open again");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ARunsEndDoesNotReadOverAnImageWhoseReadIsOnItsWay(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ImagesProjectAsync("settled-", ct);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct, "a");

        // The person chose b, and before its answer was adopted a run of a (on show) started and ended: the run's end leaves the
        // read of b alone rather than superseding it with a read of a through the one status slot.
        e2e.Actions.OpenProject(folder, "b").ShouldBeTrue();
        e2e.Actions.Jobs.TryStart(e2e.State.View.ShouldNotBeNull().Folder, "a", through: null).ShouldBeTrue();
        await e2e.PumpUntilAsync(() => !e2e.Actions.Jobs.IsRunning && !e2e.State.IsReading && e2e.State.View?.ImageName == "b",
            "the chosen image on show", ct);
        e2e.State.View.ShouldNotBeNull().ImageName.ShouldBe("b");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task NoRunStartsWhileAnotherImagesReadIsInFlight(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ImagesProjectAsync("reading-", ct);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct, "a");
        await SelectAsync(e2e, 0, ct);

        // The view on show is still a's while b is read: a run asked for now would make a's step, not the image the person chose.
        e2e.Actions.OpenProject(folder, "b").ShouldBeTrue();
        e2e.State.IsReading.ShouldBeTrue("premise: the read of b is in flight");
        e2e.Actions.RunToHereOffer().CanRun.ShouldBeFalse();
        e2e.Actions.RunOnOffer().CanRun.ShouldBeFalse();
        e2e.Actions.RunToHere();
        e2e.Actions.RunOn();
        e2e.Actions.Jobs.IsRunning.ShouldBeFalse("no run started on the old image");

        await e2e.PumpUntilAsync(() => !e2e.State.IsReading && e2e.State.View?.ImageName == "b", "b on show", ct);
    }

    private async Task<string> ImagesProjectAsync(string name, System.Threading.CancellationToken ct)
    {
        var folder = _folders.Create(name).FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("a", master, WorkbenchFixtures.Edit("add", Amount(1))),
            WorkbenchFixtures.Picture("b", master, WorkbenchFixtures.Edit("add", Amount(2))),
            WorkbenchFixtures.Picture("c", master, WorkbenchFixtures.Edit("add", Amount(3)))), ct);
        return folder;
    }

    private static bool SameFile(string? a, string? b)
        => a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static RectF32 PanelRect(WorkbenchE2E e2e) => e2e.Host.Screen.PanelRect;

    private static string Compact(string text) => string.Concat(text.Where(static c => !char.IsWhiteSpace(c)));

    // Words painted one leaf at a time are joined by single spaces; a message's own line breaks and doubled spaces are not what is checked.
    private static string Normalise(string text) => string.Join(' ', text.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

    // A capture folder with one frame, for the stack step's input; the stack itself is never run in these tests.
    private string CaptureFolder()
    {
        var folder = Path.Combine(_folders.Create("capture-").FullName, "night-1");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "frame.fits"), new byte[2880]);
        return folder;
    }

    private static OperationCatalog StackCatalog() => new([new StackOperation()]);

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task NewProjectStandsBesideOpenProjectOnOneRowOfOneHeightAndOneMiddle(float dpi)
    {
        await using var e2e = WorkbenchE2E.Start(dpi, new WorkbenchRunner(StackCatalog()), StackCatalog());

        var (fresh, open) = (e2e.RegionOfLabel("New project"), e2e.RegionOfLabel("Open project"));
        fresh.Height.ShouldBe(open.Height, 0.5f, "one height");
        (fresh.Y + (fresh.Height / 2f)).ShouldBe(open.Y + (open.Height / 2f), 0.5f, "one middle");
        (fresh.X + fresh.Width).ShouldBeLessThanOrEqualTo(open.X + 0.5f, "beside it, not over it");
        e2e.Texts().Select(static t => t.Text).ShouldContain(t => t.Contains("make a new one from a capture folder", StringComparison.Ordinal),
            "the empty window says what the two buttons are for");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task PressingNewProjectWithOneFolderPickWritesTheLibRoutinesProjectInItsWorkdirAndShowsTheImportPage(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = CaptureFolder();
        var made = WorkbenchProjects.WorkdirOf(capture);
        var reference = Path.Combine(_folders.Create("reference-").FullName, "project");
        (await WorkbenchProjects.NewAsync(reference, capture, ct)).Refusal.ShouldBeNull();
        await using var e2e = WorkbenchE2E.Start(dpi, new WorkbenchRunner(StackCatalog()), StackCatalog());
        e2e.FakeFolderPicks(capture);

        e2e.Click(e2e.RegionOfLabel("New project"));
        await e2e.PumpUntilAsync(() => e2e.State.Stage == WorkbenchStage.Import && e2e.State.Import is { Detecting: false }, "the Import page", ct);

        File.ReadAllText(WorkbenchProjectFile.PathIn(made)).ShouldBe(File.ReadAllText(WorkbenchProjectFile.PathIn(reference)),
            "the window writes what the Lib routine writes");
        e2e.State.View.ShouldBeNull("the workbench opens after the workflow is chosen (WorkbenchImportScreenTests)");
        e2e.State.NewProjectRefusal.ShouldBeNull();
        e2e.Actions.Jobs.IsRunning.ShouldBeFalse("a press of New project starts no run");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ARefusedNewProjectSaysWhyInWordsAndOpensNothing(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = CaptureFolder();
        (await WorkbenchProjects.NewAsync(capture, ct)).Refusal.ShouldBeNull();
        await using var e2e = WorkbenchE2E.Start(dpi, new WorkbenchRunner(StackCatalog()), StackCatalog());
        e2e.FakeFolderPicks(capture);

        e2e.Click(e2e.RegionOfLabel("New project"));
        await e2e.PumpUntilAsync(() => e2e.State.NewProjectRefusal is not null, "the refusal", ct);

        e2e.State.View.ShouldBeNull("nothing was opened");
        var refusal = e2e.State.NewProjectRefusal!;
        refusal.ShouldContain("already holds");
        var shown = e2e.Texts().Single(static t => t.Text.StartsWith("No project was made", StringComparison.Ordinal));
        var (cx, cy) = (shown.Bounds.X + (shown.Bounds.Width / 2f), shown.Bounds.Y + (shown.Bounds.Height / 2f));
        e2e.Regions().Where(r => cx >= r.X && cx <= r.X + r.Width && cy >= r.Y && cy <= r.Y + r.Height && r.Tooltip is not null)
            .ShouldContain(r => r.Tooltip == refusal, "the whole refusal is on hover where the line is cut");

        // The next successful open clears it.
        var (folder, _) = await ProjectAsync();
        e2e.Actions.OpenProject(folder).ShouldBeTrue();
        await e2e.PumpUntilAsync(() => e2e.State.View is not null && !e2e.State.IsReading, "the project's status", ct);
        e2e.State.NewProjectRefusal.ShouldBeNull();
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheRefusalRowIsReservedSoItsAppearingMovesNothing(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = CaptureFolder();
        (await WorkbenchProjects.NewAsync(capture, ct)).Refusal.ShouldBeNull();
        await using var e2e = WorkbenchE2E.Start(dpi, new WorkbenchRunner(StackCatalog()), StackCatalog());
        e2e.Frame();
        var before = e2e.RegionOfLabel("New project");
        var hintTop = e2e.Texts().First(static t => t.Text.Contains("make a new one from a capture folder", StringComparison.Ordinal)).Bounds.Y;
        // The empty row carries no hover text: an empty one would still be drawn, as an empty bubble over the whole strip.
        e2e.Regions().ShouldNotContain(static r => r.Tooltip == string.Empty, "no painted region has an empty tooltip");
        e2e.FakeFolderPicks(capture);

        e2e.Click(before);
        await e2e.PumpUntilAsync(() => e2e.State.NewProjectRefusal is not null, "the refusal", ct);

        var after = e2e.RegionOfLabel("New project");
        (after.X, after.Y, after.Width, after.Height).ShouldBe((before.X, before.Y, before.Width, before.Height));
        e2e.Texts().First(static t => t.Text.Contains("make a new one from a capture folder", StringComparison.Ordinal)).Bounds.Y
            .ShouldBe(hintTop, "nothing below the bar moved");
        var refusal = e2e.State.NewProjectRefusal.ShouldNotBeNull();
        e2e.Regions().ShouldContain(r => r.Tooltip == refusal, "the refusal's whole text is on hover");
        e2e.Regions().ShouldNotContain(static r => r.Tooltip == string.Empty, "no painted region has an empty tooltip");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ANewProjectThatCannotBeWrittenSaysWhyInsteadOfDoingNothing(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = CaptureFolder();
        // A workdir that is an existing file: the routine throws when it makes the folder.
        File.WriteAllText(Path.Combine(capture, "workdir"), "x");
        await using var e2e = WorkbenchE2E.Start(dpi, new WorkbenchRunner(StackCatalog()), StackCatalog());
        e2e.FakeFolderPicks(capture);

        e2e.Click(e2e.RegionOfLabel("New project"));
        await e2e.PumpUntilAsync(() => e2e.State.NewProjectRefusal is not null, "the refusal", ct);

        e2e.State.View.ShouldBeNull("nothing was opened");
        e2e.State.NewProjectRefusal.ShouldNotBeNull().ShouldStartWith("No project was made: ");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task CancellingTheFolderDialogChangesNothing(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = WorkbenchE2E.Start(dpi, new WorkbenchRunner(StackCatalog()), StackCatalog());
        e2e.FakeFolderPicks((string?)null);

        e2e.Click(e2e.RegionOfLabel("New project"));
        await e2e.PumpUntilAsync(() => true, "the dialog to end", ct);

        e2e.State.View.ShouldBeNull();
        e2e.State.NewProjectRefusal.ShouldBeNull();
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task NewProjectIsDimmedWithItsReasonWhileARunGoes(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("dimmed-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", master, WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("gate"))), ct);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);
        await SelectAsync(e2e, 1, ct);
        e2e.ClickText("Run to here (runs 1 first)");
        await e2e.PumpFramesUntilAsync(() => _gate.Started.Task.IsCompleted, "the step at its gate", ct);

        e2e.RegionOfLabel("New project").Tooltip.ShouldNotBeNull().ShouldContain(WorkbenchActions.ChoiceWaitsForTheRun, Case.Sensitive,
            "the dimmed button says why on hover");
        e2e.FakeFolderPicks(null);
        var pendingBefore = e2e.Tracker.PendingCount;
        e2e.Actions.NewProject();
        // The signal is the tracker itself: RunGuarded registers its work before it returns and the entry stays until a frame
        // collects it, so a press the guard let through would show here at once, whether or not its dialog was reached yet.
        // No frame runs between the press and this read.
        e2e.Tracker.PendingDescriptions.ShouldNotContain("Make a workbench project from a capture folder", "the press started no work");
        e2e.Tracker.PendingCount.ShouldBe(pendingBefore, "the press started no work");
        _ = e2e.FileDialog.DidNotReceiveWithAnyArgs().OpenFolderAsync(default!, default);

        _gate.Release.SetResult();
        await SettleAsync(e2e, ct);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ANewProjectMadeWhileARunStartsIsNotOpenedAndSaysSoInWordsWithTheWholeTextOnHover(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("running-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", master, WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("gate"))), ct);
        var capture = CaptureFolder();
        var made = WorkbenchProjects.WorkdirOf(capture);
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
        await e2e.OpenProjectAsync(folder, ct);
        await SelectAsync(e2e, 1, ct);

        // The press passes the guard (no run yet) and waits in its dialog; a run starts meanwhile and holds at its gate.
        var picked = new TaskCompletionSource<FileDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        e2e.FileDialog.OpenFolderAsync(default!, default).ReturnsForAnyArgs(_ => picked.Task);
        e2e.Actions.NewProject();
        e2e.ClickText("Run to here (runs 1 first)");
        await e2e.PumpFramesUntilAsync(() => _gate.Started.Task.IsCompleted, "the step at its gate", ct);

        picked.SetResult(new FileDialogResult { Paths = [capture] });
        await e2e.PumpFramesUntilAsync(() => e2e.State.NewProjectRefusal is not null, "the refusal", ct);

        File.Exists(WorkbenchProjectFile.PathIn(made)).ShouldBeTrue("the project was made on disk");
        var refusal = e2e.State.NewProjectRefusal.ShouldNotBeNull();
        refusal.ShouldStartWith($"The project was made in {made}");
        refusal.ShouldContain(WorkbenchActions.ChoiceWaitsForTheRun, Case.Sensitive, "the one reason, read once");
        e2e.State.View!.Folder.ShouldBe(Path.GetFullPath(folder), "the project on show is still the one that was open");
        e2e.Frame();
        e2e.Texts().ShouldContain(t => t.Text.StartsWith("The project was made in", StringComparison.Ordinal), "a painted line carries it");
        e2e.Regions().ShouldContain(r => r.Tooltip == refusal, "the whole text is on hover");

        _gate.Release.SetResult();
        await SettleAsync(e2e, ct);
    }
}
