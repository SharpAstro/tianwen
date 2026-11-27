using System;
using System.IO;
using System.Linq;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.UI.Abstractions;

/// <summary>One step of the step bar: what to draw, taken from the runner's report and the operation's own description.</summary>
/// <param name="Index">The step's place in the edit list, from 0.</param>
/// <param name="Title">The operation's title (its id for an operation this computer does not know).</param>
/// <param name="State">The runner's state for the step.</param>
/// <param name="Words">The state in words (<see cref="StepStates.Words"/>).</param>
/// <param name="Mark">The state's mark: a tick for a step that is kept.</param>
/// <param name="Changed">True when the project recorded another key for the step since a run last made it.</param>
/// <param name="Message">Why the step was refused or blocked; null otherwise.</param>
public sealed record WorkbenchStepItem(int Index, string Title, StepState State, string Words, string Mark, bool Changed, string? Message)
{
    /// <summary>The step's number and title as the bar and the panel show them.</summary>
    public string Label => $"{Index + 1}  {Title}";

    /// <summary>The step's whole text for a hover, which the bar may have cut short.</summary>
    public string Hover => $"{Label}: {Words}{(Message is { Length: > 0 } ? $". {Message}" : string.Empty)}"
        + (Changed ? ". Changed since the last run." : string.Empty);
}

/// <summary>One setting shown in a step's panel.</summary>
/// <param name="Name">The setting's name in the settings JSON.</param>
/// <param name="Value">Its value as the operation reads it (the canonical settings, so a default shows as its value).</param>
/// <param name="Summary">What it does (<see cref="OperationSetting.Summary"/>); empty where the operation does not describe it.</param>
public sealed record WorkbenchSettingItem(string Name, string Value, string Summary);

/// <summary>What the selected step's panel shows.</summary>
public sealed record WorkbenchStepPanel(
    WorkbenchStepItem Step,
    string Summary,
    ImmutableArray<WorkbenchSettingItem> Settings,
    string? SettingsRefusal,
    string ServedBy,
    ImmutableArray<string> CommandLine)
{
    /// <summary>
    /// For the stack step, the name of each master it gave (one per target and filter group), as the runner answered; empty
    /// before it ran. Always empty for any other step. <see cref="ShowsMasters"/> says whether the panel has a list at all.
    /// </summary>
    public ImmutableArray<string> Masters { get; init; } = [];

    /// <summary>True for the stack step: its panel lists its masters, or says they come once it has run.</summary>
    public bool ShowsMasters { get; init; }

    /// <summary>The command as a person reads and pastes it.</summary>
    public string CommandText => StepCommandLine.Display(CommandLine);
}

/// <summary>
/// What the processing app's window does, away from its widgets: opening a project and asking the runner for a status off the
/// render thread, choosing an image and a step, and putting a step's command line on the clipboard. The window's handlers
/// route here and do nothing themselves.
/// </summary>
/// <remarks>
/// A step's state is only ever what the runner answered: a press selects a step and starts no run, and the answer replaces
/// <see cref="WorkbenchState.View"/> whole on the render thread, on a frame of its choosing.
/// </remarks>
public sealed class WorkbenchActions(
    WorkbenchState state,
    WorkbenchRunner runner,
    OperationCatalog catalog,
    BackgroundTaskTracker tracker,
    IFileDialogs dialogs,
    Action<string> setClipboardText,
    ILogger logger,
    CancellationToken appToken)
{
    private const string StatusSlot = "workbench.status";
    private const string PlanSlot = "workbench.plan";

    /// <summary>Why "Process this master" is not offered while the project is being read.</summary>
    public const string MasterWaitsForTheRead = "The project is being read: a master can be processed when the answer is in";

    /// <summary>Why "Process this master" is not offered while an earlier press is still laying out its chain.</summary>
    public const string MasterWaitsForTheChain = "A master is being processed: another can be when its images are in the project";

    private readonly WorkbenchJobs _jobs = new(runner, catalog, tracker, logger, appToken,
        () => state.NeedsRedraw = true, settled => Settled(settled, state, runner, tracker, logger, appToken));

    /// <summary>The run on the tracker: its progress and lines, and the one place a run starts and is cancelled.</summary>
    public WorkbenchJobs Jobs => _jobs;

    // A run ended, however it did: the runner is asked afresh, so the bar shows its answer and never the click's.
    private static void Settled(WorkbenchJobSettled settled, WorkbenchState state, WorkbenchRunner runner,
        BackgroundTaskTracker tracker, ILogger logger, CancellationToken appToken)
    {
        // What the run left is read only when it is what is on show and no read of another answer is on its way: a read in
        // flight is the person's own choice, and the run's end must not supersede it through the one status slot.
        if (state.IsReading || state.View is not { } shown || !string.Equals(shown.Folder, settled.Folder, StringComparison.Ordinal)
            || !string.Equals(shown.ImageName, settled.Image, StringComparison.Ordinal))
        {
            state.NeedsRedraw = true;
            return;
        }

        state.SelectAfterStatus = settled.SelectStep;
        StartRead(state, runner, tracker, logger, appToken, settled.Folder, settled.Image, "Read the workbench project after a run");
    }

    // Asks the runner where every edit of the image stands, off the caller's thread: the folder (or its project.json) is resolved
    // there too, so no file is touched on the render thread. A newer read supersedes an older one through the one slot.
    private static void StartRead(WorkbenchState state, WorkbenchRunner runner, BackgroundTaskTracker tracker, ILogger logger,
        CancellationToken appToken, string folder, string? image, string description)
    {
        state.IsReading = true;
        state.NeedsRedraw = true;
        tracker.RunExclusive<WorkbenchView>(StatusSlot, ct => ReadViewAsync(runner, ProjectFolder(folder), image, ct),
            appToken, logger, description,
            onError: ex => state.View = new WorkbenchView(folder, null, [], null, null, $"The project could not be read: {ex.Message}"),
            onFinally: () => state.NeedsRedraw = true);
    }

    private static string ProjectFolder(string folder)
        => Path.GetFullPath(File.Exists(folder) ? Path.GetDirectoryName(folder) ?? folder : folder);

    /// <summary>Why the project and the image cannot be changed while a run goes; the screen shows it on the dimmed controls.</summary>
    public const string ChoiceWaitsForTheRun = "A run is going: another project or image can be chosen when it ends";

    /// <summary>
    /// Opens the project in <paramref name="folder"/> (or its <c>project.json</c>) and asks the runner where every edit of the
    /// image stands. Off the render thread, the path included; the answer is adopted by <see cref="TryAdoptStatus"/>. A newer
    /// call supersedes an older one that is still reading. False, and nothing asked, while a run goes: the run is writing the
    /// store, so no status is asked of it then, as no plan is (<see cref="RequestPlan"/>).
    /// </summary>
    public bool OpenProject(string folder, string? image = null)
    {
        if (_jobs.IsRunning)
        {
            state.NeedsRedraw = true;
            return false;
        }

        state.NewProjectRefusal = null;
        state.MasterRefusal = null;
        StartRead(state, runner, tracker, logger, appToken, folder, image, "Read the workbench project");
        return true;
    }

    /// <summary>
    /// Why "Process this master" cannot be pressed now, in words for the dimmed button's hover; null when it can. The stack step's
    /// masters are the runner's answer, so nothing is offered while a run writes the store or another answer is on its way.
    /// </summary>
    public string? ProcessMasterWaits
        => _jobs.IsRunning ? ChoiceWaitsForTheRun
            : state.IsReading ? MasterWaitsForTheRead
            : state.IsProcessingMaster ? MasterWaitsForTheChain
            : null;

    /// <summary>
    /// "Process this master": adds the image that starts from the master of <paramref name="group"/> that the image on show
    /// made, its edits the enhance laid out as its chain of single steps (<see cref="WorkbenchProjects.AddMasterChainAsync"/>), then
    /// opens the project on the chain's result. The routine runs off the render thread. False, and nothing asked, while
    /// <see cref="ProcessMasterWaits"/> says to wait; a refusal of the routine is kept in <see cref="WorkbenchState.MasterRefusal"/>
    /// and shown in words beside the list. A second press for a group the project already processed is named uniquely by the routine.
    /// </summary>
    public bool ProcessMaster(string group)
    {
        if (ProcessMasterWaits is not null || state.View is not { ImageName: { } stackImage } view)
        {
            state.NeedsRedraw = true;
            return false;
        }

        if (!catalog.TryGet(EnhanceOperation.Id, out var operation) || operation is not EnhanceOperation enhance)
        {
            state.MasterRefusal = $"{group} was not processed: this computer has no enhance operation to lay out";
            state.NeedsRedraw = true;
            return false;
        }

        var folder = view.Folder;
        state.MasterRefusal = null;
        state.IsProcessingMaster = true;
        state.NeedsRedraw = true;
        tracker.RunGuarded(async ct =>
        {
            var added = await WorkbenchProjects.AddMasterChainAsync(folder, runner, enhance, stackImage, group, name: null, say: null, ct);
            if (added.Refusal is { } refusal || added.Added.IsDefaultOrEmpty)
            {
                state.MasterRefusal = $"{group} was not processed: {added.Refusal ?? "the chain has no image"}";
                return;
            }

            // The chain's last image is its result (the starless and stars halves come before it); it is the one shown.
            state.SelectedStep = -1;
            if (!OpenProject(folder, added.Added[^1].Name))
            {
                logger.LogInformation("{Group} was processed but the new image was not opened: {Why}", group, ChoiceWaitsForTheRun);
            }
        }, appToken, logger, "Process a master of the workbench project",
            onError: ex => state.MasterRefusal = $"{group} was not processed: {ex.Message}",
            onFinally: () =>
            {
                state.IsProcessingMaster = false;
                state.NeedsRedraw = true;
            });
        return true;
    }

    /// <summary>The Open project button: a folder dialog, then <see cref="OpenProject"/>. Not offered while a run goes.</summary>
    public void ChooseProject()
    {
        if (_jobs.IsRunning)
        {
            return;
        }

        tracker.RunGuarded(async ct =>
        {
            var result = await dialogs.OpenFolderAsync(new FileDialogRequest
            {
                Title = "Open a workbench project",
                InitialDirectory = state.View?.Folder,
            }, ct);
            if (result.Path is { } picked && !OpenProject(picked))
            {
                logger.LogInformation("{Folder} was not opened: {Why}", picked, ChoiceWaitsForTheRun);
            }
        }, appToken, logger, "Choose a workbench project", onError: _ => state.NeedsRedraw = true);
    }

    /// <summary>
    /// The New project button: a folder dialog for the project's folder, one for the capture's folder, then the Lib routine
    /// (<see cref="WorkbenchProjects.NewAsync"/>) off the render thread and <see cref="OpenProject"/> on the folder it made. A
    /// cancelled dialog changes nothing; a refusal is kept in <see cref="WorkbenchState.NewProjectRefusal"/> and shown in words.
    /// Not offered while a run goes.
    /// </summary>
    public void NewProject()
    {
        if (_jobs.IsRunning)
        {
            return;
        }

        tracker.RunGuarded(async ct =>
        {
            var project = await dialogs.OpenFolderAsync(new FileDialogRequest
            {
                Title = "New project: choose the folder for the project",
                InitialDirectory = state.View?.Folder,
            }, ct);
            if (project.Path is not { } projectFolder)
            {
                return;
            }

            var capture = await dialogs.OpenFolderAsync(new FileDialogRequest
            {
                Title = "New project: choose the folder of the capture to stack",
                InitialDirectory = projectFolder,
            }, ct);
            if (capture.Path is not { } captureFolder)
            {
                return;
            }

            var made = await WorkbenchProjects.NewAsync(projectFolder, captureFolder, ct);
            if (made.Refusal is { } refusal)
            {
                state.NewProjectRefusal = $"No project was made: {refusal}";
                state.NeedsRedraw = true;
                return;
            }

            state.NewProjectRefusal = null;
            if (!OpenProject(projectFolder))
            {
                logger.LogInformation("{Folder} was made but not opened: {Why}", projectFolder, ChoiceWaitsForTheRun);
            }
        }, appToken, logger, "Make a workbench project from a capture folder", onError: _ => state.NeedsRedraw = true);
    }

    /// <summary>Shows another image of the project. Refused while a run goes, as <see cref="OpenProject"/> is.</summary>
    public void ChooseImage(string name)
    {
        if (_jobs.IsRunning || state.View is not { } view || string.Equals(view.ImageName, name, StringComparison.Ordinal))
        {
            return;
        }

        state.SelectedStep = -1;
        OpenProject(view.Folder, name);
    }

    /// <summary>Selects a step: its panel is drawn. Nothing runs and nothing about any step's state changes.</summary>
    public void SelectStep(int index)
    {
        if (state.SelectedStep != index)
        {
            state.SelectedStep = index;
            state.CommandCopied = false;
            state.NeedsRedraw = true;
            state.Plan = null;
            RequestPlan();
        }
    }

    /// <summary>
    /// Asks the runner, off the render thread, what a run to the selected step would do: a status through it. The latest selection
    /// wins (a newer ask cancels an older one, and an answer for a step no longer selected is dropped in <see cref="TryAdoptStatus"/>).
    /// Not asked while a run is going, which is writing the store.
    /// </summary>
    public void RequestPlan()
    {
        if (_jobs.IsRunning || state.View is not { Image: { } image, ImageName: { } name } view
            || state.SelectedStep < 0 || state.SelectedStep >= image.Steps.Length)
        {
            return;
        }

        var (folder, index) = (view.Folder, state.SelectedStep);
        tracker.RunExclusive<WorkbenchPlan>(PlanSlot, async ct =>
            {
                var report = await runner.StatusAsync(folder, new WorkbenchRunOptions { Image = name, Through = index }, ct);
                var answered = report.Images.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
                return answered is null || report.Refusal is not null || answered.Refusal is not null
                    ? null
                    : new WorkbenchPlan(folder, name, index, answered.Steps) { Existing = WorkbenchOriginal.ExistingOutputs(answered.Steps) };
            }, appToken, logger, "Plan a run to the selected step",
            onError: _ => state.Plan = null,
            onFinally: () => state.NeedsRedraw = true);
    }

    /// <summary>Starts a run that makes the selected step, taking each earlier step from the store where it is there. Nothing
    /// happens while a run is going or the step cannot run.</summary>
    public void RunToHere()
    {
        if (state.View is { ImageName: { } name } view && RunToHereOffer() is { CanRun: true } offer)
        {
            _jobs.TryStart(view.Folder, name, offer.Index);
        }
    }

    /// <summary>Starts the run the switch asks for: on, from the first step that is not kept to the end of the list; off, only
    /// that step, which is then selected.</summary>
    public void RunOn()
    {
        if (state.View is { ImageName: { } name } view && RunOnOffer() is { CanRun: true } offer)
        {
            _jobs.TryStart(view.Folder, name, state.RunOn ? null : offer.Index, selectAfter: state.RunOn ? -1 : offer.Index);
        }
    }

    /// <summary>Ends the run that is going.</summary>
    public void CancelRun() => _jobs.Cancel();

    /// <summary>Flips the switch "Run on until a step needs me".</summary>
    public void SetRunOn(bool on)
    {
        state.RunOn = on;
        state.NeedsRedraw = true;
    }

    /// <summary>Chooses the picture over the canvas.</summary>
    public void ChooseView(WorkbenchViewKind kind)
    {
        state.ChosenView = kind;
        state.NeedsRedraw = true;
    }

    /// <summary>What the "Run to here" button says it will do, from the runner's status through the selected step.</summary>
    public WorkbenchRunOffer RunToHereOffer()
    {
        var steps = Steps();
        var selected = state.SelectedStep;
        // A read in flight is another answer on its way (the view on show is the old one): a run then would start on the wrong image.
        if (_jobs.IsRunning || state.IsReading || selected < 0 || selected >= steps.Length)
        {
            return new WorkbenchRunOffer("Run to here", false, selected);
        }

        // A refused, blocked or off step has no plan to wait for (the runner refuses a status through it, so none is ever stored):
        // its own state already says it cannot run.
        var step = steps[selected];
        switch (step.State)
        {
            case StepState.Refused or StepState.Blocked:
                return new WorkbenchRunOffer($"Cannot run to here: the step is {step.Words}", false, selected);
            case StepState.Off:
                return new WorkbenchRunOffer("Cannot run to here: the step is off", false, selected);
        }

        if (state.Plan is not { } plan || plan.Index != selected)
        {
            return new WorkbenchRunOffer("Run to here (reading what it needs)", false, selected);
        }

        var first = steps.Take(selected).Where(static s => s.State is StepState.WillRun).Select(static s => s.Index + 1).ToArray();
        var ownRun = step.State is StepState.WillRun;
        if (first.Length == 0 && !ownRun)
        {
            return new WorkbenchRunOffer(step.State is StepState.Derived
                ? "Nothing to run: a derived step is made from its input when the step after it asks"
                : "Nothing to run: this step is kept", false, selected);
        }

        var label = first.Length == 0 ? "Run to here" : $"Run to here (runs {Ranges(first)} first)";
        return new WorkbenchRunOffer(label, true, selected);
    }

    /// <summary>What the "Run on" button says it will do: the first step that is not kept, and how far the switch takes the run.</summary>
    public WorkbenchRunOffer RunOnOffer()
    {
        var steps = Steps();
        if (_jobs.IsRunning || state.IsReading || steps.IsEmpty)
        {
            return new WorkbenchRunOffer(state.RunOn ? "Run on" : "Run next step", false, -1);
        }

        var next = steps.FirstOrDefault(static s => s.State is StepState.WillRun or StepState.Refused or StepState.Blocked);
        if (next is null)
        {
            return new WorkbenchRunOffer("Nothing to run: every step is kept", false, -1);
        }

        if (next.State is StepState.Refused or StepState.Blocked)
        {
            return new WorkbenchRunOffer($"Cannot run on: step {next.Index + 1} is {next.Words}", false, next.Index);
        }

        return new WorkbenchRunOffer(state.RunOn ? $"Run on from step {next.Index + 1}" : $"Run step {next.Index + 1}", true, next.Index);
    }

    // 1-based step numbers as a person says them: "2", "2 to 4", "2, 4 to 5".
    internal static string Ranges(int[] numbers)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (var i = 0; i < numbers.Length;)
        {
            var j = i;
            while (j + 1 < numbers.Length && numbers[j + 1] == numbers[j] + 1)
            {
                j++;
            }

            parts.Add(j == i ? $"{numbers[i]}" : $"{numbers[i]} to {numbers[j]}");
            i = j + 1;
        }

        return string.Join(", ", parts);
    }

    /// <summary>Puts the selected step's command line on the clipboard.</summary>
    public void CopyCommandLine()
    {
        if (Panel() is { CommandLine.IsEmpty: false } panel)
        {
            setClipboardText(panel.CommandText);
            state.CommandCopied = true;
            state.NeedsRedraw = true;
        }
    }

    /// <summary>
    /// Takes the runner's answer once it is in and puts it on show, between frames. Gives the viewer the image's original on
    /// the first answer for an image, and selects the first step where none is selected.
    /// </summary>
    public void TryAdoptStatus()
    {
        if (tracker.TryCollect<WorkbenchPlan>(PlanSlot, out var plan) && plan is not null && state.View is { } current
            && string.Equals(plan.Folder, current.Folder, StringComparison.Ordinal)
            && string.Equals(plan.Image, current.ImageName, StringComparison.Ordinal)
            && plan.Index == state.SelectedStep)
        {
            state.Plan = plan;
            state.NeedsRedraw = true;
        }

        if (!tracker.TryCollect<WorkbenchView>(StatusSlot, out var view) || view is null)
        {
            return;
        }

        var previous = state.View;
        state.View = view;
        state.IsReading = false;
        state.NeedsRedraw = true;
        state.Plan = null;

        var steps = view.Image?.Steps.Length ?? 0;
        if (state.SelectAfterStatus >= 0 && state.SelectAfterStatus < steps)
        {
            state.SelectedStep = state.SelectAfterStatus;
            state.CommandCopied = false;
        }
        else if (state.SelectedStep < 0 || state.SelectedStep >= steps || previous?.ImageName != view.ImageName)
        {
            state.SelectedStep = steps > 0 ? 0 : -1;
            state.CommandCopied = false;
        }

        state.SelectAfterStatus = -1;
        ShowOriginal(view);
        RequestPlan();
    }

    /// <summary>The step bar: each edit of the image, named by its operation's title, in the runner's state.</summary>
    public ImmutableArray<WorkbenchStepItem> Steps()
    {
        if (state.View is not { Image: { } image })
        {
            return [];
        }

        var items = ImmutableArray.CreateBuilder<WorkbenchStepItem>(image.Steps.Length);
        foreach (var step in image.Steps)
        {
            items.Add(ItemFor(StepAnswer(step)));
        }

        return items.MoveToImmutable();
    }

    /// <summary>The selected step's panel; null where no step is selected.</summary>
    public WorkbenchStepPanel? Panel()
    {
        if (state.View is not { Image: { } image } view || state.SelectedStep < 0 || state.SelectedStep >= image.Steps.Length)
        {
            return null;
        }

        var step = StepAnswer(image.Steps[state.SelectedStep]);
        var item = ItemFor(step);
        var description = catalog.TryGet(step.Operation, out var operation) ? operation.Description : null;
        var edit = view.ProjectImage is { } projectImage && step.Index < projectImage.Edits.Length ? projectImage.Edits[step.Index] : null;

        var settings = ImmutableArray<WorkbenchSettingItem>.Empty;
        string? settingsRefusal = null;
        if (operation is not null && edit is not null)
        {
            if (operation.TryReadSettings(edit.Settings?.ToJsonString(), out var canonical, out var refusal))
            {
                settings = SettingsOf(canonical, description!);
            }
            else
            {
                settingsRefusal = refusal;
            }
        }

        var isStack = string.Equals(step.Operation, StackOperation.Id, StringComparison.Ordinal);
        return new WorkbenchStepPanel(item, description?.Summary ?? string.Empty, settings, settingsRefusal, step.ServedBy, step.CommandLine)
        {
            // The names the runner answered with the step (StepReport.Results): never the store, never the disk on a frame.
            Masters = isStack ? step.Results : [],
            ShowsMasters = isStack,
        };
    }

    /// <summary>
    /// Which pictures the selected step can show beside the original, and in words why not where it cannot. A picture is a file of
    /// the step's own that the viewer can draw: a derived step, a step that writes no pixels (its stored output is a header-only
    /// finding), one whose results are named by its run, and one not made yet have none.
    /// </summary>
    public WorkbenchViewOffer ViewOffer()
    {
        if (state.View is not { Image: { } image } || state.SelectedStep < 0 || state.SelectedStep >= image.Steps.Length)
        {
            return new WorkbenchViewOffer(null, "Select a step to see its result.", null, "Select a step to compare it.");
        }

        var step = StepAnswer(image.Steps[state.SelectedStep]);
        var number = step.Index + 1;
        var resultFile = FileOfOwn(step, out var resultNote);
        if (resultFile is null)
        {
            return new WorkbenchViewOffer(null, resultNote, null, $"Step {number} has no result to compare: {Lower(resultNote)}");
        }

        // The input is the nearest earlier enabled step's stored result, else the original. A step with no file of its own that
        // makes the input (a derived step, one named by its run) leaves nothing to compare against, and says so. A step that writes
        // no pixels hands its input on unchanged, so it is looked through.
        for (var before = step.Index - 1; before >= 0; before--)
        {
            var earlier = StepAnswer(image.Steps[before]);
            if (earlier.State is StepState.Off || IsFinding(earlier))
            {
                continue;
            }

            var input = FileOfOwn(earlier, out var inputNote);
            return input is null
                ? new WorkbenchViewOffer(resultFile, null, null, $"Step {number}'s input is step {before + 1}'s result, which has no file: {Lower(inputNote)}")
                : new WorkbenchViewOffer(resultFile, null, input, null);
        }

        if (state.OriginalFile is { } original)
        {
            return new WorkbenchViewOffer(resultFile, null, original, null);
        }

        return new WorkbenchViewOffer(resultFile, null, null, $"Step {number}'s input is the original, which has no picture of its own: {Lower(state.OriginalNote)}");
    }

    private static string Lower(string? text) => string.IsNullOrEmpty(text) ? string.Empty : char.ToLowerInvariant(text[0]) + text[1..];

    private bool IsFinding(StepReport step) => catalog.TryGet(step.Operation, out var operation) && operation is IFindingOperation;

    // The image file a step keeps of its own, or null and why not.
    private string? FileOfOwn(StepReport step, out string? why)
    {
        why = null;
        var number = step.Index + 1;
        if (step.State is StepState.Off)
        {
            why = $"Step {number} is switched off, so it has no result of its own.";
        }
        else if (step.State is StepState.Derived)
        {
            why = $"Step {number} is derived: it is made from its input when the step after it asks, and has no file of its own.";
        }
        else if (catalog.TryGet(step.Operation, out var operation) && operation is IFindingOperation)
        {
            why = $"Step {number} writes no pixels: it keeps a finding that the steps after it apply, so there is no picture of its own.";
        }
        else if (operation is IRunNamedOperation)
        {
            why = $"Step {number}'s results are named by its run, so it has no one file to show.";
        }
        else if (step.Output is not { Length: > 0 } output || !IsThere(output))
        {
            why = step.State is StepState.Refused or StepState.Blocked
                ? $"Step {number} made nothing: it is {step.Words}."
                : $"Step {number} has not been made yet.";
        }
        else
        {
            return output;
        }

        return null;
    }

    // Whether an output was there when the runner's answer was read: a value taken with the answer, never the disk on a frame.
    private bool IsThere(string output)
        => state.View?.Existing.Contains(output) == true || state.Plan?.Existing.Contains(output) == true;

    /// <summary>The picture shown: the one chosen, or the original where the selected step cannot offer it.</summary>
    public WorkbenchViewKind EffectiveView()
    {
        var offer = ViewOffer();
        return state.ChosenView switch
        {
            WorkbenchViewKind.Result when offer.ResultFile is not null => WorkbenchViewKind.Result,
            WorkbenchViewKind.Comparison when offer.ResultFile is not null && offer.InputFile is not null => WorkbenchViewKind.Comparison,
            _ => WorkbenchViewKind.Original,
        };
    }

    /// <summary>What the viewer should be showing now: the effective view and its files.</summary>
    public WorkbenchShown? Wanted()
    {
        if (state.View is null)
        {
            return null;
        }

        var offer = ViewOffer();
        return EffectiveView() switch
        {
            WorkbenchViewKind.Result => new WorkbenchShown(WorkbenchViewKind.Result, null, offer.ResultFile),
            WorkbenchViewKind.Comparison => new WorkbenchShown(WorkbenchViewKind.Comparison, offer.InputFile, offer.ResultFile),
            _ => new WorkbenchShown(WorkbenchViewKind.Original, null, state.OriginalFile),
        };
    }

    // A step up to the selected one is as the status through that step found it (which of them will run and which are kept); every
    // other step is as the whole image's status found it. Both are the runner's answers.
    private StepReport StepAnswer(StepReport step)
        => state.Plan is { } plan && step.Index <= plan.Index && step.Index < plan.Steps.Length && plan.Steps[step.Index].Operation == step.Operation
            ? plan.Steps[step.Index]
            : step;

    private WorkbenchStepItem ItemFor(StepReport step)
    {
        var title = catalog.TryGet(step.Operation, out var operation) ? operation.Description.Title : step.Operation;
        return new WorkbenchStepItem(step.Index, title, step.State, step.Words, MarkFor(step.State), step.ChangedSinceLastRun, step.Message);
    }

    /// <summary>The mark a state is drawn with: a tick for a step that is kept (or ran), and a cross where it cannot go on.</summary>
    public static string MarkFor(StepState state) => state switch
    {
        StepState.Kept or StepState.Ran => "✓",
        StepState.WillRun => "▶",
        StepState.NotNeeded => "·",
        StepState.Derived => "∘",
        StepState.Off => "–",
        StepState.Refused => "✕",
        StepState.Blocked => "⊘",
        _ => string.Empty,
    };

    private static ImmutableArray<WorkbenchSettingItem> SettingsOf(string canonical, OperationDescription description)
    {
        if (JsonNode.Parse(canonical) is not JsonObject values)
        {
            return [];
        }

        var items = ImmutableArray.CreateBuilder<WorkbenchSettingItem>(values.Count);
        foreach (var (name, value) in values)
        {
            var shown = value switch
            {
                null => "none",
                JsonValue json when json.TryGetValue<string>(out var text) => text,
                _ => value.ToJsonString(),
            };
            var summary = description.Settings.FirstOrDefault(setting => string.Equals(setting.Name, name, StringComparison.Ordinal))?.Summary ?? string.Empty;
            items.Add(new WorkbenchSettingItem(name, shown, summary));
        }

        return items.MoveToImmutable();
    }

    private static async Task<WorkbenchView?> ReadViewAsync(WorkbenchRunner runner, string folder, string? image, CancellationToken ct)
    {
        var read = await WorkbenchProjectFile.ReadAsync(folder, ct);
        if (read.Project is not { } project)
        {
            return new WorkbenchView(folder, null, [], null, null, read.Refusal ?? "The project could not be read.");
        }

        var names = project.Images.Select(static i => i.Name).ToImmutableArray();
        var name = image is not null && names.Contains(image) ? image : names.FirstOrDefault();
        if (name is null)
        {
            return new WorkbenchView(folder, project, names, null, null, "The project has no images.");
        }

        var report = await runner.StatusAsync(folder, name, ct);
        var imageReport = report.Images.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
        return new WorkbenchView(folder, project, names, name, imageReport, report.Refusal ?? imageReport?.Refusal)
        {
            Existing = imageReport is null ? [] : WorkbenchOriginal.ExistingOutputs(imageReport.Steps),
            Original = WorkbenchOriginal.Of(imageReport?.Path),
        };
    }

    // Gives the viewer the original when it is a file it draws, and says in words what it is when it is not: as the answer found it.
    private void ShowOriginal(WorkbenchView view)
    {
        state.OriginalFile = view.Original?.File;
        state.OriginalNote = view.Original?.Note;
    }
}
