using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Imaging.Workbench;
using TianWen.UI.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>tianwen-edit</c>'s guided screen looked at, not only asserted (#1642, W2): a long edit list, step titles that need an
/// ellipsis, a refused step with its reason selected, a long command line and a run's lines in the job panel, at 1280 x 800 and
/// 2000 x 1100 in the standard (Dark) and Night palettes and once at DPI 1.5. Each render is kept as a PNG under
/// <c>TIANWEN_KEEP_TEST_OUTPUT=1</c>, beside the test binary, to be looked at against the UI checklist.
/// </summary>
/// <remarks>
/// In the theme collection: it applies a palette, which is process-wide state (<c>ThemeCollectionSourceTests</c>).
/// </remarks>
[Collection(ThemeCollection.Name)]
public sealed class WorkbenchCaptureTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly AddOperation _add = new("add");
    private readonly ServedOperation _served = new();
    private readonly LongOperation _gradient = new("gradient",
        "Estimate and remove the background gradient and the vignetting from every channel of the frame", gated: true);
    private readonly LongOperation _fit = new("fit", "Fit the red and the blue channel to the median of the green channel");
    private readonly LongOperation _deconvolve = new("deconvolve", "Deconvolve the stars with the point spread function measured on this telescope");

    public void Dispose() => _folders.Dispose();

    private OperationCatalog Catalog() => new([_add, _served, _gradient, _fit, _deconvolve]);

    private WorkbenchRunner Runner() => new(Catalog());

    private static JsonObject Amount(float amount) => new() { ["amount"] = amount };

    // Fourteen steps: two adds, the gated gradient (index 2), a fit, a served step, an add, a deconvolution, an add, an operation
    // this computer does not know (index 8, refused) and five more behind it (blocked).
    private async Task<string> ProjectAsync(CancellationToken ct)
    {
        var folder = _folders.Create("workbench-capture-with-a-realistically-long-folder-name-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        ProjectEdit[] edits =
        [
            WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)), WorkbenchFixtures.Edit("gradient"),
            WorkbenchFixtures.Edit("fit"), WorkbenchFixtures.Edit("served"), WorkbenchFixtures.Edit("add", Amount(3)),
            WorkbenchFixtures.Edit("deconvolve"), WorkbenchFixtures.Edit("add", Amount(4)),
            WorkbenchFixtures.Edit("narrowband-combination-from-a-newer-version"),
            WorkbenchFixtures.Edit("add", Amount(5)), WorkbenchFixtures.Edit("add", Amount(6)), WorkbenchFixtures.Edit("fit"),
            WorkbenchFixtures.Edit("add", Amount(7)), WorkbenchFixtures.Edit("deconvolve"),
        ];
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master, edits)), ct);
        return folder;
    }

    public static TheoryData<uint, uint, float, UiThemeState, string> Cases => new()
    {
        { 1280, 800, 1f, UiThemeState.Dark, "refused" },
        { 1280, 800, 1f, UiThemeState.Night, "refused" },
        { 2000, 1100, 1f, UiThemeState.Dark, "refused" },
        { 2000, 1100, 1f, UiThemeState.Night, "refused" },
        { 2000, 1100, 1.5f, UiThemeState.Dark, "refused" },
        { 1280, 800, 1f, UiThemeState.Dark, "running" },
        { 1280, 800, 1f, UiThemeState.Night, "running" },
        { 2000, 1100, 1f, UiThemeState.Dark, "running" },
        { 2000, 1100, 1f, UiThemeState.Night, "running" },
        { 2000, 1100, 1.5f, UiThemeState.Dark, "running" },
        { 1280, 800, 1f, UiThemeState.Dark, "result" },
        { 1280, 800, 1f, UiThemeState.Night, "comparison" },
        { 2000, 1100, 1f, UiThemeState.Dark, "comparison" },
        { 2000, 1100, 1f, UiThemeState.Night, "result" },
    };

    [Theory(Timeout = 180_000)]
    [MemberData(nameof(Cases))]
    public async Task TheGuidedScreenCutsNothingSilentlyMeetsContrastAndKeepsOneHeightAndOneMiddle(
        uint width, uint height, float dpi, UiThemeState theme, string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ProjectAsync(ct);
        GuiTheme.Apply(theme, desktopIsDark: true);
        try
        {
            await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog(), width, height);
            await e2e.OpenProjectAsync(folder, ct);
            var cells = e2e.StepCells();
            cells.Length.ShouldBe(14, "the whole edit list is on the bar");

            if (scenario == "refused")
            {
                e2e.Click(cells[8]);
                var steps = e2e.State.View!.Image!.Steps;
                steps[8].State.ShouldBe(StepState.Refused);
                steps[8].Message.ShouldNotBeNullOrWhiteSpace();
            }
            else if (scenario is "result" or "comparison")
            {
                // Run to step 3 with the gate open, so steps 1 to 3 are kept (a tick), then look at step 2's result or comparison.
                _gradient.Release.TrySetResult();
                e2e.Click(cells[2]);
                await e2e.PumpUntilAsync(
                    () => e2e.Texts().Any(static t => t.Text.StartsWith("Run to here", StringComparison.Ordinal)), "the run button for step 3", ct);
                e2e.Click(e2e.Texts().Single(static t => t.Text.StartsWith("Run to here", StringComparison.Ordinal)).Bounds);
                await e2e.PumpFramesUntilAsync(
                    () => !e2e.Actions.Jobs.IsRunning && e2e.Actions.Jobs.State.Outcome is WorkbenchJobOutcome.Finished
                        && e2e.State.View!.Image!.Steps[2].State is StepState.Kept, "step 3 kept", ct);
                e2e.Click(cells[1]);
                await e2e.PumpFramesUntilAsync(() => e2e.Actions.ViewOffer() is { ResultOffered: true, ComparisonOffered: true },
                    "step 2's result and comparison offered", ct);
                e2e.Click(e2e.Region(scenario == "result" ? "View:Result" : "View:Comparison"));
                e2e.Actions.EffectiveView().ShouldBe(scenario == "result" ? WorkbenchViewKind.Result : WorkbenchViewKind.Comparison);
            }
            else
            {
                e2e.Click(cells[2]);
                await e2e.PumpUntilAsync(
                    () => e2e.Texts().Any(static t => t.Text.StartsWith("Run to here", StringComparison.Ordinal)), "the run button for step 3", ct);
                e2e.Click(e2e.Texts().Single(static t => t.Text.StartsWith("Run to here", StringComparison.Ordinal)).Bounds);
                await e2e.PumpFramesUntilAsync(
                    () => _gradient.Started.Task.IsCompleted && e2e.Actions.Jobs.State.Fraction is not null, "the gradient at its gate", ct);
            }

            Keep(e2e, $"workbench-{scenario}-{width}x{height}-{theme}-dpi{dpi:0.0}");
            try
            {
                NothingIsCutSilently(e2e);
                StepWordsAndMarksMeetContrast(e2e, theme);
                ControlsShareOneHeightAndOneMiddle(e2e);
                NothingIsCutAtThePanelsEdge(e2e);
            }
            finally
            {
                _gradient.Release.TrySetResult();
            }
        }
        finally
        {
            GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        }
    }

    private static void NothingIsCutSilently(WorkbenchE2E e2e)
    {
        var regions = e2e.Regions();
        var cut = e2e.Host.Screen.CutTexts();
        cut.IsEmpty.ShouldBeFalse("premise: the long titles really do need an ellipsis, or this test proves nothing");
        var canvas = e2e.Host.Screen.CanvasRect;
        foreach (var (full, shown, bounds) in cut)
        {
            var (cx, cy) = (bounds.X + (bounds.Width / 2f), bounds.Y + (bounds.Height / 2f));
            if (cx >= canvas.X && cx <= canvas.X + canvas.Width && cy >= canvas.Y && cy <= canvas.Y + canvas.Height)
            {
                // The viewer's own toolbar and strips paint inside the canvas and are the viewer's to keep (ViewerE2E); this screen owns the rest.
                continue;
            }

            regions.Where(r => cx >= r.X && cx <= r.X + r.Width && cy >= r.Y && cy <= r.Y + r.Height && r.Tooltip is not null)
                .ShouldContain(r => r.Tooltip!.Contains(full, StringComparison.Ordinal),
                    $"'{full}' is shown as '{shown}' and carries its whole text on hover");
        }
    }

    // A text that fits no ellipsis can save is one longer than its box, drawn as arranged past the panel's edge: none of the panel's
    // painted texts may end beyond where the canvas begins, so a long unbroken word is broken, never cut at the edge.
    private static void NothingIsCutAtThePanelsEdge(WorkbenchE2E e2e)
    {
        var steps = e2e.StepCells();
        var below = steps[0].Y + steps[0].Height;
        var edge = e2e.Host.Screen.CanvasRect.X;
        edge.ShouldBeGreaterThan(0f);
        foreach (var t in e2e.Texts().Where(t => t.Bounds.Y >= below && t.Bounds.X < edge))
        {
            (t.Bounds.X + t.Bounds.Width).ShouldBeLessThanOrEqualTo(edge + 0.5f, $"'{t.Text}' ends inside the panel");
        }
    }

    [Theory(Timeout = 180_000)]
    [InlineData(1280u, 800u, 1f)]
    [InlineData(1280u, 800u, 1.5f)]
    public async Task SixImagesWithLongNamesKeepEveryTopBarControlInsideTheWindow(uint width, uint height, float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("workbench-six-images-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        var pictures = Enumerable.Range(1, 6)
            .Select(i => WorkbenchFixtures.Picture($"narrowband-night-{i}-of-the-long-run", master, WorkbenchFixtures.Edit("add", Amount(i))))
            .ToArray();
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(pictures), ct);

        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog(), width, height);
        await e2e.OpenProjectAsync(folder, ct);

        var steps = e2e.StepCells();
        steps.Length.ShouldBe(1);
        var top = e2e.Regions().Where(r => r.Result is HitResult.ButtonHit && r.Y + r.Height <= steps[0].Y + 0.5f).ToArray();
        top.Length.ShouldBeGreaterThan(1, "the image choice and Open project are in the top bar");
        foreach (var r in top)
        {
            r.X.ShouldBeGreaterThanOrEqualTo(0f);
            (r.X + r.Width).ShouldBeLessThanOrEqualTo(width + 0.5f, "a top bar control lies inside the window");
        }
    }

    // Each step's words and marks against the fill the cell is drawn on, read off the painted pixels beside the text.
    private static void StepWordsAndMarksMeetContrast(WorkbenchE2E e2e, UiThemeState theme)
    {
        var cells = e2e.StepCells();
        var worst = double.MaxValue;
        string? worstText = null;
        foreach (var cell in cells)
        {
            var fill = PixelAt(e2e, (int)cell.X + 2, (int)cell.Y + 2);
            foreach (var (text, _, ink) in e2e.TextInks().Where(t => Inside(cell, t.Bounds)))
            {
                var ratio = Contrast(ink, fill);
                if (ratio < worst)
                {
                    (worst, worstText) = (ratio, text);
                }
            }
        }

        // Night is held to the floor its own palette guarantees (GuiThemeTests): no colour spends a blue channel.
        var floor = theme == UiThemeState.Night ? 3.0 : 4.5;
        worst.ShouldBeGreaterThanOrEqualTo(floor, $"the weakest step text is '{worstText}'");
    }

    private static void ControlsShareOneHeightAndOneMiddle(WorkbenchE2E e2e)
    {
        var steps = e2e.StepCells();
        foreach (var cell in steps)
        {
            cell.Y.ShouldBe(steps[0].Y, 0.5f);
            cell.Height.ShouldBe(steps[0].Height, 0.5f);
        }

        var buttons = e2e.Regions().Where(static r => r.Result is HitResult.ButtonHit).ToArray();
        var topBar = buttons.Where(r => r.Y < steps[0].Y).ToArray();
        topBar.Length.ShouldBeGreaterThan(0, "the top bar holds a control");
        foreach (var r in topBar)
        {
            (r.Y + (r.Height / 2f)).ShouldBe(topBar[0].Y + (topBar[0].Height / 2f), 1f, "one middle in the top bar");
            r.Height.ShouldBe(topBar[0].Height, 1f, "one height in the top bar");
        }

        var views = buttons.Where(static r => r.Result is HitResult.ButtonHit hit && hit.Action.StartsWith("View:", StringComparison.Ordinal)).ToArray();
        foreach (var r in views)
        {
            (r.Y + (r.Height / 2f)).ShouldBe(views[0].Y + (views[0].Height / 2f), 1f, "one middle in the view choice");
            r.Height.ShouldBe(views[0].Height, 1f, "one height in the view choice");
        }

        // Within a cell the mark and its words stand on one middle.
        foreach (var cell in steps)
        {
            var row = e2e.Texts().Where(t => Inside(cell, t.Bounds) && t.Bounds.Y > cell.Y + (cell.Height / 2f) - 1f).ToArray();
            foreach (var t in row)
            {
                (t.Bounds.Y + (t.Bounds.Height / 2f)).ShouldBe(row[0].Bounds.Y + (row[0].Bounds.Height / 2f), 1.5f, "the state row has one middle");
            }
        }
    }

    private static bool Inside(RectF32 outer, RectF32 inner)
        => inner.X >= outer.X - 0.5f && inner.Y >= outer.Y - 0.5f
            && inner.X + inner.Width <= outer.X + outer.Width + 0.5f && inner.Y + inner.Height <= outer.Y + outer.Height + 0.5f;

    private static RGBAColor32 PixelAt(WorkbenchE2E e2e, int x, int y)
    {
        var surface = e2e.Renderer.Surface;
        var i = ((y * (int)surface.Width) + x) * 4;
        return new RGBAColor32(surface.Pixels[i], surface.Pixels[i + 1], surface.Pixels[i + 2], 255);
    }

    private static double Linear(byte c)
    {
        var v = c / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(RGBAColor32 c) => (0.2126 * Linear(c.Red)) + (0.7152 * Linear(c.Green)) + (0.0722 * Linear(c.Blue));

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    // Kept output only (TIANWEN_KEEP_TEST_OUTPUT=1): the render beside the test binary, to be looked at.
    private static void Keep(WorkbenchE2E e2e, string name)
    {
        if (Environment.GetEnvironmentVariable("TIANWEN_KEEP_TEST_OUTPUT") == "1")
        {
            var surface = e2e.Renderer.Surface;
            File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, name + ".png"),
                Helpers.DisplayImageWriter.EncodePng(surface.Pixels, (int)surface.Width, (int)surface.Height));
        }
    }
}

/// <summary>An operation with a title long enough to need an ellipsis, a long command line, and (gated) a run that says several
/// lines and stands at half its work until the test lets it through.</summary>
internal sealed class LongOperation(string id, string title, bool gated = false) : Operation<AddSettings>
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override OperationDescription Description { get; } = new(
        id, Version: 1, Title: title, Verb: "none", OperationOutputKind.Stored, ExtraInputs: [],
        Settings: [new OperationSetting("amount", "What is added to every pixel, from a small number that keeps the faint nebulosity to a large one.")])
    {
        Summary = "Copies the image and adds a number to it, standing in for an operation whose title and command line are long.",
    };

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output)
        => [Description.Id, .. inputs, "-o", output, "--amount", StepCommandLine.Number(settings.Amount), "--protect-star-cores", "--model-directory",
            "C:/Users/someone/AppData/Local/TianWen/Models/a-very-long-model-folder-name/weights-v12.onnx"];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
        => RunAsync(settings, inputs, outputPath, say, progress: null, cancellationToken);

    public override async ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath,
        Action<string>? say, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (gated)
        {
            say?.Invoke("measuring the background on a 16 x 16 grid of boxes, 41 of them rejected for stars");
            say?.Invoke("fitting a degree 3 polynomial to the 215 boxes that are left, and then the vignetting from the flat field model");
            say?.Invoke("subtracting the surface from the red channel");
            progress?.Report(0.4);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }

        if (!inputs[0].TryRead(out var image, out var wcs, out var refusal))
        {
            return OperationOutcome.BadInput(refusal);
        }

        OperationOutput.Write(image, outputPath, wcs);
        return OperationOutcome.Done;
    }
}
