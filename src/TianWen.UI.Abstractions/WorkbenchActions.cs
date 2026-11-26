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
    /// <summary>The step's whole text for a hover, which the bar may have cut short.</summary>
    public string Hover => $"{Index + 1}. {Title}: {Words}{(Message is { Length: > 0 } ? $". {Message}" : string.Empty)}"
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
    ViewerState viewerState,
    WorkbenchRunner runner,
    OperationCatalog catalog,
    BackgroundTaskTracker tracker,
    IFileDialogs dialogs,
    Action<string> setClipboardText,
    ILogger logger,
    CancellationToken appToken)
{
    private const string StatusSlot = "workbench.status";

    /// <summary>
    /// Opens the project in <paramref name="folder"/> (or its <c>project.json</c>) and asks the runner where every edit of the
    /// image stands. Off the render thread; the answer is adopted by <see cref="TryAdoptStatus"/>. A newer call supersedes an
    /// older one that is still reading.
    /// </summary>
    public void OpenProject(string folder, string? image = null)
    {
        var projectFolder = Path.GetFullPath(File.Exists(folder) ? Path.GetDirectoryName(folder) ?? folder : folder);
        state.IsReading = true;
        state.NeedsRedraw = true;
        tracker.RunExclusive<WorkbenchView>(StatusSlot, ct => ReadAsync(projectFolder, image, ct), appToken, logger,
            "Read the workbench project",
            onError: ex => state.View = new WorkbenchView(projectFolder, null, [], null, null, $"The project could not be read: {ex.Message}"),
            onFinally: () => state.NeedsRedraw = true);
    }

    /// <summary>The Open project button: a folder dialog, then <see cref="OpenProject"/>.</summary>
    public void ChooseProject()
        => tracker.RunGuarded(async ct =>
        {
            var result = await dialogs.OpenFolderAsync(new FileDialogRequest
            {
                Title = "Open a workbench project",
                InitialDirectory = state.View?.Folder,
            }, ct);
            if (result.Path is { } picked)
            {
                OpenProject(picked);
            }
        }, appToken, logger, "Choose a workbench project", onError: _ => state.NeedsRedraw = true);

    /// <summary>Shows another image of the project.</summary>
    public void ChooseImage(string name)
    {
        if (state.View is { } view && !string.Equals(view.ImageName, name, StringComparison.Ordinal))
        {
            state.SelectedStep = -1;
            OpenProject(view.Folder, name);
        }
    }

    /// <summary>Selects a step: its panel is drawn. Nothing runs and nothing about any step's state changes.</summary>
    public void SelectStep(int index)
    {
        if (state.SelectedStep != index)
        {
            state.SelectedStep = index;
            state.CommandCopied = false;
            state.NeedsRedraw = true;
        }
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
        if (!tracker.TryCollect<WorkbenchView>(StatusSlot, out var view) || view is null)
        {
            return;
        }

        var previous = state.View;
        state.View = view;
        state.IsReading = false;
        state.NeedsRedraw = true;

        var steps = view.Image?.Steps.Length ?? 0;
        if (state.SelectedStep < 0 || state.SelectedStep >= steps || previous?.ImageName != view.ImageName)
        {
            state.SelectedStep = steps > 0 ? 0 : -1;
            state.CommandCopied = false;
        }

        ShowOriginal(view);
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
            items.Add(ItemFor(step));
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

        var step = image.Steps[state.SelectedStep];
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

        return new WorkbenchStepPanel(item, description?.Summary ?? string.Empty, settings, settingsRefusal, step.ServedBy, step.CommandLine);
    }

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

    private async Task<WorkbenchView?> ReadAsync(string folder, string? image, CancellationToken ct)
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
        return new WorkbenchView(folder, project, names, name, imageReport, report.Refusal ?? imageReport?.Refusal);
    }

    // Gives the viewer the original when it is a file it draws, and says in words what it is when it is not.
    private void ShowOriginal(WorkbenchView view)
    {
        var path = view.Image?.Path;
        if (string.IsNullOrEmpty(path))
        {
            state.OriginalNote = null;
            state.OriginalFile = null;
            return;
        }

        if (Directory.Exists(path))
        {
            state.OriginalFile = null;
            state.OriginalNote = "This image's original is a folder of frames. A step stacks them, and there is nothing to draw until it has run.";
        }
        else if (string.Equals(Path.GetExtension(path), ".ser", StringComparison.OrdinalIgnoreCase))
        {
            state.OriginalFile = null;
            state.OriginalNote = "This image's original is a SER capture. The planetary stack step makes its master, and there is nothing to draw until it has run.";
        }
        else if (!File.Exists(path))
        {
            state.OriginalFile = null;
            state.OriginalNote = $"The original is not there: {path}";
        }
        else
        {
            state.OriginalNote = null;
            if (!string.Equals(state.OriginalFile, path, StringComparison.OrdinalIgnoreCase))
            {
                state.OriginalFile = path;
                viewerState.RequestedFilePath = path;
            }
        }
    }
}
