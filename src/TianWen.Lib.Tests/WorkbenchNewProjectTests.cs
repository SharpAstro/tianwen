using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Workbench;
using TianWen.Lib.IO;
using Xunit;
using Scaling = TianWen.Lib.Tests.WorkbenchEnhanceParityTests.Scaling;

namespace TianWen.Lib.Tests;

/// <summary>
/// A stand-in for the stack: an operation with the stack's id whose results are named by its run. It gives one small
/// master for each frame at the folder's top, named as the frame is.
/// </summary>
internal sealed class FakeStackOperation : RunNamedOperation<AddSettings>
{
    public override OperationDescription Description { get; } = new(
        StackOperation.Id, Version: 1, Title: "Stack", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [], Settings: [])
    {
        NamedByRun = true,
        TakesFrameSet = true,
    };

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output)
        => [Description.Id, inputs[0], "-o", output];

    public override ValueTask<NamedResults> RunIntoAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string folder, Action<string>? say,
        CancellationToken cancellationToken)
    {
        var results = ImmutableArray.CreateBuilder<NamedResult>();
        var seed = 1;
        foreach (var frame in FileEnumeration.EnumerateFiles(inputs[0].Path ?? string.Empty, ".fits", recursive: false).Order(StringComparer.Ordinal))
        {
            var master = Path.Combine(folder, $"master_{seed}.fits");
            OperationOutput.Write(WorkbenchFixtures.Master(seed++), master, null);
            results.Add(new NamedResult(Path.GetFileNameWithoutExtension(frame), master, []));
        }

        return ValueTask.FromResult(new NamedResults(OperationOutcome.Done, results.ToImmutable()));
    }
}

/// <summary>
/// A project made from a capture folder (#2547): the Lib routine and the verb that call it, the names of the masters a kept
/// stack step gives, and "process this master", which adds the enhance's chain of single steps starting from one.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchNewProjectTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private string Capture(params string[] frames)
    {
        var folder = Path.Combine(_folders.Create("capture-").FullName, "night-1");
        Directory.CreateDirectory(folder);
        foreach (var frame in frames)
        {
            File.WriteAllBytes(Path.Combine(folder, frame + ".fits"), new byte[2880]);
        }

        return folder;
    }

    private static (OperationCatalog Catalog, WorkbenchRunner Runner, EnhanceOperation Enhance) Registered()
    {
        var (remover, denoiser) = (new Scaling("Test/Remover", 0.5f), new Scaling("Test/Denoiser", 0.8f));
        var enhance = new EnhanceOperation(new SharpenPipeline(starRemover: remover, denoiser: denoiser));
        var catalog = new OperationCatalog(
            [new FakeStackOperation(), enhance, new RemoveStarsOperation(remover), new DenoiseOperation(denoiser), new AddStarsOperation()]);
        return (catalog, new WorkbenchRunner(catalog), enhance);
    }

    private static (Command Command, List<string> Said) WorkbenchVerb(OperationCatalog catalog, WorkbenchRunner runner)
    {
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        return (new WorkbenchSubCommand(host, catalog, runner).Build(), said);
    }

    [Fact(Timeout = 60_000)]
    public async Task ANewProjectHoldsTheCaptureAndItsStackAtTheDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = Capture("a");
        var folder = Path.Combine(_folders.Create("project-").FullName, "made");

        var made = await WorkbenchProjects.NewAsync(folder, frames, ct);

        made.Refusal.ShouldBeNull();
        var read = (await WorkbenchProjectFile.ReadAsync(folder, ct)).Project.ShouldNotBeNull();
        var image = read.Images.ShouldHaveSingleItem();
        image.Name.ShouldBe("night-1");
        image.Path.ShouldBe(Path.GetFullPath(frames));
        image.From.ShouldBeNull();
        var edit = image.Edits.ShouldHaveSingleItem();
        edit.Operation.ShouldBe(StackOperation.Id);
        edit.Enabled.ShouldBeTrue();
        edit.Settings.ShouldBeNull();
        edit.Inputs.ShouldBeEmpty();
    }

    [Fact(Timeout = 60_000)]
    public async Task ANewProjectIsRefusedInWords()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = Capture("a");
        var project = _folders.Create("project-").FullName;

        (await WorkbenchProjects.NewAsync(project, Path.Combine(frames, "nothing"), ct)).Refusal.ShouldNotBeNull().ShouldContain("does not exist");
        (await WorkbenchProjects.NewAsync(frames, frames, ct)).Refusal.ShouldNotBeNull().ShouldContain("is the project's folder");

        var store = Directory.CreateDirectory(Path.Combine(project, StepStore.FolderName, "stack")).FullName;
        (await WorkbenchProjects.NewAsync(project, store, ct)).Refusal.ShouldNotBeNull().ShouldContain("inside the project's step store");
        File.Exists(WorkbenchProjectFile.PathIn(project)).ShouldBeFalse("a refusal writes nothing");

        (await WorkbenchProjects.NewAsync(project, frames, ct)).Refusal.ShouldBeNull();
        var again = await WorkbenchProjects.NewAsync(project, frames, ct);
        again.Refusal.ShouldNotBeNull().ShouldContain("already holds a project.json");
        again.Project.ShouldBeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task ANewProjectInsideTheCaptureFolderIsRefusedSinceTheStackWouldReadItsFilesAsFrames()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = Capture("a");
        var inside = Path.Combine(frames, "project");

        var refused = await WorkbenchProjects.NewAsync(inside, frames, ct);

        refused.Refusal.ShouldNotBeNull().ShouldContain("is inside the capture folder");
        File.Exists(WorkbenchProjectFile.PathIn(inside)).ShouldBeFalse("a refusal writes nothing");
        Directory.Exists(inside).ShouldBeFalse("a refusal makes no folder");
    }

    [Fact(Timeout = 600_000)]
    public async Task ImageWithNoStackStepGivesNoMastersAndTwoPressesAtOnceKeepBothChains()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = _folders.Create("both-").FullName;
        await WorkbenchProjects.NewAsync(project, Capture("m31_L", "m31_R"), ct);
        var (_, runner, enhance) = Registered();
        (await runner.RunAsync(project, cancellationToken: ct)).Succeeded.ShouldBeTrue();

        // Two presses that read the same project before either wrote: neither drops the other's images.
        var both = await Task.WhenAll(
            WorkbenchProjects.AddMasterChainAsync(project, runner, enhance, "night-1", "m31_L", null, null, ct),
            WorkbenchProjects.AddMasterChainAsync(project, runner, enhance, "night-1", "m31_R", null, null, ct));

        both[0].Refusal.ShouldBeNull();
        both[1].Refusal.ShouldBeNull();
        var names = (await WorkbenchProjectFile.ReadAsync(project, ct)).Project.ShouldNotBeNull().Images.Select(static image => image.Name).ToArray();
        names.ShouldBe(["night-1", .. both[0].Added.Select(static image => image.Name), .. both[1].Added.Select(static image => image.Name)], ignoreOrder: true);

        // An image the chain made starts from a master and has no stack step of its own: it gives no masters.
        var noStack = await WorkbenchProjects.AddMasterChainAsync(project, runner, enhance, both[0].Added[0].Name, "m31_L", null, null, ct);
        noStack.Refusal.ShouldNotBeNull().ShouldContain("has no stack step, so it gives no masters");
        noStack.Added.ShouldBeEmpty();
    }

    [Fact(Timeout = 60_000)]
    public async Task TheNewVerbWritesWhatTheRoutineWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = Capture("a");
        var (catalog, runner, _) = Registered();
        var (command, said) = WorkbenchVerb(catalog, runner);
        var byVerb = Path.Combine(_folders.Create("verb-").FullName, "p");
        var byRoutine = Path.Combine(_folders.Create("routine-").FullName, "p");

        (await command.Parse(["new", byVerb, frames]).InvokeAsync(cancellationToken: ct)).ShouldBe(0, string.Join("\n", said));
        await WorkbenchProjects.NewAsync(byRoutine, frames, ct);

        File.ReadAllText(WorkbenchProjectFile.PathIn(byVerb)).ShouldBe(File.ReadAllText(WorkbenchProjectFile.PathIn(byRoutine)));
        said.Clear();
        (await command.Parse(["new", byVerb, frames]).InvokeAsync(cancellationToken: ct)).ShouldBe(1);
        said.ShouldContain(line => line.Contains("already holds a project.json"));
        said.Clear();
        (await command.Parse(["new", _folders.Create("other-").FullName, Path.Combine(frames, "nothing")]).InvokeAsync(cancellationToken: ct)).ShouldBe(1);
        said.ShouldContain(line => line.Contains("does not exist"));
    }

    [Fact(Timeout = 120_000)]
    public async Task AKeptNamedByRunStepReportsItsResultNamesAndOtherStepsNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = _folders.Create("results-").FullName;
        await WorkbenchProjects.NewAsync(project, Capture("m31_L", "m31_R"), ct);
        var (_, runner, _) = Registered();

        var before = await runner.StatusAsync(project, cancellationToken: ct);
        before.Images[0].Steps[0].Results.ShouldBeEmpty("a step not made names nothing");

        var ran = await runner.RunAsync(project, cancellationToken: ct);
        ran.Succeeded.ShouldBeTrue(ran.Refusal);
        ran.Images[0].Steps[0].Results.ToArray().ShouldBe(["m31_L", "m31_R"]);

        var kept = await runner.StatusAsync(project, cancellationToken: ct);
        kept.Images[0].Steps[0].State.ShouldBe(StepState.Kept);
        kept.Images[0].Steps[0].Results.ToArray().ShouldBe(["m31_L", "m31_R"]);
    }

    [Fact(Timeout = 600_000)]
    public async Task ProcessingAMasterAddsTheChainFromItsGroup()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = _folders.Create("process-").FullName;
        await WorkbenchProjects.NewAsync(project, Capture("m31_L", "m31_R"), ct);
        var (catalog, runner, enhance) = Registered();

        // Before the stack ran: refused in words.
        var unrun = await WorkbenchProjects.AddMasterChainAsync(project, runner, enhance, "night-1", "m31_L", null, null, ct);
        unrun.Refusal.ShouldNotBeNull().ShouldContain("has not been run");
        unrun.Added.ShouldBeEmpty();

        (await runner.RunAsync(project, cancellationToken: ct)).Succeeded.ShouldBeTrue();

        // A group the stack did not give: refused, naming the groups it gave.
        var unknown = await WorkbenchProjects.AddMasterChainAsync(project, runner, enhance, "night-1", "m31_Ha", null, null, ct);
        unknown.Refusal.ShouldNotBeNull().ShouldContain("gave no 'm31_Ha'");
        unknown.Refusal.ShouldContain("m31_L, m31_R");

        // An image with no stack step gives no masters.
        (await WorkbenchProjects.AddMasterChainAsync(project, runner, enhance, "nothing", "m31_L", null, null, ct)).Refusal.ShouldNotBeNull().ShouldContain("no image named");

        var added = await WorkbenchProjects.AddMasterChainAsync(project, runner, enhance, "night-1", "m31_L", null, null, ct);

        added.Refusal.ShouldBeNull();
        added.Added.ShouldNotBeEmpty();
        var starting = added.Added[0];
        starting.From.ShouldBe("night-1");
        starting.Result.ShouldBe("m31_L");
        starting.Name.ShouldStartWith("night-1-m31_L");
        added.Added.Select(static image => image.Name).ShouldContain("night-1-m31_L");
        var read = (await WorkbenchProjectFile.ReadAsync(project, ct)).Project.ShouldNotBeNull();
        read.Images.Select(static image => image.Name).ShouldBe(["night-1", .. added.Added.Select(static image => image.Name)]);

        // Again: the default name is made unique; a name given that is taken is refused.
        var second = await WorkbenchProjects.AddMasterChainAsync(project, runner, enhance, "night-1", "m31_L", null, null, ct);
        second.Refusal.ShouldBeNull();
        second.Added.Select(static image => image.Name).ShouldContain("night-1-m31_L-2");
        var taken = await WorkbenchProjects.AddMasterChainAsync(project, runner, enhance, "night-1", "m31_R", "night-1-m31_L", null, ct);
        taken.Refusal.ShouldNotBeNull().ShouldContain("already has an image named");

        // The verb adds what the routine adds.
        var (command, said) = WorkbenchVerb(catalog, runner);

        // The status names the masters a kept stack step gives, so a group is known before a refusal teaches it.
        (await command.Parse(["status", project]).InvokeAsync(cancellationToken: ct)).ShouldBe(0, string.Join("\n", said));
        said.ShouldContain(line => line.Contains("masters: m31_L, m31_R"));
        said.Clear();
        (await command.Parse(["process", project, "night-1", "m31_R", "--name", "right"]).InvokeAsync(cancellationToken: ct)).ShouldBe(0, string.Join("\n", said));
        read = (await WorkbenchProjectFile.ReadAsync(project, ct)).Project.ShouldNotBeNull();
        var right = read.Images.Single(static image => image.Name == "right-starless");
        (right.From, right.Result).ShouldBe(("night-1", "m31_R"));
        said.Clear();
        (await command.Parse(["process", project, "night-1", "nothing"]).InvokeAsync(cancellationToken: ct)).ShouldBe(1);
        said.ShouldContain(line => line.Contains("it gave m31_L, m31_R"));
    }
}
