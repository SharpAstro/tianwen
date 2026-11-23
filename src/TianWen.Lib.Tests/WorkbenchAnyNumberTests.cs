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
/// A stored operation over a SET of images: every pixel is the sum of them all plus the amount. As "odd" it gives the
/// first image it was handed, and only when it was handed an odd number of them. It counts its runs.
/// </summary>
internal sealed class SumOperation : MultiResultOperation<AddSettings>
{
    public const string Odd = "odd";

    public int Runs { get; private set; }

    public override OperationDescription Description { get; } = new(
        "sum", Version: 1, Title: "Sum", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [],
        Settings: [new OperationSetting("amount", "What is added to every pixel of the sum.")])
    {
        AnyNumber = true,
        ExtraResults = [new OperationResultRole(Odd, "The first image handed in, where an odd number were.", Optional: true)],
    };

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, StepOutputs outputs)
        => [Description.Id, .. inputs, "-o", outputs.Path, .. outputs.Extra.Values];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, StepOutputs outputs, Action<string>? say,
        CancellationToken cancellationToken)
    {
        Runs++;
        var read = new List<Image>(inputs.Count);
        foreach (var input in inputs)
        {
            if (!input.TryRead(out var image, out _, out var refusal))
            {
                return ValueTask.FromResult(OperationOutcome.BadInput(refusal));
            }

            read.Add(image);
        }

        var (width, height) = (read[0].Width, read[0].Height);
        var sum = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                sum[y, x] = settings.Amount + read.Sum(image => image[0, y, x]);
            }
        }

        OperationOutput.Write(new Image([sum], BitDepth.Float32, maxValue: 100f, minValue: 0f, pedestal: 0f, imageMeta: read[0].ImageMeta), outputs.Path, null);
        if (inputs.Count % 2 == 1 && outputs.Extra.TryGetValue(Odd, out var place))
        {
            OperationOutput.Write(read[0], place, null);
        }

        return ValueTask.FromResult(OperationOutcome.Done);
    }
}

/// <summary>An operation that says it takes any number of images and names one it takes too: a catalog refuses it.</summary>
internal sealed class SetAndNamedOperation : Operation<AddSettings>
{
    public override OperationDescription Description { get; } = new(
        "set-and-named", Version: 1, Title: "Says", Verb: "none", OperationOutputKind.Stored,
        ExtraInputs: [new OperationInputRole("other", "An image by its place.")], Settings: [])
    {
        AnyNumber = true,
    };

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output) => [Description.Id];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(OperationOutcome.Done);
}

/// <summary>
/// An operation that takes any number of images as a set (#2468, for <c>planetary compose</c>): one step whichever edit
/// list holds it and in whatever order it names the others, and a result the step may leave out.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchAnyNumberTests : IDisposable
{
    private const float Tolerance = 1e-5f;

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static JsonObject Amount(int amount) => new() { ["amount"] = amount };

    [Fact(Timeout = 120_000)]
    public async Task ASetOfImagesIsOneStepWhicheverListHoldsItAndInWhateverOrderItNamesTheOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("set-").FullName;
        var x = WorkbenchFixtures.WriteMaster(folder, "x", WorkbenchFixtures.Master(seed: 1));
        var y = WorkbenchFixtures.WriteMaster(folder, "y", WorkbenchFixtures.Master(seed: 2));
        var z = WorkbenchFixtures.WriteMaster(folder, "z", WorkbenchFixtures.Master(seed: 3));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("x", x),
            WorkbenchFixtures.Picture("y", y),
            WorkbenchFixtures.Picture("z", z),
            // The same three, held by two lists that start from different ones and name the others in other orders.
            WorkbenchFixtures.Starting("a", "x", WorkbenchFixtures.Edit("sum", Amount(1), "y", "z")),
            WorkbenchFixtures.Starting("b", "z", WorkbenchFixtures.Edit("sum", Amount(1), "x", "y")),
            // Three is odd, so the step gives its extra result too.
            WorkbenchFixtures.StartingFromResult("first", "a", SumOperation.Odd)), ct);
        var sum = new SumOperation();
        var runner = new WorkbenchRunner(new OperationCatalog([sum]));

        var report = await runner.RunAsync(folder, cancellationToken: ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        ImageReport Of(string name) => report.Images.Single(image => image.Name == name);
        var (a, b) = (Of("a").Steps[0], Of("b").Steps[0]);
        b.Key.ShouldBe(a.Key, "one set of images and one setting is one step, whoever holds it");
        (a.State, b.State).ShouldBe((StepState.Ran, StepState.Kept));
        sum.Runs.ShouldBe(1);
        b.Output.ShouldBe(a.Output);
        // And one command line: the images in one order, whichever list prints it.
        b.CommandLine.ToArray().ShouldBe(a.CommandLine.ToArray());
        a.CommandLine.Count(static word => word.EndsWith(".fits", StringComparison.Ordinal)).ShouldBe(5, "three images, the sum and where its extra result would go");
        var expected = WorkbenchFixtures.Read(x)[0, 40, 50] + WorkbenchFixtures.Read(y)[0, 40, 50] + WorkbenchFixtures.Read(z)[0, 40, 50] + 1f;
        WorkbenchFixtures.Read(a.Output.ShouldNotBeNull())[0, 40, 50].ShouldBe(expected, Tolerance);
        Of("first").Refusal.ShouldBeNull();
        File.Exists(Path.Combine(folder, StepStore.FolderName, StepStore.FileNameOf(Of("first").Key))).ShouldBeTrue("the result it may leave out was given, and kept");

        var again = await runner.RunAsync(folder, cancellationToken: ct);

        again.Succeeded.ShouldBeTrue();
        sum.Runs.ShouldBe(1);
    }

    [Fact(Timeout = 120_000)]
    public async Task AResultAStepMayLeaveOutIsNotMissedAndAnImageThatStartsFromItSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("left-out-").FullName;
        var x = WorkbenchFixtures.WriteMaster(folder, "x", WorkbenchFixtures.Master(seed: 1));
        var y = WorkbenchFixtures.WriteMaster(folder, "y", WorkbenchFixtures.Master(seed: 2));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("x", x),
            WorkbenchFixtures.Picture("y", y),
            // Two is even: the step gives its sum and leaves its extra result out.
            WorkbenchFixtures.Starting("pair", "x", WorkbenchFixtures.Edit("sum", null, "y")),
            WorkbenchFixtures.StartingFromResult("first", "pair", SumOperation.Odd),
            // An image handed in twice is no set.
            WorkbenchFixtures.Starting("twice", "x", WorkbenchFixtures.Edit("sum", null, "x"))), ct);
        var sum = new SumOperation();
        var runner = new WorkbenchRunner(new OperationCatalog([sum]));

        var report = await runner.RunAsync(folder, cancellationToken: ct);

        ImageReport Of(WorkbenchReport of, string name) => of.Images.Single(image => image.Name == name);
        Of(report, "pair").Steps[0].State.ShouldBe(StepState.Ran);
        Of(report, "first").Refusal.ShouldNotBeNull().ShouldContain("gave no 'odd'");
        Of(report, "twice").Steps[0].State.ShouldBe(StepState.Refused);
        Of(report, "twice").Steps[0].Message.ShouldNotBeNull().ShouldContain("handed one image twice");
        report.Succeeded.ShouldBeFalse();
        sum.Runs.ShouldBe(1);
        File.Exists(Path.Combine(folder, StepStore.FolderName, StepStore.FileNameOf(Of(report, "first").Key))).ShouldBeFalse();

        // A status reads the same off the store: the step is there, whole, and the result is not.
        var status = await runner.StatusAsync(folder, cancellationToken: ct);
        var again = await runner.RunAsync(folder, cancellationToken: ct);

        Of(status, "pair").Steps[0].State.ShouldBe(StepState.Kept);
        Of(status, "first").Refusal.ShouldNotBeNull().ShouldContain("gave no 'odd'");
        Of(again, "pair").Steps[0].State.ShouldBe(StepState.Kept, "a step is whole in the store without the result it may leave out");
        sum.Runs.ShouldBe(1);
        Of(again, "first").Refusal.ShouldNotBeNull().ShouldContain("gave no 'odd'");
    }

    [Fact]
    public void ACatalogRefusesAnOperationThatIsASetAndNamesAnImageByItsPlace()
    {
        Should.Throw<ArgumentException>(() => new OperationCatalog([new SetAndNamedOperation()])).Message.ShouldContain("'set-and-named'");
        new OperationCatalog([new SumOperation()]).TryGet("sum", out var sum).ShouldBeTrue();
        sum.ShouldNotBeNull().Description.AnyNumber.ShouldBeTrue();
    }
}
