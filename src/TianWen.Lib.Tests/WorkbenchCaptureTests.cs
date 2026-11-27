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
using TianWen.Lib.Imaging;
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
        { 1280, 800, 1f, UiThemeState.Light, "refused" },
        { 2000, 1100, 1f, UiThemeState.Light, "running" },
        { 2000, 1100, 1f, UiThemeState.Light, "comparison" },
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

                // The capture is of the settled screen: the plan read has landed, so the run button says it cannot run, and why.
                // The reason is words in a flow (nothing to press), so it is read from the panel's text, not from one text.
                await e2e.PumpUntilAsync(
                    () => string.Join(' ', e2e.PanelText().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .Contains("Cannot run to here", StringComparison.Ordinal),
                    "the refused step's run line saying it cannot run", ct);
                e2e.Actions.RunToHereOffer().CanRun.ShouldBeFalse("a refused step offers no run");
            }
            else if (scenario is "result" or "comparison")
            {
                // Run to step 3 with the gate open, so steps 1 to 3 are kept (a tick), then look at step 2's result or comparison.
                _gradient.Release.TrySetResult();
                e2e.Click(cells[2]);
                await e2e.PumpUntilAsync(
                    () => e2e.Texts().Any(static t => t.Text.StartsWith("▶ Run", StringComparison.Ordinal)), "the run button for step 3", ct);
                e2e.ClickRun();
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
                    () => e2e.Texts().Any(static t => t.Text.StartsWith("▶ Run", StringComparison.Ordinal)), "the run button for step 3", ct);
                e2e.ClickRun();
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

    public static TheoryData<uint, uint, UiThemeState, string> PanelCases => new()
    {
        { 1280, 800, UiThemeState.Light, "settings" },
        { 1280, 800, UiThemeState.Dark, "settings" },
        { 2000, 1100, UiThemeState.Night, "settings" },
        { 1280, 800, UiThemeState.Light, "frames" },
        { 1280, 800, UiThemeState.Dark, "frames" },
        { 2000, 1100, UiThemeState.Night, "frames" },
    };

    // The panel beside the tree as controls (#2553 section 3): a step with a checkbox, a dropdown and fields of every kind, and a frame group's
    // file list with its kind dropdowns, with the capture's groups read. The PNGs are kept for a look; the checks are that nothing is cut
    // silently (the whole text is on hover) and that no word of the panel runs past its edge.
    [Theory(Timeout = 180_000)]
    [MemberData(nameof(PanelCases))]
    public async Task ThePanelsControlsAndFileListCutNothingSilently(uint width, uint height, UiThemeState theme, string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        GuiTheme.Apply(theme, desktopIsDark: true);
        try
        {
            var catalog = new OperationCatalog([new KnobOperation(), new StackOperation()]);
            string folder;
            if (scenario == "settings")
            {
                folder = _folders.Create("workbench-settings-capture-").FullName;
                var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
                await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master,
                    WorkbenchFixtures.Edit(KnobOperation.Id, new JsonObject { ["flag"] = true, ["mode"] = "Thorough", ["count"] = 12, ["extra"] = 0.25 }),
                    WorkbenchFixtures.Edit(KnobOperation.Id))), ct);
            }
            else
            {
                var capture = Path.Combine(_folders.Create("capture-").FullName, "10p_tempel_night_with_a_long_folder_name");
                for (var i = 1; i <= 6; i++)
                {
                    WorkbenchSettingsEditTests.WriteFrame(capture, $"10PTempel2_ASI2600MC_IDAS-LPS-D3_light_60s_gain1600_-5C_000{i}.fits", FrameType.Light, 60);
                }

                WorkbenchSettingsEditTests.WriteFrame(capture, "dark_60s_-5C_0001.fits", FrameType.Dark, 60);
                WorkbenchSettingsEditTests.WriteFrame(capture, "dark_60s_-5C_0002.fits", FrameType.Dark, 60);
                (await WorkbenchProjects.NewAsync(capture, ct)).Refusal.ShouldBeNull();
                folder = WorkbenchProjects.WorkdirOf(capture);
            }

            await using var e2e = WorkbenchE2E.Start(1f, new WorkbenchRunner(catalog), catalog, width, height);
            await e2e.OpenProjectAsync(folder, ct);
            if (scenario == "settings")
            {
                e2e.Click(e2e.StepCells()[0]);
                await e2e.PumpUntilAsync(() => e2e.State.Plan is { Index: 0 }, "step 1 selected", ct);
            }
            else
            {
                await e2e.PumpUntilAsync(() => e2e.State.Frames is { Groups.IsEmpty: false }, "the capture folder read", ct);
                e2e.Click(e2e.Region("Frames:Lights"));
                await e2e.PumpUntilAsync(() => e2e.State.Frames?.Groups.First(static g => g.Name == "Lights").Details.Length == 6, "the lights", ct);
                e2e.Frame();
                var first = e2e.State.Frames!.Groups.First(static g => g.Name == "Lights").Details[0];
                e2e.Click(e2e.Region("Frame:" + first.Place));
                await e2e.PumpUntilAsync(() => e2e.Controller.Document is not null, "the file shown", ct);
            }

            Keep(e2e, $"workbench-panel-{scenario}-{width}x{height}-{theme}");
            NothingIsCutAtThePanelsEdge(e2e);
            var regions = e2e.Regions();
            foreach (var (full, shown, bounds) in e2e.Host.Screen.CutTexts())
            {
                var (cx, cy) = (bounds.X + (bounds.Width / 2f), bounds.Y + (bounds.Height / 2f));
                if (cx < e2e.Host.Screen.CanvasRect.X)
                {
                    regions.Any(r => cx >= r.X && cx <= r.X + r.Width && cy >= r.Y && cy <= r.Y + r.Height && r.Tooltip is not null
                        && r.Tooltip.Contains(full, StringComparison.Ordinal)).ShouldBeTrue($"'{full}' is shown as '{shown}' and carries its whole text on hover");
                }
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

            // A tree row below the list's own viewport (the legend and the status strip lie there) is reached by scrolling the list: its
            // hover region is clipped with it, so it is judged when it is scrolled into view.
            if (cx < canvas.X && cy >= e2e.Renderer.Height - (92f * e2e.Host.Screen.DpiScale))
            {
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
        var canvas = e2e.Host.Screen.CanvasRect;
        var edge = canvas.X;
        edge.ShouldBeGreaterThan(0f);
        foreach (var t in e2e.Texts().Where(t => t.Bounds.Y >= canvas.Y && t.Bounds.Y + t.Bounds.Height <= canvas.Y + canvas.Height && t.Bounds.X < edge))
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

        // The image choice's menu: every image a row, inside the window, over the canvas, each name whole or on hover.
        e2e.Click(e2e.Region(WorkbenchScreen<RgbaImage>.ImageChoiceHit));
        var rows = e2e.MenuRows();
        rows.Length.ShouldBe(6, "every image is a row of the menu");
        foreach (var row in rows)
        {
            row.X.ShouldBeGreaterThanOrEqualTo(0f);
            (row.X + row.Width).ShouldBeLessThanOrEqualTo(width + 0.5f, "a menu row lies inside the window");
            (row.Y + row.Height).ShouldBeLessThanOrEqualTo(height + 0.5f, "a menu row lies inside the window");
        }

        Keep(e2e, $"workbench-image-menu-{width}x{height}-Dark-dpi{dpi:0.0}");
    }

    public static TheoryData<uint, uint, float, UiThemeState, string> NewProjectCases => new()
    {
        { 1280, 800, 1f, UiThemeState.Dark, "empty" },
        { 1280, 800, 1f, UiThemeState.Night, "empty" },
        { 2000, 1100, 1f, UiThemeState.Dark, "empty" },
        { 2000, 1100, 1f, UiThemeState.Night, "empty" },
        { 2000, 1100, 1.5f, UiThemeState.Dark, "empty" },
        { 1280, 800, 1f, UiThemeState.Dark, "new" },
        { 1280, 800, 1f, UiThemeState.Night, "new" },
        { 2000, 1100, 1f, UiThemeState.Dark, "new" },
        { 2000, 1100, 1f, UiThemeState.Night, "new" },
        { 2000, 1100, 1.5f, UiThemeState.Dark, "new" },
        { 1280, 800, 1f, UiThemeState.Dark, "long-path" },
        { 1280, 800, 1f, UiThemeState.Night, "long-path" },
        { 2000, 1100, 1f, UiThemeState.Dark, "long-path" },
        { 2000, 1100, 1f, UiThemeState.Night, "long-path" },
        { 1280, 800, 1.5f, UiThemeState.Dark, "long-path" },
        { 1280, 800, 1f, UiThemeState.Dark, "refused" },
        { 2000, 1100, 1f, UiThemeState.Night, "refused" },
    };

    // No project open, a new capture project before its stack, a capture folder with a long realistic path, and a refused New project:
    // the New project and Open project buttons stay on one height and one middle inside the window, and no text is cut silently.
    [Theory(Timeout = 180_000)]
    [MemberData(nameof(NewProjectCases))]
    public async Task TheNewProjectScreensCutNothingSilentlyMeetContrastAndKeepOneHeightAndOneMiddle(
        uint width, uint height, float dpi, UiThemeState theme, string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = Path.Combine(_folders.Create("capture-").FullName,
            scenario == "long-path" ? "2026-11-14 M31 Andromeda Galaxy HaLRGB mosaic panel 3 of 4 (SkyWatcher Esprit 100ED, ASI2600MM Pro)" : "night-1");
        Directory.CreateDirectory(capture);
        File.WriteAllBytes(Path.Combine(capture, "frame.fits"), new byte[2880]);
        var projects = _folders.Create(scenario == "long-path" ? "astrophotography-projects-for-the-winter-season-" : "projects-").FullName;
        var project = Path.Combine(projects, scenario == "long-path" ? "andromeda-galaxy-narrowband-and-broadband-reprocessing-attempt-two" : "project");
        var catalog = new OperationCatalog([new StackOperation()]);
        GuiTheme.Apply(theme, desktopIsDark: true);
        try
        {
            await using var e2e = WorkbenchE2E.Start(dpi, new WorkbenchRunner(catalog), catalog, width, height);
            if (scenario == "refused")
            {
                (await WorkbenchProjects.NewAsync(capture, ct)).Refusal.ShouldBeNull();
                e2e.FakeFolderPicks(capture);
                e2e.Click(e2e.RegionOfLabel("New project"));
                await e2e.PumpUntilAsync(() => e2e.State.NewProjectRefusal is not null, "the refusal", ct);
            }
            else if (scenario != "empty")
            {
                e2e.FakeFolderPicks(capture);
                e2e.Click(e2e.RegionOfLabel("New project"));
                // New project now stops at the Import page; the capture of the workbench with a new project goes on through the
                // Workflow page with the empty workflow (the pages themselves are WorkbenchImportScreenTests').
                await e2e.PumpUntilAsync(() => e2e.State.Stage == WorkbenchStage.Import && e2e.State.Import is { Detecting: false }, "the Import page", ct);
                e2e.Actions.UseWorkflow(BuiltInWorkflows.Empty);
                await e2e.PumpUntilAsync(() => e2e.State.Stage == WorkbenchStage.Workbench && e2e.State.View is not null && !e2e.State.IsReading, "the new project", ct);
            }

            Keep(e2e, $"workbench-new-{scenario}-{width}x{height}-{theme}-dpi{dpi:0.0}");

            var (fresh, open) = (e2e.RegionOfLabel("New project"), e2e.RegionOfLabel("Open project"));
            fresh.Height.ShouldBe(open.Height, 1f, "one height");
            (fresh.Y + (fresh.Height / 2f)).ShouldBe(open.Y + (open.Height / 2f), 1f, "one middle");
            foreach (var r in new[] { fresh, open })
            {
                r.X.ShouldBeGreaterThanOrEqualTo(0f);
                (r.X + r.Width).ShouldBeLessThanOrEqualTo(width + 0.5f, "a top bar control lies inside the window");
            }

            // Every text the screen drew shorter than it is carries its whole text on hover (the viewer's own strips are its own).
            var regions = e2e.Regions();
            var canvas = e2e.Host.Screen.CanvasRect;
            foreach (var (full, shown, bounds) in e2e.Host.Screen.CutTexts())
            {
                var (cx, cy) = (bounds.X + (bounds.Width / 2f), bounds.Y + (bounds.Height / 2f));
                if (cx >= canvas.X && cx <= canvas.X + canvas.Width && cy >= canvas.Y && cy <= canvas.Y + canvas.Height)
                {
                    continue;
                }

                regions.Where(r => cx >= r.X && cx <= r.X + r.Width && cy >= r.Y && cy <= r.Y + r.Height && r.Tooltip is not null)
                    .ShouldContain(r => r.Tooltip!.Contains(full, StringComparison.Ordinal), $"'{full}' is shown as '{shown}' and carries its whole text on hover");
            }

            // Contrast of each top bar and message text against the pixel beside it, which is the fill it is drawn on.
            var floor = theme == UiThemeState.Night ? 3.0 : 4.5;
            foreach (var (text, bounds, ink) in e2e.TextInks().Where(t => t.Bounds.Y < e2e.Renderer.Height / 2f && t.Bounds.X < canvas.X + canvas.Width && t.Bounds.Width > 0))
            {
                if (Inside(canvas, bounds))
                {
                    continue; // the viewer's own marks sit on the picture, not on the chrome's fill
                }

                if (regions.Any(r => r.Result is HitResult.ButtonHit && Inside(new RectF32(r.X, r.Y, r.Width, r.Height), bounds)))
                {
                    continue; // a button's own label is the theme's pair for its fill, checked by the theme's tests
                }

                // The pixel just left of the text, never one of its glyphs.
                var fill = PixelAt(e2e, Math.Max(0, (int)bounds.X - 3), (int)(bounds.Y + (bounds.Height / 2f)));
                Contrast(ink, fill).ShouldBeGreaterThanOrEqualTo(floor, $"'{text}' against the fill it is drawn on");
            }
        }
        finally
        {
            GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        }
    }

    public static TheoryData<uint, uint, float, UiThemeState, string> MastersCases => new()
    {
        { 1280, 800, 1f, UiThemeState.Dark, "one" },
        { 1280, 800, 1f, UiThemeState.Night, "one" },
        { 2000, 1100, 1f, UiThemeState.Dark, "one" },
        { 2000, 1100, 1f, UiThemeState.Night, "one" },
        { 1280, 800, 1f, UiThemeState.Dark, "several" },
        { 1280, 800, 1f, UiThemeState.Night, "several" },
        { 2000, 1100, 1f, UiThemeState.Dark, "several" },
        { 2000, 1100, 1f, UiThemeState.Night, "several" },
        { 1280, 800, 1.5f, UiThemeState.Dark, "several" },
        { 2000, 1100, 1.5f, UiThemeState.Dark, "one" },
    };

    // After its stack: the stack step's masters, one (a short name) or several with long realistic group names (target, filter and
    // exposure). Each name carries its whole text on hover where it is cut, every button lies inside the panel on one height, and the
    // names meet contrast on the panel's fill.
    [Theory(Timeout = 180_000)]
    [MemberData(nameof(MastersCases))]
    public async Task TheMastersListCutsNothingSilentlyMeetsContrastAndKeepsOneHeightAndOneMiddle(
        uint width, uint height, float dpi, UiThemeState theme, string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        string[] groups = scenario == "one"
            ? ["m31_L"]
            :
            [
                "M31 Andromeda Galaxy panel 3 of 4_Hydrogen alpha 7nm_300s",
                "M31 Andromeda Galaxy panel 3 of 4_Luminance_180s",
                "M31 Andromeda Galaxy panel 3 of 4_Oxygen III 6.5nm_300s",
                "M31_R_120s",
                "NGC 7000 North America Nebula_Sulphur II 6.5nm_600s",
            ];
        var capture = Path.Combine(_folders.Create("capture-").FullName, "2026-11-14 M31 Andromeda Galaxy mosaic");
        Directory.CreateDirectory(capture);
        foreach (var group in groups)
        {
            File.WriteAllBytes(Path.Combine(capture, group + ".fits"), new byte[2880]);
        }

        var catalog = new OperationCatalog([new FakeStackOperation()]);
        var runner = new WorkbenchRunner(catalog);
        var project = Path.Combine(_folders.Create("projects-").FullName, "andromeda");
        (await WorkbenchProjects.NewAsync(project, capture, ct)).Refusal.ShouldBeNull();
        (await runner.RunAsync(project, cancellationToken: ct)).Succeeded.ShouldBeTrue();
        GuiTheme.Apply(theme, desktopIsDark: true);
        try
        {
            await using var e2e = WorkbenchE2E.Start(dpi, runner, catalog, width, height);
            await e2e.OpenProjectAsync(project, ct);
            e2e.Actions.Panel().ShouldNotBeNull().Masters.Length.ShouldBe(groups.Length);

            Keep(e2e, $"workbench-masters-{scenario}-{width}x{height}-{theme}-dpi{dpi:0.0}");

            var canvas = e2e.Host.Screen.CanvasRect;
            var buttons = e2e.Texts().Where(static t => t.Text == "Process this master").OrderBy(static t => t.Bounds.Y).Select(t =>
            {
                var (cx, cy) = (t.Bounds.X + (t.Bounds.Width / 2f), t.Bounds.Y + (t.Bounds.Height / 2f));
                return e2e.Regions().Single(r => r.Result is HitResult.ButtonHit && cx >= r.X && cx <= r.X + r.Width && cy >= r.Y && cy <= r.Y + r.Height);
            }).ToArray();
            buttons.Length.ShouldBe(groups.Length, "a button for each master");
            foreach (var r in buttons)
            {
                (r.X + r.Width).ShouldBeLessThanOrEqualTo(canvas.X + 0.5f, "a button lies inside the panel");
                r.Height.ShouldBe(buttons[0].Height, 1f, "one height");
            }

            // Where a name is cut, its whole text is on hover.
            var regions = e2e.Regions();
            foreach (var (full, shown, bounds) in e2e.Host.Screen.CutTexts())
            {
                var (cx, cy) = (bounds.X + (bounds.Width / 2f), bounds.Y + (bounds.Height / 2f));
                if (cx >= canvas.X)
                {
                    continue;
                }

                regions.Where(r => cx >= r.X && cx <= r.X + r.Width && cy >= r.Y && cy <= r.Y + r.Height && r.Tooltip is not null)
                    .ShouldContain(r => r.Tooltip!.Contains(full, StringComparison.Ordinal), $"'{full}' is shown as '{shown}' and carries its whole text on hover");
            }

            // Each master's name against the panel's fill beside it, and on one middle with its button.
            var floor = theme == UiThemeState.Night ? 3.0 : 4.5;
            var names = e2e.TextInks().Where(t => groups.Any(g => t.Text.StartsWith(g[..Math.Min(g.Length, 3)], StringComparison.Ordinal)) && t.Bounds.X < canvas.X
                && t.Bounds.X >= e2e.Host.Screen.PanelRect.X).OrderBy(static t => t.Bounds.Y).ToArray();
            names.Length.ShouldBeGreaterThanOrEqualTo(groups.Length);
            foreach (var (text, bounds, ink) in names)
            {
                var fill = PixelAt(e2e, Math.Max(0, (int)bounds.X - 3), (int)(bounds.Y + (bounds.Height / 2f)));
                Contrast(ink, fill).ShouldBeGreaterThanOrEqualTo(floor, $"'{text}' against the fill it is drawn on");
            }

            NothingIsCutAtThePanelsEdge(e2e);
        }
        finally
        {
            GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
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
            foreach (var (text, bounds, ink) in e2e.TextInks().Where(t => Inside(cell, t.Bounds) && t.Text.Length > 0))
            {
                // The fill under the text itself: the row's, or the ▶'s own when the pointer or the selection makes it the accent.
                var fill = PixelAt(e2e, (int)(bounds.X + (bounds.Width / 2f)), (int)bounds.Y + 1);
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
            cell.X.ShouldBe(steps[0].X, 0.5f, "the tree's rows are one column");
            cell.Height.ShouldBeLessThanOrEqualTo(steps[0].Height + 0.5f, "of one height (the last may be cut by the list's edge)");
        }

        var dpi = e2e.Host.Screen.DpiScale;
        var buttons = e2e.Regions().Where(static r => r.Result is HitResult.ButtonHit).ToArray();
        var topBar = buttons.Where(r => r.Y + (r.Height / 2f) < 44f * dpi).ToArray();
        // The toolbar's controls (Run, Stop, the picture choice, Fit, 1:1, the stretch) share one height and one middle too.
        var toolbar = buttons.Where(r => !(r.Result is HitResult.ButtonHit h && (h.Action.StartsWith("Play:", StringComparison.Ordinal) || h.Action.StartsWith("Step:", StringComparison.Ordinal)))
            && r.Y + (r.Height / 2f) > 44f * dpi && r.Y + (r.Height / 2f) < 88f * dpi).ToArray();
        toolbar.Length.ShouldBeGreaterThan(3, "the toolbar holds its controls");
        foreach (var r in toolbar)
        {
            (r.Y + (r.Height / 2f)).ShouldBe(toolbar[0].Y + (toolbar[0].Height / 2f), 1f, "one middle in the toolbar");
            if (!(r.Result is HitResult.ButtonHit view && view.Action.StartsWith("View:", StringComparison.Ordinal)))
            {
                r.Height.ShouldBe(toolbar[0].Height, 1f, "one height in the toolbar (the picture choice's segments are the engine's own height, checked among themselves below)");
            }
        }

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
            var row = e2e.Texts().Where(t => Inside(cell, t.Bounds) && t.Text.Length > 0).ToArray();
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
