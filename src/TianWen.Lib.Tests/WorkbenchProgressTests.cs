using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>A stored operation that copies its input, and reports its work as four fractions on the way.</summary>
internal sealed class FractionOperation : Operation<AddSettings>
{
    public override OperationDescription Description { get; } = new(
        "fractions", Version: 1, Title: "Fractions", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [], Settings: [])
    {
        Summary = "Copies the image and says how far it is on the way.",
    };

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output)
        => [Description.Id, inputs[0], "-o", output];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
        => RunAsync(settings, inputs, outputPath, say, progress: null, cancellationToken);

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        foreach (var fraction in new[] { 0.25, 0.5, 0.75, 1.0 })
        {
            progress?.Report(fraction);
        }

        if (!inputs[0].TryRead(out var image, out var wcs, out var refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        OperationOutput.Write(image, outputPath, wcs);
        return ValueTask.FromResult(OperationOutcome.Done);
    }
}

/// <summary>
/// What a run tells while it works (<see cref="WorkbenchRunOptions.Progress"/>; #1642): each step it runs starts and
/// ends, a step taken from the store only ends, as kept, an operation's fractions arrive between its start and its
/// end, and a refusal ends the step as refused with its message.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchProgressTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    /// <summary>Takes each report as it is made, on the thread that makes it: no posting to a context, no waiting.</summary>
    private sealed class Told : IProgress<WorkbenchProgress>
    {
        public List<WorkbenchProgress> Reports { get; } = [];

        public void Report(WorkbenchProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }

        public string[] Lines
        {
            get
            {
                lock (Reports)
                {
                    return [.. Reports.Select(static r => r.Kind switch
                    {
                        WorkbenchProgressKind.Started => $"{r.StepIndex + 1} started",
                        WorkbenchProgressKind.Fraction => $"{r.StepIndex + 1} {r.Fraction}",
                        _ => $"{r.StepIndex + 1} {r.State}",
                    })];
                }
            }
        }
    }

    private async Task<string> ProjectAsync(string name, params ProjectEdit[] edits)
    {
        var folder = _folders.Create(name + "-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master, edits)), TestContext.Current.CancellationToken);
        return folder;
    }

    private static JsonObject Amount(float amount) => new() { ["amount"] = amount };

    [Fact(Timeout = 120_000)]
    public async Task EachStepRunIsToldStartedAndEndedInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ProjectAsync("order", WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)));
        var told = new Told();

        var report = await new WorkbenchRunner(new OperationCatalog([new AddOperation("add")]))
            .RunAsync(folder, new WorkbenchRunOptions { Progress = told }, ct);

        report.Succeeded.ShouldBeTrue(report.Refusal);
        told.Lines.ShouldBe(["1 started", "1 Ran", "2 started", "2 Ran"]);
        told.Reports.ShouldAllBe(r => r.Image == "m" && r.Operation == "add");
    }

    [Fact(Timeout = 120_000)]
    public async Task AStepTakenFromTheStoreIsToldKeptWithNoStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ProjectAsync("kept", WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("add", Amount(2)));
        var runner = new WorkbenchRunner(new OperationCatalog([new AddOperation("add")]));
        await runner.RunAsync(folder, cancellationToken: ct);
        var told = new Told();

        await runner.RunAsync(folder, new WorkbenchRunOptions { Progress = told }, ct);

        // The last step is in the store, so nothing before it is asked for.
        told.Lines.ShouldBe(["2 Kept"]);

        // A status reports nothing.
        var quiet = new Told();
        await runner.StatusAsync(folder, new WorkbenchRunOptions { Progress = quiet }, ct);
        quiet.Reports.ShouldBeEmpty();
    }

    [Fact(Timeout = 120_000)]
    public async Task AnOperationsFractionsArriveInOrderBetweenItsStartAndItsEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ProjectAsync("fractions", WorkbenchFixtures.Edit("fractions"));
        var told = new Told();

        var report = await new WorkbenchRunner(new OperationCatalog([new FractionOperation()]))
            .RunAsync(folder, new WorkbenchRunOptions { Progress = told }, ct);

        report.Succeeded.ShouldBeTrue(report.Refusal);
        told.Lines.ShouldBe(["1 started", "1 0.25", "1 0.5", "1 0.75", "1 1", "1 Ran"]);
    }

    [Fact(Timeout = 120_000)]
    public async Task ARefusedStepEndsRefusedWithItsMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ProjectAsync("refused", WorkbenchFixtures.Edit("break"), WorkbenchFixtures.Edit("break"));
        var told = new Told();

        var report = await new WorkbenchRunner(new OperationCatalog([new BreakOperation()]))
            .RunAsync(folder, new WorkbenchRunOptions { Progress = told }, ct);

        report.Succeeded.ShouldBeFalse();
        told.Lines.ShouldBe(["1 started", "1 Refused", "2 Blocked"]);
        told.Reports[1].Message.ShouldBe(BreakOperation.WhyItFails);
        told.Reports[2].Message.ShouldNotBeNull().ShouldContain("gave no result");
    }

    [Fact(Timeout = 300_000)]
    public async Task TheStacksFractionArrivesOnTheSmallestFrameSet()
    {
        var ct = TestContext.Current.CancellationToken;
        // The ten lights WorkbenchStackTests stacks.
        var frames = Path.Combine(_folders.Create("night-").FullName, "2026-12-21");
        var lights = Path.Combine(frames, "LIGHT");
        Directory.CreateDirectory(lights);
        StackingCloudEmptiedGroupTest.WriteFrames(lights, FrameType.Light, skyBackground: 100.0);
        var project = _folders.Create("stack-").FullName;
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit(StackOperation.Id))), ct);
        var told = new Told();

        var report = await new WorkbenchRunner(new OperationCatalog([new StackOperation(NullLogger<StackingPipeline>.Instance)]))
            .RunAsync(project, new WorkbenchRunOptions { Progress = told }, ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static i => i.Steps).Select(static s => s.Message)));
        output.WriteLine(string.Join(", ", told.Lines));
        var kinds = told.Reports.Select(static r => r.Kind).ToArray();
        kinds[0].ShouldBe(WorkbenchProgressKind.Started);
        kinds[^1].ShouldBe(WorkbenchProgressKind.Ended);
        var fractions = told.Reports.Where(static r => r.Kind == WorkbenchProgressKind.Fraction).Select(static r => r.Fraction).ToArray();
        fractions.ShouldNotBeEmpty("the stack reports how far it is");
        fractions.ShouldAllBe(static f => f >= 0 && f <= 1);
        fractions.ShouldBe([.. fractions.Order()], "the fraction never goes back");
        told.Reports[^1].State.ShouldBe(StepState.Ran);
    }
}
