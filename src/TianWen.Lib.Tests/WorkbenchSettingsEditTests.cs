using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.Workbench;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The panel beside the project tree as controls (#2553 section 3), driven through the real host at DPI 1 and 1.5: every kind of setting the
/// Lib derives from an operation's JSON contract (a checkbox for on/off, a dropdown for one of a few, a field for a number) edits and
/// saves into <c>project.json</c>, the step's key changes so the runner answers it as not made (stale), a step is switched off and on, a
/// frame group's file list reassigns through the Lib routine, and a click on a file shows it on the canvas.
/// </summary>
[Collection("Viewer")]
public sealed class WorkbenchSettingsEditTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static OperationCatalog Catalog() => new([new KnobOperation()]);

    private static WorkbenchRunner Runner() => new(Catalog());

    // Two knob steps of the image "m", both made, so a change to the first is seen as stale on both.
    private async Task<string> MadeProjectAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create(name + "-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder,
            WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master, WorkbenchFixtures.Edit(KnobOperation.Id), WorkbenchFixtures.Edit(KnobOperation.Id))), ct);
        (await Runner().RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 1 }, ct)).Refusal.ShouldBeNull();
        return folder;
    }

    private static async Task<WorkbenchE2E> OpenAndSelectFirstAsync(float dpi, string folder, CancellationToken ct)
    {
        var e2e = WorkbenchE2E.Start(dpi, Runner(), Catalog(), height: 1500);
        await e2e.OpenProjectAsync(folder, ct);
        e2e.Click(e2e.StepCells()[0]);
        await e2e.PumpUntilAsync(() => e2e.State.SelectedStep == 0 && e2e.State.Plan is { Index: 0 }, "step 1 to be selected", ct);
        return e2e;
    }

    private static async Task<JsonObject> SettingsOfFirstAsync(string folder, CancellationToken ct)
        => (await WorkbenchProjectFile.ReadAsync(folder, ct)).Project!.Images[0].Edits[0].Settings ?? [];

    private static async Task<ImmutableArray<StepReport>> StatusAsync(string folder, CancellationToken ct)
        => (await Runner().StatusAsync(folder, "m", ct)).Images.Single().Steps;

    private static async Task SettleStaleAsync(WorkbenchE2E e2e, CancellationToken ct)
        => await e2e.PumpUntilAsync(() => !e2e.State.IsReading && e2e.State.View?.Image?.Steps[0].State is StepState.WillRun, "the runner to answer the step as not made", ct);

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task EveryKindOfSettingIsAControlTheLibDerivedFromTheOperationsContract(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await MadeProjectAsync("kinds");
        await using var e2e = await OpenAndSelectFirstAsync(dpi, folder, ct);

        var kinds = OperationSettingsForm.Describe(new KnobOperation()).ToDictionary(static f => f.Name, static f => f.Kind);
        kinds.ShouldBe(new Dictionary<string, OperationSettingKind>
        {
            ["flag"] = OperationSettingKind.Bool,
            ["mode"] = OperationSettingKind.Choice,
            ["count"] = OperationSettingKind.Integer,
            ["gain"] = OperationSettingKind.Number,
            ["extra"] = OperationSettingKind.Number,
            ["marks"] = OperationSettingKind.Other,
        });
        OperationSettingsForm.Describe(new KnobOperation()).Single(static f => f.Name == "mode").Choices.ShouldBe(["Fast", "Thorough"]);

        e2e.Regions().Any(r => r.Result is HitResult.ButtonHit { Action: "Setting:flag" }).ShouldBeTrue("a bool is a checkbox");
        e2e.Regions().Any(r => r.Result is HitResult.ButtonHit { Action: "Setting:m:0:mode" }).ShouldBeTrue("a choice is a dropdown");
        var inputs = new List<TextInputState>();
        e2e.Host.Screen.CollectRegisteredTextInputs(inputs);
        inputs.Count.ShouldBe(3, "a whole number, a number and a number that may be left out are fields");
        e2e.PanelText().ShouldContain("Flag");
        e2e.PanelText().ShouldContain("SETTINGS", Case.Sensitive, "a section label is the small dim upper-case one");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ACheckboxSavesIntoTheProjectAndTheStepReadsStaleFromTheRunnersAnswer(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await MadeProjectAsync("flag");
        var before = await StatusAsync(folder, ct);
        before.Select(static s => s.State).ShouldBe([StepState.Kept, StepState.Kept]);
        await using var e2e = await OpenAndSelectFirstAsync(dpi, folder, ct);

        e2e.Click(e2e.Region("Setting:flag"));
        await SettleStaleAsync(e2e, ct);

        (await SettingsOfFirstAsync(folder, ct))["flag"]!.GetValue<bool>().ShouldBeTrue("the edit is written to project.json");
        var after = await StatusAsync(folder, ct);
        after[0].State.ShouldBe(StepState.WillRun, "its key changed");
        after[1].State.ShouldBe(StepState.WillRun, "and so did the step after it, which takes its result");
        e2e.State.View!.Image!.Steps[0].State.ShouldBe(StepState.WillRun, "the screen shows the runner's answer");
        e2e.Actions.Tree().First(static r => r.Row.EditIndex == 0).Mark.ShouldBe(WorkbenchActions.TreeMarkFor(StepState.WillRun, changed: true), "the tree marks it stale");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ADropdownSavesTheChosenValue(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await MadeProjectAsync("choice");
        await using var e2e = await OpenAndSelectFirstAsync(dpi, folder, ct);

        e2e.Click(e2e.Region("Setting:m:0:mode"));
        e2e.MenuRows().Length.ShouldBe(2, "the menu offers each value of the enum");
        e2e.Click(e2e.MenuRows()[1]);
        await SettleStaleAsync(e2e, ct);

        (await SettingsOfFirstAsync(folder, ct))["mode"]!.GetValue<string>().ShouldBe("Thorough");
        e2e.Actions.SettingControls().Single(static c => c.Field.Name == "mode").Value.ShouldBe("Thorough", "the panel shows what the project now holds");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ANumberFieldSavesOnEnterAndARefusedTextSaysWhyAndWritesNothing(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await MadeProjectAsync("number");
        await using var e2e = await OpenAndSelectFirstAsync(dpi, folder, ct);
        var inputs = new List<TextInputState>();
        e2e.Host.Screen.CollectRegisteredTextInputs(inputs);
        var count = inputs.Single(static i => i.Placeholder == "3");

        count.Text = "seven";
        await count.OnCommit!("seven");
        e2e.Frame();
        e2e.State.SettingRefusal.ShouldNotBeNull().ShouldContain("whole number");
        (await SettingsOfFirstAsync(folder, ct)).ContainsKey("count").ShouldBeFalse("nothing was written");
        (await StatusAsync(folder, ct))[0].State.ShouldBe(StepState.Kept, "and the step is still made");

        count.Text = "7";
        await count.OnCommit!("7");
        await SettleStaleAsync(e2e, ct);
        (await SettingsOfFirstAsync(folder, ct))["count"]!.GetValue<int>().ShouldBe(7);
        e2e.State.SettingRefusal.ShouldBeNull();
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheOnOffSwitchesTheStepOffAndTheStepsAfterItTakeItsInput(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await MadeProjectAsync("switch");
        await using var e2e = await OpenAndSelectFirstAsync(dpi, folder, ct);

        e2e.Click(e2e.Region(WorkbenchScreen<RgbaImage>.StepSwitchHit));
        await e2e.PumpUntilAsync(() => !e2e.State.IsReading && e2e.State.View?.Image?.Steps[0].State is StepState.Off, "the runner to answer the step as off", ct);

        (await WorkbenchProjectFile.ReadAsync(folder, ct)).Project!.Images[0].Edits[0].Enabled.ShouldBeFalse();
        (await StatusAsync(folder, ct))[0].State.ShouldBe(StepState.Off);
        string.Join(' ', e2e.PanelText().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ShouldContain("Switch it on", Case.Insensitive, "the panel says what fixes it");

        e2e.Click(e2e.Region(WorkbenchScreen<RgbaImage>.StepSwitchHit));
        await e2e.PumpUntilAsync(() => !e2e.State.IsReading && e2e.State.View?.Image?.Steps[0].State is not StepState.Off, "the step to be on again", ct);
        (await WorkbenchProjectFile.ReadAsync(folder, ct)).Project!.Images[0].Edits[0].Enabled.ShouldBeTrue();
        (await StatusAsync(folder, ct))[0].State.ShouldBe(StepState.Kept, "its earlier result is found in the store");
    }

    [Fact(Timeout = 120_000)]
    public async Task TheCommandLineIsFoldedAwayUntilItIsAsked()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await MadeProjectAsync("fold");
        await using var e2e = await OpenAndSelectFirstAsync(1f, folder, ct);

        e2e.PanelText().ShouldContain("Command line");
        e2e.Texts().ShouldNotContain(static t => t.Text == "Copy", "folded");
        e2e.ClickText("Command line ⌄");
        e2e.Texts().ShouldContain(static t => t.Text == "Copy");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AFileOfAFrameGroupShowsOnTheCanvasAndAReassignGoesThroughTheLib(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = Path.Combine(_folders.Create("capture-").FullName, "night");
        WriteFrame(capture, "light_0001.fits", FrameType.Light, 60);
        WriteFrame(capture, "light_0002.fits", FrameType.Light, 60);
        WriteFrame(capture, "dark_0001.fits", FrameType.Dark, 60);
        (await WorkbenchProjects.NewAsync(capture, ct)).Refusal.ShouldBeNull();
        var workdir = WorkbenchProjects.WorkdirOf(capture);
        await using var e2e = WorkbenchE2E.Start(dpi, new WorkbenchRunner(new OperationCatalog([new StackOperation()])), new OperationCatalog([new StackOperation()]), height: 1500);
        await e2e.OpenProjectAsync(workdir, ct);
        await e2e.PumpUntilAsync(() => e2e.State.Frames is { Groups.IsEmpty: false }, "the capture folder to be read", ct);

        e2e.Click(e2e.Region("Frames:Lights"));
        await e2e.PumpUntilAsync(() => e2e.State.Frames?.Groups.First(static g => g.Name == "Lights").Details.Length == 2, "the lights", ct);
        e2e.Frame();
        e2e.Texts().ShouldContain(static t => t.Text.Contains("light_0001", StringComparison.Ordinal), "the file list");

        var file = e2e.State.Frames!.Groups.First(static g => g.Name == "Lights").Details.First(static d => d.Place.Contains("light_0001", StringComparison.Ordinal));
        e2e.Click(e2e.Region("Frame:" + file.Place));
        await e2e.PumpUntilAsync(() => e2e.Controller.Document is { } document && string.Equals(document.FilePath, file.FullPath, StringComparison.OrdinalIgnoreCase),
            "the file to open in the viewer", ct);
        e2e.State.PreviewFrame.ShouldBe(file.FullPath);

        e2e.Click(e2e.Region(WorkbenchScreen<RgbaImage>.KindHitPrefix + file.Place));
        var rows = e2e.MenuRows();
        var dark = WorkbenchActions.KindChoices.ToList().FindIndex(static c => c.Kind == FrameType.Dark);
        e2e.Click(rows[dark]);
        await e2e.PumpUntilAsync(() => e2e.State.Frames?.Groups.First(static g => g.Name == "Darks").Details.Length == 2, "the file to be a dark", ct);
        (await WorkbenchProjects.ReassignmentsAsync(capture, ct)).Single().Kind.ShouldBe(FrameType.Dark);
    }

    internal static void WriteFrame(string folder, string name, FrameType type, double exposure)
    {
        Directory.CreateDirectory(folder);
        var pixels = new float[32, 32];
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                pixels[y, x] = 300f + (((x * 7) + (y * 13)) % 5);
            }
        }

        var meta = new ImageMeta
        {
            Instrument = "ASI2600MC",
            ExposureStartTime = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero).AddMinutes(Math.Abs(name.GetHashCode()) % 600),
            ExposureDuration = TimeSpan.FromSeconds(exposure),
            FrameType = type,
            PixelSizeX = 3.76f,
            PixelSizeY = 3.76f,
            FocalLength = 1000,
            BinX = 1,
            BinY = 1,
            CCDTemperature = -5f,
            SensorType = SensorType.RGGB,
            ObjectName = type == FrameType.Light ? "M 42" : string.Empty,
            Gain = 100,
            Offset = 25,
            SensorFullScaleAdu = 4096f,
        };
        new Image(data: [pixels], bitDepth: BitDepth.Int16, maxValue: 4096f, minValue: 0f, pedestal: 0f, imageMeta: meta).WriteToFitsFile(Path.Combine(folder, name));
    }
}

/// <summary>The choices of <see cref="KnobSettings.Mode"/>.</summary>
internal enum KnobMode
{
    Fast,
    Thorough,
}

/// <summary>One setting of every kind the panel makes a control for.</summary>
internal sealed record KnobSettings
{
    public bool Flag { get; set; }

    public KnobMode Mode { get; set; } = KnobMode.Fast;

    public int Count { get; set; } = 3;

    public double Gain { get; set; } = 1.5;

    public float? Extra { get; set; }

    public ImmutableArray<float> Marks { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, UseStringEnumConverter = true)]
[JsonSerializable(typeof(KnobSettings))]
internal sealed partial class KnobJsonContext : JsonSerializerContext
{
}

/// <summary>A stored operation whose settings carry one of each kind; it copies its input, so a run is made and a key is the thing under test.</summary>
internal sealed class KnobOperation : Operation<KnobSettings>
{
    public const string Id = "knob";

    public override OperationDescription Description { get; } = new(
        Id, Version: 1, Title: "Knob", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [],
        Settings: [new OperationSetting("flag", "Does the flag thing.")])
    {
        Summary = "Copies the image, with every kind of setting to try.",
    };

    protected override JsonTypeInfo<KnobSettings> SettingsInfo => KnobJsonContext.Default.KnobSettings;

    public override ImmutableArray<string> CommandLine(KnobSettings settings, IReadOnlyList<string> inputs, string output)
        => [Id, .. inputs, "-o", output, "--count", StepCommandLine.Number(settings.Count)];

    public override ValueTask<OperationOutcome> RunAsync(KnobSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (!inputs[0].TryRead(out var image, out var wcs, out var refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        OperationOutput.Write(image, outputPath, wcs);
        return ValueTask.FromResult(OperationOutcome.Done);
    }
}
