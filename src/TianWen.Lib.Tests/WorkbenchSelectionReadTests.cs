using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Workbench;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The reader half of #2554: every reader of the shown image's steps takes the view and the selection once and indexes what it
/// checked. Each test puts a stale selection on the state as a frame could find it (past the end, -1, a frame group) and asks every
/// reader: each gives its no-step answer, none throws, and the frame paints.
/// </summary>
[Collection("Viewer")]
public sealed class WorkbenchSelectionReadTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private async Task<string> ThreeStepProjectAsync()
    {
        var folder = _folders.Create("steps-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        var project = WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master,
            WorkbenchFixtures.Edit("add", new JsonObject { ["amount"] = 1f }),
            WorkbenchFixtures.Edit("add", new JsonObject { ["amount"] = 2f }),
            WorkbenchFixtures.Edit("add", new JsonObject { ["amount"] = 3f })));
        await WorkbenchProjectFile.WriteAsync(folder, project, TestContext.Current.CancellationToken);
        return folder;
    }

    private static WorkbenchE2E Start()
    {
        var catalog = new OperationCatalog([new AddOperation("add")]);
        return WorkbenchE2E.Start(1f, new WorkbenchRunner(catalog), catalog);
    }

    private static void EveryReaderHasNoStepAnswer(WorkbenchE2E e2e)
    {
        var actions = e2e.Actions;
        Should.NotThrow(() =>
        {
            actions.Panel().ShouldBeNull();
            actions.SelectedStepTitle.ShouldBeNull();
            actions.ShowsEmptyState.ShouldBeFalse();
            actions.SettingControls().ShouldBeEmpty();
            actions.ViewOffer().ResultFile.ShouldBeNull();
            actions.ViewOffer().InputFile.ShouldBeNull();
            actions.RunToHereOffer().CanRun.ShouldBeFalse();
            actions.EmptyStateWords().ShouldBe("Select a step to see its output.");
            actions.Wanted();
            actions.Gives().ShouldBeEmpty();
            actions.RequestPlan();
            actions.SetStepEnabled(false);
            actions.RemoveSelected();
        });
        e2e.Frame();
    }

    [Theory(Timeout = 300_000)]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(-1)]
    public async Task AStaleSelectionGetsTheNoStepAnswerFromEveryReader(int selected)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ThreeStepProjectAsync();
        await using var e2e = Start();
        await e2e.OpenProjectAsync(folder, ct);
        e2e.State.View!.Image!.Steps.Length.ShouldBe(3);

        e2e.State.SelectedStep = selected;
        EveryReaderHasNoStepAnswer(e2e);
        await e2e.Tracker.DrainAsync();
    }

    [Fact(Timeout = 300_000)]
    public async Task AFrameGroupSelectedGivesNoStepTitleAndNoEmptyState()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ThreeStepProjectAsync();
        await using var e2e = Start();
        await e2e.OpenProjectAsync(folder, ct);

        e2e.State.SelectedStep = 1;
        e2e.State.SelectedFrames = "Lights";
        Should.NotThrow(() =>
        {
            e2e.Actions.SelectedStepTitle.ShouldBeNull();
            e2e.Actions.ShowsEmptyState.ShouldBeFalse();
            e2e.Actions.Wanted();
            e2e.Actions.RunToHereOffer();
            e2e.Actions.EmptyStateWords();
        });
        e2e.Frame();
    }

    [Fact(Timeout = 300_000)]
    public async Task AValidSelectionGivesTheTitleAndThePanelTheSameStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = await ThreeStepProjectAsync();
        await using var e2e = Start();
        await e2e.OpenProjectAsync(folder, ct);

        for (var index = 0; index < 3; index++)
        {
            e2e.State.SelectedStep = index;
            var panel = e2e.Actions.Panel();
            panel.ShouldNotBeNull();
            e2e.Actions.SelectedStepTitle.ShouldBe(panel.Step.Title);
            e2e.Actions.SettingControls().ShouldNotBeEmpty();
        }

        e2e.Frame();
    }
}
