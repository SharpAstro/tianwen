using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Making one image only up to a chosen step (<see cref="WorkbenchRunOptions.Through"/>; #1642): a status says which
/// steps up to it will run and which are kept and none after it will run, and a run makes that step's result, from the
/// store where the keys are there, and runs nothing after it.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchRunToStepTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly AddOperation _add = new("add");

    public void Dispose() => _folders.Dispose();

    private static JsonObject Amount(float amount) => new() { ["amount"] = amount };

    private static StepState[] States(WorkbenchReport report) => [.. report.Images[0].Steps.Select(static step => step.State)];

    private WorkbenchRunner Runner() => new(new OperationCatalog([_add]));

    // An image of five adds, each by a different amount, so every step has a key of its own.
    private async Task<string> FiveStepsAsync(string name, params ProjectEdit[] replacing)
    {
        var folder = _folders.Create(name + "-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        ProjectEdit[] edits = replacing.Length > 0
            ? replacing
            : [.. Enumerable.Range(1, 5).Select(n => WorkbenchFixtures.Edit("add", Amount(n)))];
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master, edits)), TestContext.Current.CancellationToken);
        return folder;
    }

    [Fact(Timeout = 120_000)]
    public async Task AStatusToStepThreeOfFiveSaysOneToThreeWillRunAndFourAndFiveDoNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await FiveStepsAsync("status");

        var status = await Runner().StatusAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 2 }, ct);

        status.Refusal.ShouldBeNull();
        States(status).ShouldBe([StepState.WillRun, StepState.WillRun, StepState.WillRun, StepState.NotNeeded, StepState.NotNeeded]);
        _add.Runs.ShouldBe(0, "a status runs nothing");
    }

    [Fact(Timeout = 120_000)]
    public async Task ARunToStepThreeMakesOneToThreeOnlyAndTheNextStatusSaysThemKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await FiveStepsAsync("run");
        var runner = Runner();

        var run = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 2 }, ct);

        run.Succeeded.ShouldBeTrue(run.Refusal);
        States(run).ShouldBe([StepState.Ran, StepState.Ran, StepState.Ran, StepState.NotNeeded, StepState.NotNeeded]);
        _add.Runs.ShouldBe(3);

        var through = await runner.StatusAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 2 }, ct);
        var whole = await runner.StatusAsync(folder, new WorkbenchRunOptions { Image = "m" }, ct);

        States(through).ShouldBe([StepState.Kept, StepState.Kept, StepState.Kept, StepState.NotNeeded, StepState.NotNeeded]);
        States(whole).ShouldBe([StepState.Kept, StepState.Kept, StepState.Kept, StepState.WillRun, StepState.WillRun], "the whole image still has two steps to make");
    }

    [Fact(Timeout = 120_000)]
    public async Task AfterStepTwosSettingChangesARunToThreeRunsTwoAndThreeOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await FiveStepsAsync("changed");
        var runner = Runner();
        await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "m" }, ct);
        _add.Runs.ShouldBe(5);

        var read = await WorkbenchProjectFile.ReadAsync(folder, ct);
        var project = read.Project.ShouldNotBeNull(read.Refusal);
        var image = project.Images[0];
        await WorkbenchProjectFile.WriteAsync(folder, project with { Images = [image with { Edits = image.Edits.SetItem(1, image.Edits[1] with { Settings = Amount(20) }) }] }, ct);

        var run = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 2 }, ct);

        States(run).ShouldBe([StepState.Kept, StepState.Ran, StepState.Ran, StepState.NotNeeded, StepState.NotNeeded]);
        _add.Runs.ShouldBe(7, "steps 2 and 3 ran again and nothing else");
    }

    [Fact(Timeout = 120_000)]
    public async Task ARefusedStepBeforeTheTargetBlocksItWithTheReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await FiveStepsAsync("refused",
            WorkbenchFixtures.Edit("add", Amount(1)), WorkbenchFixtures.Edit("no-such-operation"), WorkbenchFixtures.Edit("add", Amount(3)));

        var run = await Runner().RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 2 }, ct);

        run.Succeeded.ShouldBeFalse();
        States(run).ShouldBe([StepState.Ran, StepState.Refused, StepState.Blocked]);
        run.Images[0].Steps[1].Message.ShouldNotBeNull().ShouldContain("no-such-operation");
        run.Images[0].Steps[2].Message.ShouldNotBeNull().ShouldContain("cannot be made");
        _add.Runs.ShouldBe(1, "what is before the refused step was made");
    }

    [Fact(Timeout = 120_000)]
    public async Task AStepOutOfRangeOrSwitchedOffIsRefusedInWords()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await FiveStepsAsync("range");
        var runner = Runner();

        var tooFar = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 5 }, ct);
        var zero = await runner.StatusAsync(folder, new WorkbenchRunOptions { Image = "m", Through = -1 }, ct);
        var noImage = await runner.RunAsync(folder, new WorkbenchRunOptions { Through = 1 }, ct);
        var exporting = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 1, ExportPath = _folders.Create("out-").FullName + "/m.fits" }, ct);

        tooFar.Refusal.ShouldNotBeNull().ShouldContain("no step 6");
        zero.Refusal.ShouldNotBeNull().ShouldContain("no step 0");
        noImage.Refusal.ShouldNotBeNull().ShouldContain("needs the image named");
        exporting.Refusal.ShouldNotBeNull().ShouldContain("export");
        _add.Runs.ShouldBe(0);

        var read = await WorkbenchProjectFile.ReadAsync(folder, ct);
        var project = read.Project.ShouldNotBeNull(read.Refusal);
        var image = project.Images[0];
        await WorkbenchProjectFile.WriteAsync(folder, project with { Images = [image with { Edits = image.Edits.SetItem(2, image.Edits[2] with { Enabled = false }) }] }, ct);

        var off = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = "m", Through = 2 }, ct);

        off.Refusal.ShouldNotBeNull().ShouldContain("switched off");
        _add.Runs.ShouldBe(0, "a refused target makes nothing");
    }

    [Fact(Timeout = 120_000)]
    public async Task TheWorkbenchVerbTakesToThroughTheSamePathAndRefusesABadNumber()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await FiveStepsAsync("verb");
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var catalog = new OperationCatalog([_add]);
        var workbench = new WorkbenchSubCommand(host, catalog, new WorkbenchRunner(catalog)).Build();

        async Task<(int Exit, string Said)> InvokeAsync(params string[] args)
        {
            said.Clear();
            var exit = await workbench.Parse(args).InvokeAsync(cancellationToken: ct);
            var text = string.Join(Environment.NewLine, said);
            output.WriteLine($"> workbench {string.Join(' ', args)} (exit {exit})");
            output.WriteLine(text);
            return (exit, text);
        }

        var status = await InvokeAsync("status", folder, "--image", "m", "--to", "3");
        status.Exit.ShouldBe(0);
        status.Said.ShouldContain("3 add: will run");
        status.Said.ShouldContain("4 add: not in the store, and not needed");

        var run = await InvokeAsync("run", folder, "--image", "m", "--to", "3");
        run.Exit.ShouldBe(0);
        run.Said.ShouldContain("3 add: ran");
        run.Said.ShouldNotContain("4 add: ran");
        _add.Runs.ShouldBe(3);

        var bad = await InvokeAsync("run", folder, "--image", "m", "--to", "6");
        bad.Exit.ShouldBe(1, "a refused target is the runner's refusal, as the others");
        bad.Said.ShouldContain("no step 6");
        (await InvokeAsync("status", folder, "--to", "2")).Said.ShouldContain("needs the image named");
    }
}
