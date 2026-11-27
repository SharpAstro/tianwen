using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Workbench;
using TianWen.UI.Abstractions;
using Xunit;
using Scaling = TianWen.Lib.Tests.WorkbenchEnhanceParityTests.Scaling;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>tianwen-edit</c>'s masters list (#2547): once the stack has run, the stack step's panel lists the masters the runner named
/// (one per group), each with "Process this master", which adds the enhance's chain of single steps through the Lib routine and shows
/// the chain's result. The stack is a fake that is named by its run and writes small masters; the enhance is the real one. Driven
/// through the real host at DPI 1 and 1.5.
/// </summary>
[Collection("Viewer")]
public sealed class WorkbenchMastersE2ETests : IDisposable
{
    private const string Process = "Process this master";

    private readonly TempFolders _folders = new();
    private readonly GateOperation _gate = new();

    public void Dispose() => _folders.Dispose();

    private (OperationCatalog Catalog, WorkbenchRunner Runner) Registered()
    {
        var (remover, denoiser) = (new Scaling("Test/Remover", 0.5f), new Scaling("Test/Denoiser", 0.8f));
        var enhance = new EnhanceOperation(new SharpenPipeline(starRemover: remover, denoiser: denoiser));
        var catalog = new OperationCatalog(
            [new FakeStackOperation(), _gate, enhance, new RemoveStarsOperation(remover), new DenoiseOperation(denoiser), new AddStarsOperation()]);
        return (catalog, new WorkbenchRunner(catalog));
    }

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

    // A project made from a capture of these groups, its stack run by the runner: the masters are in the store and kept.
    private async Task<string> StackedProjectAsync(WorkbenchRunner runner, params string[] groups)
    {
        var ct = TestContext.Current.CancellationToken;
        var project = _folders.Create("project-").FullName;
        (await WorkbenchProjects.NewAsync(project, Capture(groups), ct)).Refusal.ShouldBeNull();
        (await runner.RunAsync(project, cancellationToken: ct)).Succeeded.ShouldBeTrue();
        return project;
    }

    // The Process this master buttons of the last frame, top to bottom, as a click finds them.
    private static ImmutableArray<ClickableRegion> Buttons(WorkbenchE2E e2e)
        => [.. e2e.Texts().Where(static t => t.Text == Process).OrderBy(static t => t.Bounds.Y).Select(t =>
        {
            var (cx, cy) = (t.Bounds.X + (t.Bounds.Width / 2f), t.Bounds.Y + (t.Bounds.Height / 2f));
            return e2e.Regions().Single(r => r.Result is HitResult.ButtonHit && cx >= r.X && cx <= r.X + r.Width && cy >= r.Y && cy <= r.Y + r.Height);
        })];

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task BeforeTheStackRanThePanelListsNoMastersAndSaysTheyComeOnceItRuns(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var project = _folders.Create("unrun-").FullName;
        (await WorkbenchProjects.NewAsync(project, Capture("m31_L", "m31_R"), ct)).Refusal.ShouldBeNull();
        await using var e2e = WorkbenchE2E.Start(dpi, runner, catalog);

        await e2e.OpenProjectAsync(project, ct);

        e2e.State.SelectedStep.ShouldBe(0);
        var panel = string.Join(' ', e2e.PanelText().Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        panel.ShouldContain("The masters come once the stack has run");
        panel.ShouldNotContain("m31_L");
        e2e.Texts().ShouldNotContain(static t => t.Text == Process, "no master, no button");
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AfterTheStackEachMasterIsOneRowByItsGroupNameWithProcessThisMaster(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var project = await StackedProjectAsync(runner, "m31_L", "m31_R", "m31_Ha");
        await using var e2e = WorkbenchE2E.Start(dpi, runner, catalog);

        await e2e.OpenProjectAsync(project, ct);

        var panel = e2e.Actions.Panel().ShouldNotBeNull();
        panel.Masters.ToArray().ShouldBe(["m31_Ha", "m31_L", "m31_R"], "the names the runner answered, in its order");
        var buttons = Buttons(e2e);
        buttons.Length.ShouldBe(3, "one button for each master");
        var names = e2e.Texts().Where(static t => t.Text is "m31_Ha" or "m31_L" or "m31_R").OrderBy(static t => t.Bounds.Y).ToArray();
        names.Select(static t => t.Text).ToArray().ShouldBe(panel.Masters.ToArray(), "one row for each, in order");
        for (var i = 0; i < 3; i++)
        {
            buttons[i].Height.ShouldBe(buttons[0].Height, 0.5f, "rows of one height");
            (names[i].Bounds.Y + (names[i].Bounds.Height / 2f)).ShouldBe(buttons[i].Y + (buttons[i].Height / 2f), 1.5f, "the name and its button share one middle");
            (names[i].Bounds.X + names[i].Bounds.Width).ShouldBeLessThanOrEqualTo(buttons[i].X + 0.5f, "the name ends before its button");
        }

        string.Join(' ', e2e.PanelText().Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)).ShouldNotContain("The masters come once", Case.Sensitive);
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task APressAddsTheChainThroughTheLibRoutineAndShowsItsResultSelected(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var project = await StackedProjectAsync(runner, "m31_L", "m31_R");
        var reference = await StackedProjectAsync(runner, "m31_L", "m31_R");
        var routine = await WorkbenchProjects.AddMasterChainAsync(reference, runner, (EnhanceOperation)catalog.All.Single(static o => o.Description.Id == EnhanceOperation.Id),
            "night-1", "m31_R", null, null, ct);
        routine.Refusal.ShouldBeNull();
        await using var e2e = WorkbenchE2E.Start(dpi, runner, catalog);
        await e2e.OpenProjectAsync(project, ct);

        e2e.Click(Buttons(e2e)[1]);
        await e2e.PumpUntilAsync(() => e2e.State.View is { ImageName: not "night-1" } && !e2e.State.IsReading, "the chain's result on show", ct);

        var written = (await WorkbenchProjectFile.ReadAsync(project, ct)).Project.ShouldNotBeNull();
        written.Images.Select(static i => i.Name).ToArray().ShouldBe(["night-1", .. routine.Added.Select(static i => i.Name)], "what the Lib routine adds");
        var starting = written.Images[1];
        (starting.From, starting.Result).ShouldBe(("night-1", "m31_R"));
        written.Images.Skip(1).Select(static i => i.Edits.Select(static e => e.Operation).ToArray())
            .ShouldBe(routine.Added.Select(static i => i.Edits.Select(static e => e.Operation).ToArray()), "its edits are the chain's single steps");
        e2e.State.View!.ImageName.ShouldBe(routine.Added[^1].Name, "the chain's result is the image on show");
        e2e.State.View.ImageNames.ShouldContain(routine.Added[^1].Name);
        e2e.State.MasterRefusal.ShouldBeNull();
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ASecondPressForTheSameGroupIsNamedUniquely(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var project = await StackedProjectAsync(runner, "m31_L");
        await using var e2e = WorkbenchE2E.Start(dpi, runner, catalog);
        await e2e.OpenProjectAsync(project, ct);
        e2e.Click(Buttons(e2e).Single());
        await e2e.PumpUntilAsync(() => e2e.State.View is { ImageName: not "night-1" } && !e2e.State.IsReading, "the first chain on show", ct);
        var first = e2e.State.View!.ImageName!;

        e2e.Actions.ChooseImage("night-1");
        await e2e.PumpUntilAsync(() => e2e.State.View is { ImageName: "night-1" } && !e2e.State.IsReading, "the stack image on show", ct);
        e2e.State.SelectedStep.ShouldBe(0);
        e2e.Click(Buttons(e2e).Single());
        await e2e.PumpUntilAsync(() => e2e.State.View is { ImageName: not "night-1" } && !e2e.State.IsReading, "the second chain on show", ct);

        var second = e2e.State.View!.ImageName!;
        second.ShouldNotBe(first);
        second.ShouldEndWith("-2", Case.Sensitive, "the routine's unique name");
        e2e.State.MasterRefusal.ShouldBeNull();
        var written = (await WorkbenchProjectFile.ReadAsync(project, ct)).Project.ShouldNotBeNull();
        written.Images.Select(static i => i.Name).ShouldContain(first);
        written.Images.Select(static i => i.Name).ShouldContain(second);
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AMasterTheStoreHasLostIsRefusedInWordsBesideTheListAndAddsNothing(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var project = await StackedProjectAsync(runner, "m31_L");
        await using var e2e = WorkbenchE2E.Start(dpi, runner, catalog);
        await e2e.OpenProjectAsync(project, ct);
        // The window's answer is stale: the master is lost from the store after it was read.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(project, StepStore.FolderName), "*.fits", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        e2e.Click(Buttons(e2e).Single());
        await e2e.PumpUntilAsync(() => e2e.State.MasterRefusal is not null && !e2e.State.IsProcessingMaster, "the refusal", ct);

        // With the files gone the runner no longer reports the stack as kept, so the routine says to run it first.
        e2e.State.MasterRefusal.ShouldNotBeNull().ShouldContain("has not been run: run it first");
        e2e.State.View!.ImageName.ShouldBe("night-1", "nothing was opened");
        string.Join(' ', e2e.PanelText().Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)).ShouldContain("was not processed", Case.Sensitive, "the refusal is shown in words beside the list");
        (await WorkbenchProjectFile.ReadAsync(project, ct)).Project.ShouldNotBeNull().Images.Length.ShouldBe(1, "nothing was added");
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task WhileAPressLaysOutItsChainNoOtherProjectOrImageCanBeChosen(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var project = await StackedProjectAsync(runner, "m31_L");
        var other = await StackedProjectAsync(runner, "m31_R");
        await using var e2e = WorkbenchE2E.Start(dpi, runner, catalog);
        await e2e.OpenProjectAsync(project, ct);
        e2e.Click(Buttons(e2e).Single());
        await e2e.PumpUntilAsync(() => e2e.State.View is { ImageName: not "night-1" } && !e2e.State.IsReading, "the chain on show", ct);
        var shown = e2e.State.View!.ImageName!;

        // The press's flag is held (as it is while the routine runs): a choice made meanwhile is refused, not landed on.
        e2e.State.IsProcessingMaster = true;
        e2e.Actions.ChoiceWaits.ShouldBe(WorkbenchActions.ChoiceWaitsForTheChain);
        e2e.Frame();
        // Open project, New project and the image box are dimmed with the chain's reason on hover, not the run's.
        e2e.RegionOfLabel("Open project").Tooltip.ShouldBe(WorkbenchActions.ChoiceWaitsForTheChain);
        e2e.RegionOfLabel("New project").Tooltip.ShouldBe(WorkbenchActions.ChoiceWaitsForTheChain);
        e2e.Regions().ShouldContain(r => r.Tooltip == $"{shown}. {WorkbenchActions.ChoiceWaitsForTheChain}", "the image box says why, its whole name first");
        var pendingBefore = e2e.Tracker.PendingCount;
        e2e.Actions.ChooseProject();
        e2e.Actions.NewProject();
        e2e.Tracker.PendingCount.ShouldBe(pendingBefore, "neither press started a dialog");
        e2e.Actions.OpenProject(other).ShouldBeFalse();
        e2e.Actions.ChooseImage("night-1");
        e2e.State.IsReading.ShouldBeFalse("nothing was asked of the runner");
        e2e.State.View!.ImageName.ShouldBe(shown);
        e2e.State.View.Folder.ShouldBe(Path.GetFullPath(project));

        e2e.State.IsProcessingMaster = false;
        e2e.Actions.ChoiceWaits.ShouldBeNull();
        e2e.Actions.OpenProject(other).ShouldBeTrue("offered again once the press ended");
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AChainMadeWhileARunStartsIsNotOpenedAndSaysSoInWordsLeavingTheStackStepSelected(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var project = await StackedProjectAsync(runner, "m31_L");
        // A second image whose one step waits at the gate: a run of it starts after the press passed its guard.
        var master = WorkbenchFixtures.WriteMaster(project, "master", WorkbenchFixtures.Master(seed: 1));
        var read = (await WorkbenchProjectFile.ReadAsync(project, ct)).Project.ShouldNotBeNull();
        await WorkbenchProjectFile.WriteAsync(project, read with { Images = [.. read.Images, WorkbenchFixtures.Picture("other", master, WorkbenchFixtures.Edit("gate"))] }, ct);
        await using var e2e = WorkbenchE2E.Start(dpi, runner, catalog);
        await e2e.OpenProjectAsync(project, ct, "night-1");
        var button = Buttons(e2e).ShouldHaveSingleItem();

        // The press is let through (no run yet) and its routine goes off the render thread; the run starts at once, before the
        // routine can finish its file work, and holds at its gate, so the chain's own open meets a run that is going.
        e2e.Click(button);
        e2e.Actions.Jobs.TryStart(e2e.State.View!.Folder, "other", 0).ShouldBeTrue();
        using var diag = CancellationTokenSource.CreateLinkedTokenSource(ct);
        diag.CancelAfter(20_000);
        try
        {
            await e2e.PumpFramesUntilAsync(() => e2e.State.MasterRefusal is not null && !e2e.State.IsProcessingMaster, "the refusal", diag.Token);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"DIAG image={e2e.State.View!.ImageName} processing={e2e.State.IsProcessingMaster} running={e2e.Actions.Jobs.IsRunning} refusal={e2e.State.MasterRefusal} pending={string.Join('|', e2e.Tracker.PendingDescriptions)} images={(await WorkbenchProjectFile.ReadAsync(project, ct)).Project?.Images.Length}");
        }

        var refusal = e2e.State.MasterRefusal.ShouldNotBeNull();
        refusal.ShouldStartWith("m31_L was processed but its image was not opened: ");
        refusal.ShouldContain(WorkbenchActions.ChoiceWaitsForTheRun, Case.Sensitive);
        refusal.ShouldContain("Choose it from the image box.", Case.Sensitive);
        e2e.State.View!.ImageName.ShouldBe("night-1", "the image on show is still the stack's");
        e2e.State.SelectedStep.ShouldBe(0, "the stack step stays selected so its panel and the refusal stay on screen");
        (await WorkbenchProjectFile.ReadAsync(project, ct)).Project.ShouldNotBeNull().Images.Length.ShouldBeGreaterThan(2, "the chain was made on disk");

        _gate.Release.SetResult();
        await e2e.PumpUntilAsync(() => !e2e.Actions.Jobs.IsRunning, "the run to end", ct);
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheButtonsAreDimmedWithTheirReasonWhileARunGoes(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (catalog, runner) = Registered();
        var project = await StackedProjectAsync(runner, "m31_L");
        // A second image whose one step waits at the gate: its run is the run that is going while the stack image is on show.
        var master = WorkbenchFixtures.WriteMaster(project, "master", WorkbenchFixtures.Master(seed: 1));
        var read = (await WorkbenchProjectFile.ReadAsync(project, ct)).Project.ShouldNotBeNull();
        await WorkbenchProjectFile.WriteAsync(project, read with { Images = [.. read.Images, WorkbenchFixtures.Picture("other", master, WorkbenchFixtures.Edit("gate"))] }, ct);
        await using var e2e = WorkbenchE2E.Start(dpi, runner, catalog);
        await e2e.OpenProjectAsync(project, ct, "night-1");
        Buttons(e2e).Length.ShouldBe(1);

        e2e.Actions.Jobs.TryStart(e2e.State.View!.Folder, "other", 0).ShouldBeTrue();
        await e2e.PumpFramesUntilAsync(() => _gate.Started.Task.IsCompleted, "the step at its gate", ct);

        var button = Buttons(e2e).ShouldHaveSingleItem();
        button.Tooltip.ShouldNotBeNull().ShouldContain(WorkbenchActions.ChoiceWaitsForTheRun, Case.Sensitive, "the dimmed button says why on hover");
        e2e.Actions.ProcessMasterWaits.ShouldBe(WorkbenchActions.ChoiceWaitsForTheRun);
        e2e.Actions.ProcessMaster("m31_L").ShouldBeFalse("refused while a run goes");
        e2e.Click(button);
        (await WorkbenchProjectFile.ReadAsync(project, ct)).Project.ShouldNotBeNull().Images.Length.ShouldBe(2, "a press on a dimmed button adds nothing");

        _gate.Release.SetResult();
        await e2e.PumpUntilAsync(() => !e2e.Actions.Jobs.IsRunning, "the run to end", ct);
        e2e.Actions.ProcessMasterWaits.ShouldBeNull("offered again once the run ended");
    }
}
