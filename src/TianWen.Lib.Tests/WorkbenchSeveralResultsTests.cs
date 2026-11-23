using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A stored operation with fixed inputs whose one run gives two results: the image plus the amount, and as "half" the
/// image at half its values. It counts its runs.
/// </summary>
internal sealed class SplitOperation : MultiResultOperation<AddSettings>
{
    public const string Half = "half";

    public int Runs { get; private set; }

    public override OperationDescription Description { get; } = new(
        "split", Version: 1, Title: "Split", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [],
        Settings: [new OperationSetting("amount", "What is added to every pixel of the main result.")])
    {
        ExtraResults = [new OperationResultRole(Half, "The image at half its values.")],
    };

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, StepOutputs outputs)
        => [Description.Id, .. inputs, "-o", outputs.Path, .. outputs.Extra.Values];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, StepOutputs outputs, Action<string>? say,
        CancellationToken cancellationToken)
    {
        Runs++;
        if (!inputs[0].TryRead(out var image, out var wcs, out var refusal))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
        }

        var sum = new float[image.Height, image.Width];
        var half = new float[image.Height, image.Width];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                sum[y, x] = image[0, y, x] + settings.Amount;
                half[y, x] = image[0, y, x] * 0.5f;
            }
        }

        OperationOutput.Write(new Image([sum], BitDepth.Float32, maxValue: 100f, minValue: 0f, pedestal: 0f, imageMeta: image.ImageMeta), outputs.Path, wcs);
        if (outputs.Extra.TryGetValue(Half, out var place))
        {
            OperationOutput.Write(new Image([half], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: image.ImageMeta), place, wcs);
        }

        return ValueTask.FromResult(OperationOutcome.Done);
    }
}

/// <summary>An operation that says it gives an extra result and cannot be told where it goes: a catalog refuses it.</summary>
internal sealed class SaysExtraOperation : Operation<AddSettings>
{
    public override OperationDescription Description { get; } = new(
        "says-extra", Version: 1, Title: "Says", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [], Settings: [])
    {
        ExtraResults = [new OperationResultRole("other", "A result nothing writes.")],
    };

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output) => [Description.Id];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(OperationOutcome.Done);
}

/// <summary>
/// A step with fixed inputs that gives several results (#2465, for <c>image denoise --colour</c>): each result is kept
/// under a key of its own, another image starts from an extra one, and the step is kept only when all are there.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchSeveralResultsTests : IDisposable
{
    private const float Tolerance = 1e-6f;

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static StepState[] States(WorkbenchReport report, int image) => [.. report.Images[image].Steps.Select(static step => step.State)];

    [Fact(Timeout = 120_000)]
    public async Task AStepWithFixedInputsGivesSeveralResultsAndIsKeptOnlyWhenAllAreThere()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("several-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", master, WorkbenchFixtures.Edit("split", new JsonObject { ["amount"] = 2 })),
            WorkbenchFixtures.StartingFromResult("h", "m", SplitOperation.Half, WorkbenchFixtures.Edit("add", new JsonObject { ["amount"] = 1 }))), ct);
        var split = new SplitOperation();
        var add = new AddOperation("add");
        var runner = new WorkbenchRunner(new OperationCatalog([split, add]));

        // Asked for alone, the image that starts from the extra result has the step that gives it run.
        var first = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "h" }, ct);

        first.Succeeded.ShouldBeTrue(string.Join("; ", first.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        States(first, 0).ShouldBe([StepState.Ran]);
        States(first, 1).ShouldBe([StepState.Ran]);
        split.Runs.ShouldBe(1);
        var original = WorkbenchFixtures.Read(master)[0, 40, 50];
        var step = first.Images[0].Steps[0];
        WorkbenchFixtures.Read(step.Output.ShouldNotBeNull())[0, 40, 50].ShouldBe(original + 2f, Tolerance);
        WorkbenchFixtures.Read(first.Images[1].Steps[0].Output.ShouldNotBeNull())[0, 40, 50]
            .ShouldBe((original * 0.5f) + 1f, Tolerance, "the other image went on from the extra result");
        var store = Path.Combine(folder, StepStore.FolderName);
        var half = Path.Combine(store, StepStore.FileNameOf(StepKey.OfResult(step.Key, SplitOperation.Half)));
        File.Exists(half).ShouldBeTrue("the extra result is in the store under a key of its own");
        step.CommandLine.ShouldContain(half, "the step's line names every file it writes");
        first.Images[1].Key.ShouldBe(StepKey.OfResult(step.Key, SplitOperation.Half));
        Directory.GetFiles(store, "*.fits").Length.ShouldBe(3, "two results of one step, and the step after one of them");

        var second = await runner.RunAsync(folder, cancellationToken: ct);

        States(second, 0).ShouldBe([StepState.Kept]);
        States(second, 1).ShouldBe([StepState.Kept]);
        split.Runs.ShouldBe(1);

        // With one of its results gone the step is not whole, so it is made again when it is asked for.
        File.Delete(half);
        var status = await runner.StatusAsync(folder, cancellationToken: ct);
        var third = await runner.RunAsync(folder, cancellationToken: ct);

        States(status, 0).ShouldBe([StepState.WillRun]);
        States(third, 0).ShouldBe([StepState.Ran]);
        States(third, 1).ShouldBe([StepState.Kept], "the step after it has its own result, and needs nothing before it");
        split.Runs.ShouldBe(2);
        add.Runs.ShouldBe(1);
        File.Exists(half).ShouldBeTrue();
    }

    [Fact]
    public void ACatalogRefusesAnOperationWhoseExtraResultsHaveNowhereToGo()
    {
        Should.Throw<ArgumentException>(() => new OperationCatalog([new SaysExtraOperation()])).Message.ShouldContain("'says-extra'");
        new OperationCatalog([new SplitOperation()]).TryGet("split", out var split).ShouldBeTrue();
        split.ShouldBeAssignableTo<IMultiResultOperation>();
    }
}
