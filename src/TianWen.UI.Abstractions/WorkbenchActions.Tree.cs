using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.UI.Abstractions;

/// <summary>One row of the project tree: a step of some image, or a frame group, with the runner's state for it.</summary>
/// <param name="Row">The Lib's row (kind, depth, image, edit index, label, detail).</param>
/// <param name="State">The runner's state for the step; null for a row that is not a step.</param>
/// <param name="Mark">The mark the state is drawn with.</param>
/// <param name="Words">The state in words, for the hover.</param>
/// <param name="Selected">True for the row the selection is on.</param>
/// <param name="Number">The step's number in its image, from 1; 0 for a row that is not a step.</param>
public sealed record WorkbenchTreeRow(WorkflowRow Row, StepState? State, string Mark, string Words, bool Selected, int Number)
{
    /// <summary>True where a ▶ is offered: a step that can be made.</summary>
    public bool Runs => State is not null && State is not (StepState.Refused or StepState.Blocked or StepState.Off);
}

// The project tree (#2553 section 3): the frames, the workflow as the Lib draws it, a ▶ on each step that runs every step up to it, and
// the steps added, removed and switched through the Lib's routines. Every mark is the runner's answer, never a click.
public sealed partial class WorkbenchActions
{
    private const string FramesSlot = "workbench.frames";
    private WorkbenchProject? _treeFor;
    private ImmutableArray<WorkflowRow> _tree = [];

    /// <summary>The mark a state is drawn with in the tree: a tick made, a turn stale, a circle not made, a warning refused.</summary>
    public static string TreeMarkFor(StepState state, bool changed) => state switch
    {
        StepState.Kept or StepState.Ran => "✓",
        StepState.WillRun => changed ? "↻" : "○",
        StepState.Refused or StepState.Blocked => "⚠",
        StepState.Off => "–",
        StepState.Derived => "∘",
        _ => "·",
    };

    /// <summary>The workflow's name: the built-in workflow the project was made from, or Steps for one with none.</summary>
    public string WorkflowTitle
        => state.View?.Project?.Workflow is { } id && BuiltInWorkflows.Find(id) is { } template ? template.Title : "Steps";

    /// <summary>The project's rows as the tree draws them, each step in the runner's state.</summary>
    public ImmutableArray<WorkbenchTreeRow> Tree()
    {
        if (state.View is not { Project: { } project } view)
        {
            return [];
        }

        if (!ReferenceEquals(_treeFor, project))
        {
            _tree = WorkbenchWorkflows.Tree(project, catalog);
            _treeFor = project;
        }

        var rows = ImmutableArray.CreateBuilder<WorkbenchTreeRow>(_tree.Length);
        foreach (var row in _tree)
        {
            if (row.EditIndex < 0)
            {
                rows.Add(new WorkbenchTreeRow(row, null, string.Empty, string.Empty, false, 0));
                continue;
            }

            var current = string.Equals(row.Image, view.ImageName, StringComparison.Ordinal);
            StepReport? step = null;
            if (current && view.Image is { } shown && row.EditIndex < shown.Steps.Length)
            {
                step = StepAnswer(shown.Steps[row.EditIndex]);
            }
            else if (view.Reports.FirstOrDefault(r => string.Equals(r.Name, row.Image, StringComparison.Ordinal)) is { } report && row.EditIndex < report.Steps.Length)
            {
                step = report.Steps[row.EditIndex];
            }

            var selected = current && state.SelectedFrames is null && row.EditIndex == state.SelectedStep;
            rows.Add(step is null
                ? new WorkbenchTreeRow(row, null, "·", string.Empty, selected, row.EditIndex + 1)
                : new WorkbenchTreeRow(row, step.State, TreeMarkFor(step.State, step.ChangedSinceLastRun),
                    StepStates.Words(step.State), selected, row.EditIndex + 1));
        }

        return rows.MoveToImmutable();
    }

    /// <summary>How many steps of the whole project are made, and how many there are: "9 of 13 made".</summary>
    public (int Made, int Total) MadeCount()
    {
        var steps = state.View?.Reports.SelectMany(static r => r.Steps).ToList() ?? [];
        return (steps.Count(static s => s.State is StepState.Kept or StepState.Ran), steps.Count);
    }

    /// <summary>
    /// Selects a step of the tree. A step of the image on show is selected at once; one of another image opens that image with the step
    /// selected. Nothing runs and no state changes: a mark is the runner's answer.
    /// </summary>
    public void SelectTreeRow(string image, int editIndex)
    {
        state.SelectedFrames = null;
        if (state.View is not { } view)
        {
            return;
        }

        if (string.Equals(view.ImageName, image, StringComparison.Ordinal))
        {
            SelectStep(editIndex);
            state.NeedsRedraw = true;
            return;
        }

        if (ChoiceWaits is not null)
        {
            return;
        }

        // Opening another image: its status is read, then the step is selected (SelectAfterStatus).
        state.SelectedStep = -1;
        state.SelectAfterStatus = editIndex;
        OpenProject(view.Folder, image);
    }

    /// <summary>
    /// The ▶ of a row: selects the step and runs every step up to it, the kept ones kept and the stale and missing ones first, as the
    /// toolbar's button does. A row of another image opens that image first and runs when its status is in.
    /// </summary>
    public void RunToRow(string image, int editIndex)
    {
        if (_jobs.IsRunning || state.View is not { } view)
        {
            return;
        }

        if (string.Equals(view.ImageName, image, StringComparison.Ordinal))
        {
            SelectStep(editIndex);
            if (view.Image is { } shown && editIndex < shown.Steps.Length && shown.Steps[editIndex].State is not (StepState.Refused or StepState.Blocked or StepState.Off))
            {
                _jobs.TryStart(view.Folder, image, editIndex);
            }

            return;
        }

        if (ChoiceWaits is not null)
        {
            return;
        }

        state.RunAfterStatus = true;
        SelectTreeRow(image, editIndex);
    }

    /// <summary>Selects a frame group of the tree (Lights, Darks, Flats or Bias); its files are listed in the panel.</summary>
    public void SelectFrames(string name)
    {
        state.SelectedFrames = name;
        state.NeedsRedraw = true;
    }

    /// <summary>The selected frame group; null while a step is selected.</summary>
    public WorkbenchFrameGroup? SelectedFrameGroup
        => state.SelectedFrames is { } name ? state.Frames?.Groups.FirstOrDefault(g => g.Name == name) : null;

    /// <summary>The capture the project's frames are in: the folder above the project's <c>workdir</c>; null for a project made elsewhere.</summary>
    private static string? CaptureFolderOf(WorkbenchView view)
    {
        var folder = view.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(folder), "workdir", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(folder) : null;
    }

    // Reads what the capture folder holds once for each project shown, off the render thread, for the tree's Frames section.
    private void RequestFrames(WorkbenchView view)
    {
        if (CaptureFolderOf(view) is not { } capture || string.Equals(state.Frames?.Folder, capture, StringComparison.Ordinal))
        {
            return;
        }

        tracker.RunExclusive<WorkbenchFrames>(FramesSlot, async ct =>
            {
                var detected = await CaptureDetection.DetectAsync(capture, new CaptureDetectionOptions
                {
                    Reassigned = await WorkbenchProjects.ReassignmentsAsync(capture, ct),
                    GradeLights = false,
                }, ct);
                ImmutableArray<string> Of(params DetectedGroupKind[] kinds)
                    => [.. detected.Groups.Where(g => kinds.Contains(g.Kind)).SelectMany(static g => g.Files).Select(static f => f.Place)];
                return new WorkbenchFrames(capture,
                [
                    new("Lights", Of(DetectedGroupKind.Lights)),
                    new("Darks", Of(DetectedGroupKind.Darks)),
                    new("Flats", Of(DetectedGroupKind.Flats)),
                    new("Bias", Of(DetectedGroupKind.Bias)),
                ]);
            }, appToken, logger, "Read what the capture folder holds",
            onError: _ => state.Frames = new WorkbenchFrames(capture, []),
            onFinally: () => state.NeedsRedraw = true);
    }

    // Takes the frames answer once it is in, between frames.
    private void TryAdoptFrames()
    {
        if (tracker.TryCollect<WorkbenchFrames>(FramesSlot, out var frames) && frames is not null)
        {
            state.Frames = frames;
            state.NeedsRedraw = true;
        }
    }

    /// <summary>The operations a "+ Step" can add: those that take no image beyond the one the steps go on from.</summary>
    public ImmutableArray<(string Id, string Title)> AddableSteps()
        => [.. catalog.All.Where(static o => o.Description.ExtraInputs.All(static role => role.Optional))
            .Select(static o => (o.Description.Id, o.Description.Title)).OrderBy(static t => t.Title, StringComparer.Ordinal)];

    /// <summary>Adds a step of the operation after the selected one, through <see cref="WorkbenchWorkflows.AddStepAsync"/>; the new step is then selected.</summary>
    public void AddStepAfterSelected(string operationId)
    {
        if (_jobs.IsRunning || state.View is not { ImageName: { } image } view)
        {
            return;
        }

        var after = state.SelectedStep >= 0 ? state.SelectedStep : (view.Image?.Steps.Length ?? 0) - 1;
        state.TreeRefusal = null;
        tracker.RunGuarded(async ct =>
        {
            var result = await WorkbenchWorkflows.AddStepAsync(view.Folder, catalog, image, after, operationId, null, ct);
            if (result.Refusal is { } refusal)
            {
                state.TreeRefusal = $"The step was not added: {refusal}";
                return;
            }

            state.SelectAfterStatus = after + 1;
            state.SelectedFrames = null;
            OpenProjectNow(view.Folder, image);
        }, appToken, logger, "Add a step to the workbench project",
            onError: ex => state.TreeRefusal = $"The step was not added: {ex.Message}",
            onFinally: () => state.NeedsRedraw = true);
    }

    /// <summary>Removes the selected step, through <see cref="WorkbenchWorkflows.RemoveStepAsync"/>; the step before it is then selected.</summary>
    public void RemoveSelected()
    {
        if (_jobs.IsRunning || state.View is not { ImageName: { } image } view || state.SelectedStep < 0 || state.SelectedFrames is not null)
        {
            return;
        }

        var index = state.SelectedStep;
        state.TreeRefusal = null;
        tracker.RunGuarded(async ct =>
        {
            var result = await WorkbenchWorkflows.RemoveStepAsync(view.Folder, catalog, image, index, ct);
            if (result.Refusal is { } refusal)
            {
                state.TreeRefusal = $"The step was not removed: {refusal}";
                return;
            }

            state.SelectAfterStatus = Math.Max(0, index - 1);
            OpenProjectNow(view.Folder, image);
        }, appToken, logger, "Remove a step from the workbench project",
            onError: ex => state.TreeRefusal = $"The step was not removed: {ex.Message}",
            onFinally: () => state.NeedsRedraw = true);
    }

    /// <summary>The words that ask before a workflow replaces the person's changes to the steps.</summary>
    public static string SwitchWords(int changes)
        => $"Your {(changes == 1 ? "1 change" : $"{changes} changes")} to the steps {(changes == 1 ? "is" : "are")} replaced; results already made are kept.";

    /// <summary>
    /// Switches the workflow of the project. Asks first (<see cref="WorkbenchState.PendingSwitch"/>) only when the steps were changed by
    /// hand, counted by <see cref="WorkbenchWorkflows.HandChanges"/>; otherwise applies it at once.
    /// </summary>
    public void SwitchWorkflow(string id)
    {
        if (_jobs.IsRunning || state.View is not { Project: { } project } view || string.Equals(project.Workflow, id, StringComparison.Ordinal))
        {
            return;
        }

        var changes = WorkbenchWorkflows.HandChanges(project);
        if (changes > 0)
        {
            state.PendingSwitch = new WorkbenchSwitch(id, changes);
            state.NeedsRedraw = true;
            return;
        }

        ApplyWorkflow(view, id);
    }

    /// <summary>The person agreed to replace their changes: applies the workflow they chose.</summary>
    public void ConfirmSwitch()
    {
        if (state.PendingSwitch is { } pending && state.View is { } view)
        {
            state.PendingSwitch = null;
            ApplyWorkflow(view, pending.Id);
        }
    }

    /// <summary>The person kept their steps: nothing is replaced.</summary>
    public void CancelSwitch()
    {
        state.PendingSwitch = null;
        state.NeedsRedraw = true;
    }

    // The Lib's apply routine, with the facts of the capture read afresh (the person's reassignments included), then the project is read again.
    private void ApplyWorkflow(WorkbenchView view, string id)
    {
        var capture = CaptureFolderOf(view);
        if (capture is null)
        {
            state.TreeRefusal = "This project was not made from a capture folder, so there are no frames to fit a workflow to.";
            state.NeedsRedraw = true;
            return;
        }

        state.TreeRefusal = null;
        tracker.RunGuarded(async ct =>
        {
            var detected = await CaptureDetection.DetectAsync(capture, new CaptureDetectionOptions
            {
                Reassigned = await WorkbenchProjects.ReassignmentsAsync(capture, ct),
                GradeLights = false,
            }, ct);
            var applied = await WorkbenchWorkflows.ApplyAsync(capture, id, WorkflowFacts.From(detected), ct);
            if (applied.Refusal is { } refusal)
            {
                state.TreeRefusal = $"The workflow was not used: {refusal}";
                return;
            }

            state.SelectedStep = -1;
            state.SelectedFrames = null;
            OpenProjectNow(view.Folder, null);
        }, appToken, logger, "Switch the workflow of the workbench project",
            onError: ex => state.TreeRefusal = $"The workflow was not used: {ex.Message}",
            onFinally: () => state.NeedsRedraw = true);
    }

    /// <summary>Opens or closes the log under the status strip.</summary>
    public void ToggleLog()
    {
        state.LogOpen = !state.LogOpen;
        state.NeedsRedraw = true;
    }

    /// <summary>The selected step's title for the toolbar's button; null where no step is selected.</summary>
    public string? SelectedStepTitle
        => state.View is { Image: { } image } && state.SelectedFrames is null && state.SelectedStep >= 0 && state.SelectedStep < image.Steps.Length
            ? Steps()[state.SelectedStep].Title
            : null;

    /// <summary>
    /// True when the canvas has nothing to draw for the picture chosen: Output or Before and after for a step that has made no output. The
    /// screen then says so in words (<see cref="EmptyStateWords"/>) and offers the step's ▶.
    /// </summary>
    public bool ShowsEmptyState
        => state.View is { Image: not null } && state.SelectedFrames is null && SelectedStepTitle is not null
            && state.ChosenView is WorkbenchViewKind.Result or WorkbenchViewKind.Comparison
            && ViewOffer().ResultFile is null;

    /// <summary>What running the selected step will do and what is kept, in words: "Runs Remove stars, then Denoise. Steps 1 to 5 are kept."</summary>
    public string EmptyStateWords()
    {
        var steps = Steps();
        var selected = state.SelectedStep;
        if (selected < 0 || selected >= steps.Length)
        {
            return "Select a step to see its output.";
        }

        var step = steps[selected];
        if (step.State is StepState.Refused or StepState.Blocked)
        {
            return step.Message is { Length: > 0 } message ? $"{step.Words}: {message}" : step.Words;
        }

        if (step.State is StepState.Off)
        {
            return "This step is switched off, so it makes nothing; the steps after it take its input.";
        }

        if (state.Plan is not { } plan || plan.Index != selected)
        {
            return "Reading what a run to this step needs.";
        }

        var runs = steps.Take(selected + 1).Where(static s => s.State is StepState.WillRun).Select(static s => s.Title).ToList();
        var kept = steps.Take(selected).Where(static s => s.State is StepState.Kept or StepState.Ran).Select(static s => s.Index + 1).ToArray();
        var text = runs.Count == 0
            ? "Nothing needs to run: the step is kept, but it has no picture of its own."
            : $"Runs {JoinWords(runs)}.";
        return kept.Length == 0 ? text : $"{text} {(kept.Length == 1 ? "Step" : "Steps")} {Ranges(kept)} {(kept.Length == 1 ? "is" : "are")} kept.";
    }

    private static string JoinWords(List<string> titles)
        => titles.Count == 1 ? titles[0] : $"{string.Join(", ", titles.Take(titles.Count - 1))}, then {titles[^1]}";

    /// <summary>The last run and where it is, for the status strip: its words, the fraction where the operation reports one, and the outcome.</summary>
    public (string Words, double? Fraction, bool Failed) LastRun()
    {
        var job = _jobs.State;
        if (!job.Running && job.Outcome is WorkbenchJobOutcome.None)
        {
            return ("No run yet", null, false);
        }

        return (job.Words, job.Running ? job.Fraction : null, !job.Running && job.Outcome is WorkbenchJobOutcome.Stopped or WorkbenchJobOutcome.Failed);
    }
}
