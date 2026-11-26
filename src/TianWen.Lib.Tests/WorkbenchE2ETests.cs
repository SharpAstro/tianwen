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

    private OperationCatalog Catalog() => new([_add, _served]);

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
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
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
        panel.ShouldContain(Normalise(StepCommandLine.Display(report.Steps[1].CommandLine)), Case.Insensitive,
            "the command line, wrapped into the panel and not cut");

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
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());
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

        e2e.Controller.Document!.FilePath.ShouldBe(master, Case.Insensitive);
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

    private static RectF32 PanelRect(WorkbenchE2E e2e)
    {
        var cells = e2e.StepCells();
        var top = cells.Max(static c => c.Y + c.Height);
        return new RectF32(0, top, e2e.Host.Screen.CanvasRect.X, e2e.Renderer.Height - top);
    }

    // Words painted one leaf at a time are joined by single spaces; a message's own line breaks and doubled spaces are not what is checked.
    private static string Normalise(string text) => string.Join(' ', text.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
}
