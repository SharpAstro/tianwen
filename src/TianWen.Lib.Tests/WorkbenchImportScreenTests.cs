using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.Workbench;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The two pages of New project in <c>tianwen-edit</c> (#2553, sections 1 and 2), driven through <see cref="WorkbenchE2E"/> at DPI 1
/// and 1.5 on a synthetic 10P-like capture: lights of a comet with a track, darks, flats, bias, one file with no type that is a
/// guess, and one light no dark matches. A press picks a folder, the detection runs off the render thread, the Import page shows
/// the groups and the chosen group's files; a reassignment goes through the Lib and the table shows the saved answer; the Workflow
/// page lists what fits and what does not, previews the chosen one, and a press, a double-click or Enter uses it.
/// </summary>
/// <remarks>In the theme collection: the captures apply a palette, which is process-wide state.</remarks>
[Collection(ThemeCollection.Name)]
public sealed class WorkbenchImportScreenTests : IDisposable
{
    private const int Size = 32;
    private const float FullScale = 4096f;
    private const string LongLight = "10PTempel2_ASI2600MC_IDAS-LPS-D3_light_60s_gain1600_-5C_2026-12-21T00-00-00_0001_with_a_very_long_name.fits";
    private readonly TempFolders _folders = new();
    private int _serial;

    public void Dispose() => _folders.Dispose();

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

    private static WorkbenchRunner Runner() => new(Catalog());

    private void Write(string folder, string name, FrameType type, double exposure, double level, float temperature = -5f, bool stars = false)
    {
        Directory.CreateDirectory(folder);
        var pixels = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                pixels[y, x] = (float)(level + (((x * 7) + (y * 13)) % 5) - 2);
            }
        }

        if (stars)
        {
            foreach (var (bx, by) in new[] { (4, 4), (20, 18) })
            {
                for (var y = by; y < by + 6; y++)
                {
                    for (var x = bx; x < bx + 6; x++)
                    {
                        pixels[y, x] = 3900f;
                    }
                }
            }
        }

        var meta = new ImageMeta
        {
            Instrument = "ASI2600MC",
            ExposureStartTime = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero).AddMinutes(_serial++),
            ExposureDuration = TimeSpan.FromSeconds(exposure),
            FrameType = type,
            PixelSizeX = 3.76f,
            PixelSizeY = 3.76f,
            FocalLength = 1000,
            BinX = 1,
            BinY = 1,
            CCDTemperature = temperature,
            SensorType = SensorType.RGGB,
            ObjectName = type == FrameType.Light ? "10P/Tempel 2" : string.Empty,
            Gain = 1600,
            Offset = 25,
            Latitude = 48f,
            Longitude = 11f,
            SensorFullScaleAdu = FullScale,
        };
        new Image(data: [pixels], bitDepth: BitDepth.Int16, maxValue: FullScale, minValue: 0f, pedestal: 0f, imageMeta: meta)
            .WriteToFitsFile(Path.Combine(folder, name));
    }

    // The 10P night: five lights at -5 C and a sixth of 120 s (no dark matches it), three darks at -5 C, three flats, two bias, a file
    // with no type (an even field: a flat, guessed), and the comet's track.
    private async Task<string> CaptureAsync(CancellationToken ct)
    {
        var folder = Path.Combine(_folders.Create("capture-").FullName, "10p_tempel");
        Write(folder, LongLight, FrameType.Light, 60, 300, stars: true);
        for (var i = 2; i <= 5; i++)
        {
            Write(folder, $"10PTempel2_light_60s_-5C_{i:0000}.fits", FrameType.Light, 60, 300, stars: true);
        }

        Write(folder, "10PTempel2_light_120s_-5C_0006.fits", FrameType.Light, 120, 300, stars: true);
        for (var i = 1; i <= 3; i++)
        {
            Write(folder, $"dark_60s_-5C_{i:0000}.fits", FrameType.Dark, 60, 100);
            Write(folder, $"flat_{i:0000}.fits", FrameType.Flat, 2, 2000);
        }

        Write(folder, "bias_0001.fits", FrameType.Bias, 0, 100);
        Write(folder, "bias_0002.fits", FrameType.Bias, 0, 100);
        Write(folder, "IMG_0412.fits", FrameType.None, 2, 2000);
        var start = new DateTimeOffset(2026, 12, 20, 23, 50, 0, TimeSpan.Zero);
        await new CometTrack(CometTrack.CurrentSchemaVersion, CometTrack.Horizons, "10P", 48f, 11f, 0,
            [.. Enumerable.Range(0, 4).Select(i => new EphemerisSample(start.AddMinutes(10 * i), 127.0 + (0.001 * i), -41.2))])
            .WriteAsync(Path.Combine(folder, "10P.track.json"), ct);
        return folder;
    }

    private static async Task<WorkbenchE2E> StartAtImportAsync(float dpi, string capture, CancellationToken ct, uint width = 1600, uint height = 1000)
    {
        var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog(), width, height);
        e2e.FakeFolderPicks(capture);
        e2e.Click(e2e.RegionOfLabel("New project"));
        await e2e.PumpUntilAsync(() => e2e.State.Stage == WorkbenchStage.Import && e2e.State.Import is { Capture: not null, Detecting: false },
            "the Import page with the detected folder", ct);
        return e2e;
    }

    private static async Task GoToWorkflowAsync(WorkbenchE2E e2e, CancellationToken ct)
    {
        e2e.ClickText("Choose a workflow ›");
        await e2e.PumpUntilAsync(() => e2e.State.Stage == WorkbenchStage.Workflow, "the Workflow page", ct);
    }

    private static bool Shows(WorkbenchE2E e2e, string text) => e2e.Texts().Any(t => t.Text == text);

    private static void DoubleClick(WorkbenchE2E e2e, RectF32 rect)
    {
        var (x, y) = (rect.X + (rect.Width / 2f), rect.Y + (rect.Height / 2f));
        e2e.Host.HandlePointer(new InputEvent.MouseDown(x, y, MouseButton.Left, ClickCount: 1));
        e2e.Host.HandlePointer(new InputEvent.MouseUp(x, y, MouseButton.Left));
        e2e.Host.HandlePointer(new InputEvent.MouseDown(x, y, MouseButton.Left, ClickCount: 2));
        e2e.Host.HandlePointer(new InputEvent.MouseUp(x, y, MouseButton.Left));
        e2e.Frame();
    }

    private static int GroupIndex(WorkbenchE2E e2e, DetectedGroupKind kind)
        => e2e.State.Import!.Capture!.Groups.ToList().FindIndex(group => group.Kind == kind);

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task NewProjectShowsTheImportPageWithTheGroupsFoundAndTheFirstGroupsFiles(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = await CaptureAsync(ct);
        await using var e2e = await StartAtImportAsync(dpi, capture, ct);

        e2e.State.Stage.ShouldBe(WorkbenchStage.Import);
        var groups = e2e.State.Import!.Capture!.Groups;
        groups.Select(static g => g.Kind).ShouldBe(
            [DetectedGroupKind.Lights, DetectedGroupKind.Darks, DetectedGroupKind.Flats, DetectedGroupKind.Bias, DetectedGroupKind.CometTrack, DetectedGroupKind.NeedsALook],
            "lights first, then the calibration, the track and what needs a look");
        foreach (var title in new[] { "FOUND", "Lights", "Darks", "Flats", "Bias", "Comet track", "Choose a workflow ›", "Cancel" })
        {
            Shows(e2e, title).ShouldBeTrue($"the page says '{title}'");
        }

        e2e.Texts().Select(static t => t.Text).ShouldContain(static t => t.StartsWith("⚠ 2 files need a look", StringComparison.Ordinal));
        Shows(e2e, "10PTempel2_light_60s_-5C_0002.fits").ShouldBeTrue("the chosen (first) group's files are the table");
        Shows(e2e, "dark_60s_-5C_0001.fits").ShouldBeFalse("another group's files are not");
        e2e.State.View.ShouldBeNull("nothing of the workbench is opened yet");
        File.Exists(WorkbenchProjectFile.PathIn(WorkbenchProjects.WorkdirOf(capture))).ShouldBeTrue("the project is in the capture's workdir");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ChoosingAGroupShowsItsFilesAndTheNeedsALookGroupHoldsTheGuessAndTheLightNoDarkMatches(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = await CaptureAsync(ct);
        await using var e2e = await StartAtImportAsync(dpi, capture, ct);

        var darks = GroupIndex(e2e, DetectedGroupKind.Darks);
        e2e.Click(e2e.Region($"Group:{darks}"));
        e2e.State.Import!.SelectedGroup.ShouldBe(darks);
        Shows(e2e, "dark_60s_-5C_0001.fits").ShouldBeTrue();
        Shows(e2e, "10PTempel2_light_60s_-5C_0002.fits").ShouldBeFalse();

        var look = GroupIndex(e2e, DetectedGroupKind.NeedsALook);
        e2e.Click(e2e.Region($"Group:{look}"));
        Shows(e2e, "IMG_0412.fits").ShouldBeTrue("the file with no IMAGETYP");
        Shows(e2e, "10PTempel2_light_120s_-5C_0006.fits").ShouldBeTrue("the light no dark matches");
        e2e.Texts().Select(static t => t.Text).ShouldContain(static t => t.StartsWith("no IMAGETYP", StringComparison.Ordinal), "the guess says why");
        e2e.Texts().Select(static t => t.Text).ShouldContain("Flat?", "an unsure kind carries a question mark, colour is not the only mark");
        e2e.Texts().Select(static t => t.Text).ShouldContain(static t => t.Contains("no dark matches", StringComparison.Ordinal));
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AReassignmentIsSavedInTheProjectAndTheTableShowsTheSavedAnswer(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = await CaptureAsync(ct);
        await using var e2e = await StartAtImportAsync(dpi, capture, ct);
        e2e.Click(e2e.Region($"Group:{GroupIndex(e2e, DetectedGroupKind.NeedsALook)}"));

        // Open the kind box of the guessed file and choose Bias (the menu is Detected, Light, Dark, Flat, Bias, Dark flat, Leave out).
        e2e.Click(e2e.Region(WorkbenchScreen<RgbaImage>.KindHitPrefix + "IMG_0412.fits"));
        var rows = e2e.MenuRows();
        rows.Length.ShouldBe(WorkbenchActions.KindChoices.Length, "every kind a stack takes, and the way back to the detection");
        e2e.Click(rows[4]);
        await e2e.PumpUntilAsync(() => e2e.State.Import is { Reassigning: false } && e2e.State.Import.Capture!.Files
            .Any(static f => f.Place == "IMG_0412.fits" && f.Kind == FrameType.Bias), "the saved answer", ct);

        var saved = await WorkbenchProjects.ReassignmentsAsync(capture, ct);
        saved.Select(static r => (r.Path, r.Kind)).ShouldBe([("IMG_0412.fits", FrameType.Bias)], "project.json holds it");
        File.ReadAllText(WorkbenchProjectFile.PathIn(WorkbenchProjects.WorkdirOf(capture))).ShouldContain("IMG_0412.fits");
        var detected = e2e.State.Import.Capture!.Files.Single(static f => f.Place == "IMG_0412.fits");
        (detected.Reassigned, detected.Unsure).ShouldBe((true, false));
        // The file left the needs-a-look group; the group on show is now the light with no dark, and the table says what the Lib answered.
        e2e.State.Import.NeedsALook.ShouldBe(1);
        Shows(e2e, "IMG_0412.fits").ShouldBeFalse("it is no longer among the files to look at");
        e2e.Click(e2e.Region($"Group:{GroupIndex(e2e, DetectedGroupKind.Bias)}"));
        Shows(e2e, "IMG_0412.fits").ShouldBeTrue("it is a bias now");
        Shows(e2e, "Bias ✎").ShouldBeTrue("its kind box says it was set by the person");
        Shows(e2e, "Flat?").ShouldBeFalse();

        // And taking the answer back (the first entry) restores the detection.
        e2e.Click(e2e.Region(WorkbenchScreen<RgbaImage>.KindHitPrefix + "IMG_0412.fits"));
        e2e.Click(e2e.MenuRows()[0]);
        await e2e.PumpUntilAsync(() => e2e.State.Import is { Reassigning: false } && e2e.State.Import.NeedsALook == 2, "the detection standing again", ct);
        (await WorkbenchProjects.ReassignmentsAsync(capture, ct)).ShouldBeEmpty();
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheWorkflowPageListsWhatFitsAndWhatDoesNotWithWhyAndPreviewsTheBestFit(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = await CaptureAsync(ct);
        await using var e2e = await StartAtImportAsync(dpi, capture, ct);
        await GoToWorkflowAsync(e2e, ct);

        var import = e2e.State.Import!;
        import.ChosenWorkflow.ShouldBe(BuiltInWorkflows.CometColour, "the best fit is preselected");
        foreach (var text in new[] { "FITS THESE FRAMES", "DOES NOT FIT", "WHAT IT WILL DO", "Comet, colour camera", "Deep sky, colour camera",
            "Start from an empty workflow", "‹ Back", "Use selected workflow ›", "Or double-click a workflow, or press Enter" })
        {
            Shows(e2e, text).ShouldBeTrue($"the page says '{text}'");
        }

        foreach (var choice in import.Workflows.Where(static w => !w.Answer.Fit.Fits))
        {
            Shows(e2e, choice.Answer.Template.Title).ShouldBeTrue($"{choice.Id} says it does not fit");
            e2e.Regions().Any(r => r.Result is HitResult.ButtonHit hit && hit.Action == $"Workflow:{choice.Id}")
                .ShouldBeFalse("a workflow that does not fit is not chosen");
        }

        var preview = import.Workflows.Single(static w => w.Id == BuiltInWorkflows.CometColour).Tree;
        preview.Length.ShouldBeGreaterThan(5, "the preview is the tree the workflow writes");
        e2e.Texts().Select(static t => t.Text).ShouldContain(preview.First(static r => r.Kind == WorkflowRowKind.Step).Label);

        // A press chooses another that fits; the preview follows the Lib's choice.
        e2e.Click(e2e.Region($"Workflow:{BuiltInWorkflows.DeepSkyColour}"));
        e2e.State.Import!.ChosenWorkflow.ShouldBe(BuiltInWorkflows.DeepSkyColour);
        e2e.State.Stage.ShouldBe(WorkbenchStage.Workflow, "choosing does not use it");

        e2e.ClickText("‹ Back");
        e2e.State.Stage.ShouldBe(WorkbenchStage.Import);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task UseSelectedWorkflowWritesTheProjectAndOpensTheWorkbench(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = await CaptureAsync(ct);
        await using var e2e = await StartAtImportAsync(dpi, capture, ct);
        await GoToWorkflowAsync(e2e, ct);
        e2e.Click(e2e.Region($"Workflow:{BuiltInWorkflows.DeepSkyColour}"));

        e2e.ClickText("Use selected workflow ›");
        await e2e.PumpUntilAsync(() => e2e.State.Stage == WorkbenchStage.Workbench && e2e.State.View is { Image: not null } && !e2e.State.IsReading,
            "the workbench on the new project", ct);

        e2e.State.Import.ShouldBeNull();
        var (project, _) = await WorkbenchProjectFile.ReadAsync(WorkbenchProjects.WorkdirOf(capture), ct);
        project!.Workflow.ShouldBe(BuiltInWorkflows.DeepSkyColour);
        e2e.StepCells().Length.ShouldBeGreaterThan(0, "the step bar shows the workflow's steps");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ADoubleClickOnAWorkflowUsesIt(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = await CaptureAsync(ct);
        await using var e2e = await StartAtImportAsync(dpi, capture, ct);
        await GoToWorkflowAsync(e2e, ct);

        DoubleClick(e2e, e2e.Region($"Workflow:{BuiltInWorkflows.DeepSkyColour}"));
        await e2e.PumpUntilAsync(() => e2e.State.Stage == WorkbenchStage.Workbench && e2e.State.View is { Image: not null } && !e2e.State.IsReading,
            "the workbench", ct);

        var (project, _) = await WorkbenchProjectFile.ReadAsync(WorkbenchProjects.WorkdirOf(capture), ct);
        project!.Workflow.ShouldBe(BuiltInWorkflows.DeepSkyColour);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task EnterUsesTheSelectedWorkflow(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = await CaptureAsync(ct);
        await using var e2e = await StartAtImportAsync(dpi, capture, ct);
        await GoToWorkflowAsync(e2e, ct);

        e2e.Host.HandleKeyDown(new InputEvent.KeyDown(InputKey.Enter, InputModifier.None));
        e2e.Frame();
        await e2e.PumpUntilAsync(() => e2e.State.Stage == WorkbenchStage.Workbench && e2e.State.View is { Image: not null } && !e2e.State.IsReading,
            "the workbench", ct);

        var (project, _) = await WorkbenchProjectFile.ReadAsync(WorkbenchProjects.WorkdirOf(capture), ct);
        project!.Workflow.ShouldBe(BuiltInWorkflows.CometColour, "the best fit was selected");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AnEmptyWorkflowCanBeStartedFromAndCancelTakesTheNewProjectBack(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = await CaptureAsync(ct);
        await using (var e2e = await StartAtImportAsync(dpi, capture, ct))
        {
            e2e.ClickText("Cancel");
            await e2e.PumpUntilAsync(() => !File.Exists(WorkbenchProjectFile.PathIn(WorkbenchProjects.WorkdirOf(capture))), "the project taken back", ct);
            e2e.State.Stage.ShouldBe(WorkbenchStage.Workbench);
            e2e.State.Import.ShouldBeNull();
        }

        await using var again = await StartAtImportAsync(dpi, capture, ct);
        await GoToWorkflowAsync(again, ct);
        again.ClickText("Start from an empty workflow");
        await again.PumpUntilAsync(() => again.State.Stage == WorkbenchStage.Workbench && again.State.View is not null && !again.State.IsReading, "the workbench", ct);
        var (project, _) = await WorkbenchProjectFile.ReadAsync(WorkbenchProjects.WorkdirOf(capture), ct);
        project!.Workflow.ShouldBe(BuiltInWorkflows.Empty);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task OpeningAnExistingProjectGoesStraightToTheWorkbench(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = await CaptureAsync(ct);
        (await WorkbenchProjects.NewAsync(capture, ct)).Refusal.ShouldBeNull();
        await using var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog());

        await e2e.OpenProjectAsync(WorkbenchProjects.WorkdirOf(capture), ct);

        e2e.State.Stage.ShouldBe(WorkbenchStage.Workbench);
        e2e.State.Import.ShouldBeNull();
        e2e.StepCells().Length.ShouldBe(1);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f, "import")]
    [InlineData(1.5f, "import")]
    [InlineData(1f, "workflow")]
    [InlineData(1.5f, "workflow")]
    public async Task LongNamesAreNeverCutSilentlyOnEitherPage(float dpi, string page)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = await CaptureAsync(ct);
        await using var e2e = await StartAtImportAsync(dpi, capture, ct, 1280, 800);
        if (page == "workflow")
        {
            await GoToWorkflowAsync(e2e, ct);
        }

        AssertNothingCutSilently(e2e);
        if (page == "import")
        {
            e2e.Texts().Select(static t => t.Text).ShouldContain(LongLight, "the long name is the text, cut only by the painter");
        }
    }

    private static void AssertNothingCutSilently(WorkbenchE2E e2e)
    {
        var regions = e2e.Regions();
        foreach (var (full, shown, bounds) in e2e.Host.Screen.CutTexts())
        {
            var (cx, cy) = (bounds.X + (bounds.Width / 2f), bounds.Y + (bounds.Height / 2f));
            regions.Where(r => cx >= r.X && cx <= r.X + r.Width && cy >= r.Y && cy <= r.Y + r.Height && r.Tooltip is not null)
                .ShouldContain(r => r.Tooltip!.Contains(full, StringComparison.Ordinal), $"'{full}' is shown as '{shown}' and carries its whole text on hover");
        }
    }

    public static TheoryData<uint, uint, UiThemeState, string> CaptureCases
    {
        get
        {
            var data = new TheoryData<uint, uint, UiThemeState, string>();
            foreach (var page in new[] { "import", "needs-a-look", "workflow" })
            {
                foreach (var theme in new[] { UiThemeState.Light, UiThemeState.Dark, UiThemeState.Night })
                {
                    data.Add(1280, 800, theme, page);
                    data.Add(2000, 1100, theme, page);
                }
            }

            return data;
        }
    }

    [Theory(Timeout = 180_000)]
    [MemberData(nameof(CaptureCases))]
    public async Task ThePagesAreCapturedInEveryPaletteAndCutNothingSilently(uint width, uint height, UiThemeState theme, string page)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = await CaptureAsync(ct);
        GuiTheme.Apply(theme, desktopIsDark: true);
        try
        {
            await using var e2e = await StartAtImportAsync(1f, capture, ct, width, height);
            if (page == "needs-a-look")
            {
                e2e.Click(e2e.Region($"Group:{GroupIndex(e2e, DetectedGroupKind.NeedsALook)}"));
            }
            else if (page == "workflow")
            {
                await GoToWorkflowAsync(e2e, ct);
            }

            AssertNothingCutSilently(e2e);
            if (Environment.GetEnvironmentVariable("TIANWEN_KEEP_TEST_OUTPUT") == "1")
            {
                var surface = e2e.Renderer.Surface;
                File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, $"workbench-new-{page}-{width}x{height}-{theme}.png"),
                    Helpers.DisplayImageWriter.EncodePng(surface.Pixels, (int)surface.Width, (int)surface.Height));
            }
        }
        finally
        {
            GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        }
    }
}
