using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.UI.Abstractions;

/// <summary>One setting of the selected step as the panel edits it: the control the Lib derived and the value the runner's project holds.</summary>
/// <param name="Field">The Lib's description of the setting (<see cref="OperationSettingsForm"/>).</param>
/// <param name="Value">The setting's value as a control shows it; empty for a setting that is left out.</param>
/// <param name="Summary">What it does.</param>
public sealed record WorkbenchSettingControl(OperationSettingField Field, string Value, string Summary)
{
    /// <summary>The setting's name as a person reads it: <c>keepOvershoot</c> is "Keep overshoot".</summary>
    public string Label
    {
        get
        {
            var text = new System.Text.StringBuilder(Field.Name.Length + 4);
            for (var i = 0; i < Field.Name.Length; i++)
            {
                var c = Field.Name[i];
                if (i == 0)
                {
                    text.Append(char.ToUpperInvariant(c));
                }
                else if (char.IsUpper(c))
                {
                    text.Append(' ').Append(char.ToLowerInvariant(c));
                }
                else
                {
                    text.Append(c);
                }
            }

            return text.ToString();
        }
    }
}

// The selected step's panel as controls (#2553 section 3): a setting is written into the project through the Lib, the project is read
// again, and the panel and the tree show the runner's new answer (the step stale), never the click.
public sealed partial class WorkbenchActions
{
    /// <summary>The selected step's settings as controls, derived from the operation's JSON contract; empty where there is no step or no contract.</summary>
    public ImmutableArray<WorkbenchSettingControl> SettingControls()
    {
        if (!TrySelected(out var view, out var image, out var index)
            || !catalog.TryGet(image.Steps[index].Operation, out var operation)
            || view.ProjectImage is not { } projectImage || index >= projectImage.Edits.Length
            || !operation.TryReadSettings(projectImage.Edits[index].Settings?.ToJsonString(), out var canonical, out _)
            || JsonNode.Parse(canonical) is not JsonObject values)
        {
            return [];
        }

        var controls = ImmutableArray.CreateBuilder<WorkbenchSettingControl>();
        foreach (var field in OperationSettingsForm.Describe(operation))
        {
            var shown = values.TryGetPropertyValue(field.Name, out var node) && node is not null
                ? node is JsonValue json && json.TryGetValue<string>(out var text) ? text : node.ToJsonString()
                : string.Empty;
            controls.Add(new WorkbenchSettingControl(field, shown, field.Summary));
        }

        return controls.ToImmutable();
    }

    /// <summary>
    /// Writes a setting of the selected step from the text a control holds, through <see cref="WorkbenchWorkflows.SetStepSettingAsync"/> off
    /// the render thread, then reads the project again with the step selected: the tree and the panel show the runner's answer, which is the
    /// step stale. A text that is not a value of the setting is refused in words (<see cref="WorkbenchState.SettingRefusal"/>) and nothing is written.
    /// </summary>
    public void SetSetting(string name, string text)
    {
        if (_jobs.IsRunning || !TrySelected(out var view, out var shown, out var index) || view.ImageName is not { } image
            || !catalog.TryGet(shown.Steps[index].Operation, out var operation))
        {
            return;
        }

        var field = OperationSettingsForm.Describe(operation).FirstOrDefault(each => string.Equals(each.Name, name, StringComparison.Ordinal));
        if (field is null)
        {
            return;
        }

        state.SettingRefusal = null;
        if (!OperationSettingsForm.TryParse(field, text, out var value, out var refusal))
        {
            state.SettingRefusal = $"{name} was not changed: {refusal}";
            state.NeedsRedraw = true;
            return;
        }

        tracker.RunGuarded(async ct =>
        {
            var result = await WorkbenchWorkflows.SetStepSettingAsync(view.Folder, catalog, image, index, name, value, ct);
            if (result.Refusal is { } why)
            {
                HandToFrame(() => state.SettingRefusal = $"{name} was not changed: {why}");
                return;
            }

            HandToFrame(() =>
            {
                state.SelectAfterStatus = index;
                OpenProjectNow(view.Folder, image);
            });
        }, appToken, logger, "Change a setting of a workbench step",
            onError: ex => HandToFrame(() => state.SettingRefusal = $"{name} was not changed: {ex.Message}"),
            onFinally: () => state.NeedsRedraw = true);
    }

    /// <summary>Switches the selected step on or off, through <see cref="WorkbenchWorkflows.SetStepEnabledAsync"/>; the runner's answer then says it is off (or not made).</summary>
    public void SetStepEnabled(bool enabled)
    {
        if (_jobs.IsRunning || !TrySelected(out var view, out _, out var index) || view.ImageName is not { } image)
        {
            return;
        }

        state.SettingRefusal = null;
        tracker.RunGuarded(async ct =>
        {
            var result = await WorkbenchWorkflows.SetStepEnabledAsync(view.Folder, image, index, enabled, ct);
            if (result.Refusal is { } why)
            {
                HandToFrame(() => state.SettingRefusal = $"The step was not switched: {why}");
                return;
            }

            HandToFrame(() =>
            {
                state.SelectAfterStatus = index;
                OpenProjectNow(view.Folder, image);
            });
        }, appToken, logger, "Switch a workbench step on or off",
            onError: ex => HandToFrame(() => state.SettingRefusal = $"The step was not switched: {ex.Message}"),
            onFinally: () => state.NeedsRedraw = true);
    }

    /// <summary>Opens or folds the command line under the selected step's panel.</summary>
    public void ToggleCommandLine()
    {
        state.CommandLineOpen = !state.CommandLineOpen;
        state.NeedsRedraw = true;
    }

    /// <summary>What the runner's answer asks of the person for the step, in words: why it is stale or not made, what fixes a refusal.</summary>
    public string FixWords(WorkbenchStepItem step) => step.State switch
    {
        StepState.Refused or StepState.Blocked => "Change its settings or the step before it, then run again.",
        StepState.Off => "Switch it on to use it; the steps after it take its input meanwhile.",
        StepState.WillRun when step.Changed => "Its settings, or a step before it, changed since it was made, so running to here makes it again.",
        StepState.WillRun => "Run to here makes it.",
        StepState.Derived => "Made from its input when the step after it asks.",
        _ => string.Empty,
    };

    /// <summary>What the selected step gives, as the panel lists it: its result and each extra one, with the state in words.</summary>
    public ImmutableArray<(string Name, string State)> Gives()
    {
        if (Panel() is not { } panel || !catalog.TryGet(StepOperation(), out var operation))
        {
            return [];
        }

        var description = operation.Description;
        var words = panel.Step.Words;
        var items = ImmutableArray.CreateBuilder<(string, string)>();
        if (description.NamedByRun)
        {
            // The masters are listed by name in their own block of the panel (each with Process this master): said once.
            items.Add((panel.Masters.IsEmpty ? "Masters, one for each group" : $"{panel.Masters.Length} masters", words));
        }
        else
        {
            items.Add((description.Output == OperationOutputKind.Finding ? "Finding" : "Image", words));
        }

        foreach (var extra in description.ExtraResults)
        {
            items.Add((extra.Name, words));
        }

        return items.ToImmutable();
    }

    private string StepOperation()
        => TrySelected(out _, out var image, out var index) ? image.Steps[index].Operation : string.Empty;

    /// <summary>Shows a file of the selected frame group on the canvas, through the viewer's own load path (the driver asks for it between frames, off the render thread).</summary>
    public void ShowFrameFile(string fullPath)
    {
        state.PreviewFrame = fullPath;
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Saves that the capture's file at <paramref name="place"/> is a <paramref name="kind"/> (null takes the answer back) through
    /// <see cref="WorkbenchProjects.ReassignAsync"/>, as the Import page does, then reads the frames and the project again: the stack step
    /// reads the reassignment, so it shows as not made.
    /// </summary>
    public void ReassignFrame(string place, FrameType? kind)
    {
        if (_jobs.IsRunning || state.View is not { } view || CaptureFolderOf(view) is not { } capture)
        {
            return;
        }

        state.TreeRefusal = null;
        tracker.RunGuarded(async ct =>
        {
            var result = await WorkbenchProjects.ReassignAsync(capture, place, kind, ct);
            if (result.Refusal is { } why)
            {
                HandToFrame(() => state.TreeRefusal = $"{place} was not changed: {why}");
                return;
            }

            HandToFrame(() =>
            {
                RequestFrames(view, force: true);
                OpenProjectNow(view.Folder, view.ImageName);
            });
        }, appToken, logger, "Reassign a frame of the capture",
            onError: ex => HandToFrame(() => state.TreeRefusal = $"{place} was not changed: {ex.Message}"),
            onFinally: () => state.NeedsRedraw = true);
    }
}
