using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.UI.Abstractions;

// The two pages of New project (#2553, sections 1 and 2): what is in the capture folder, then which workflow to start from.
// Every answer is the Lib routine's (CaptureDetection, WorkbenchProjects.ReassignAsync, BuiltInWorkflows, WorkbenchWorkflows.ApplyAsync),
// asked off the render thread, and the page shows what came back, never what was clicked.
public sealed partial class WorkbenchActions
{
    /// <summary>The kinds the "Is" dropdown offers, in the Lib's order, as a person reads them; the first entry takes a reassignment back.</summary>
    public static ImmutableArray<(string Label, FrameType? Kind)> KindChoices { get; } =
    [
        ("Detected", null),
        .. TianWen.Lib.Imaging.Stacking.FrameReassignment.Kinds.Select(static kind => (KindLabel(kind), (FrameType?)kind)),
    ];

    /// <summary>The words for a kind of frame as the table and its dropdown say them.</summary>
    public static string KindLabel(FrameType kind) => kind switch
    {
        FrameType.Light => "Light",
        FrameType.Dark => "Dark",
        FrameType.Flat => "Flat",
        FrameType.Bias => "Bias",
        FrameType.DarkFlat => "Dark flat",
        FrameType.None => "Leave out",
        _ => kind.ToString(),
    };

    /// <summary>Starts the New project pages on <paramref name="captureFolder"/>: its project is made in its workdir, then the folder is detected.</summary>
    private async Task StartImportAsync(string captureFolder, CancellationToken ct)
    {
        var made = await WorkbenchProjects.NewAsync(captureFolder, ct);
        if (made.Refusal is { } refusal)
        {
            state.NewProjectRefusal = $"No project was made: {refusal}";
            state.NeedsRedraw = true;
            return;
        }

        state.NewProjectRefusal = null;
        state.Import = new WorkbenchImport(Path.GetFullPath(captureFolder)) { Detecting = true, MadeProject = true };
        state.Stage = WorkbenchStage.Import;
        state.NeedsRedraw = true;
        await DetectAsync(Path.GetFullPath(captureFolder), keepGradesOf: null, selectKind: null, ct);
    }

    // Detects the folder with the reassignments the project holds and replaces the page's capture with the answer. The grades of a
    // detection that stands (a reassignment re-detects, and reading every light's pixels again would make each press slow) are kept.
    private async Task DetectAsync(string folder, DetectedCapture? keepGradesOf, string? selectKind, CancellationToken ct)
    {
        try
        {
            var reassigned = await WorkbenchProjects.ReassignmentsAsync(folder, ct);
            var capture = await CaptureDetection.DetectAsync(folder, new CaptureDetectionOptions
            {
                Reassigned = reassigned,
                GradeLights = keepGradesOf is null,
            }, ct);
            if (keepGradesOf is not null)
            {
                capture = WithGradesOf(capture, keepGradesOf);
            }

            var facts = WorkflowFacts.From(capture);
            var workflows = ChoicesFor(facts);
            Update(folder, import =>
            {
                var group = selectKind is null ? import.SelectedGroup : capture.Groups.ToList().FindIndex(g => g.Heading == selectKind);
                return import with
                {
                    Capture = capture,
                    Detecting = false,
                    Reassigning = false,
                    Refusal = null,
                    SelectedGroup = Math.Clamp(group < 0 ? 0 : group, 0, Math.Max(0, capture.Groups.Length - 1)),
                    Workflows = workflows,
                    ChosenWorkflow = workflows.Any(w => w.Id == import.ChosenWorkflow && w.Answer.Fit.Fits)
                        ? import.ChosenWorkflow
                        : workflows.FirstOrDefault(static w => w.Answer.Best)?.Id,
                };
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The capture folder {Folder} could not be read", folder);
            Update(folder, import => import with { Detecting = false, Reassigning = false, Refusal = $"The folder could not be read: {ex.Message}" });
        }
    }

    private static DetectedCapture WithGradesOf(DetectedCapture fresh, DetectedCapture old)
    {
        var grades = old.Files.Where(static file => file.Grade is not null).ToDictionary(static file => file.Place, static file => file.Grade, StringComparer.OrdinalIgnoreCase);
        return fresh with
        {
            Groups = [.. fresh.Groups.Select(group => group with
            {
                Files = [.. group.Files.Select(file => file.Grade is null && file.Kind == FrameType.Light && grades.TryGetValue(file.Place, out var grade)
                    ? file with { Grade = grade }
                    : file)],
            })],
        };
    }

    // The workflows with their answer for the facts and the tree each becomes, worked out with the detection so no frame does it.
    private ImmutableArray<WorkflowChoice> ChoicesFor(WorkflowFacts facts)
        => [.. BuiltInWorkflows.Answer(facts).Select(answer => new WorkflowChoice(answer,
            WorkbenchWorkflows.Tree(new WorkbenchProject { Images = answer.Template.Build(facts) }, catalog)))];

    // Replaces the page's value whole, when it is still the folder's.
    private void Update(string folder, Func<WorkbenchImport, WorkbenchImport> change)
    {
        if (state.Import is { } import && string.Equals(import.Folder, folder, StringComparison.Ordinal))
        {
            state.Import = change(import);
            state.NeedsRedraw = true;
        }
    }

    /// <summary>Shows the group at <paramref name="index"/> of the detection on the Import page.</summary>
    public void SelectGroup(int index)
    {
        if (state.Import is { Capture: { } capture } import && index >= 0 && index < capture.Groups.Length && index != import.SelectedGroup)
        {
            state.Import = import with { SelectedGroup = index };
            state.NeedsRedraw = true;
        }
    }

    /// <summary>
    /// Saves that the file at <paramref name="place"/> is a <paramref name="kind"/> (null takes the reassignment back) through
    /// <see cref="WorkbenchProjects.ReassignAsync"/>, then detects the folder again, so the table shows what the project now
    /// holds. A refusal is said in words on the page. Nothing is asked while an earlier save is under way.
    /// </summary>
    public void Reassign(string place, FrameType? kind)
    {
        if (state.Import is not { Capture: not null, Reassigning: false, Detecting: false } import)
        {
            return;
        }

        var (folder, old, heading) = (import.Folder, import.Capture, import.Group?.Heading);
        state.Import = import with { Reassigning = true, Refusal = null };
        state.NeedsRedraw = true;
        tracker.RunGuarded(async ct =>
        {
            var result = await WorkbenchProjects.ReassignAsync(folder, place, kind, ct);
            if (result.Refusal is { } refusal)
            {
                Update(folder, current => current with { Reassigning = false, Refusal = $"{place} was not changed: {refusal}" });
                return;
            }

            await DetectAsync(folder, old, heading, ct);
        }, appToken, logger, "Reassign a frame of the capture",
            onError: ex => Update(folder, current => current with { Reassigning = false, Refusal = $"{place} was not changed: {ex.Message}" }),
            onFinally: () => state.NeedsRedraw = true);
    }

    /// <summary>The Import page's Cancel: the project this New project made is taken back (a project that was there before is not touched) and the workbench shows as it was.</summary>
    public void CancelImport()
    {
        if (state.Import is not { } import || import.Applying)
        {
            return;
        }

        state.Import = null;
        state.Stage = WorkbenchStage.Workbench;
        state.NeedsRedraw = true;
        if (import.MadeProject)
        {
            tracker.RunGuarded(_ =>
            {
                var workdir = WorkbenchProjects.WorkdirOf(import.Folder);
                var file = WorkbenchProjectFile.PathIn(workdir);
                if (File.Exists(file))
                {
                    File.Delete(file);
                }

                if (Directory.Exists(workdir) && !Directory.EnumerateFileSystemEntries(workdir).Any())
                {
                    Directory.Delete(workdir);
                }

                return Task.CompletedTask;
            }, appToken, logger, "Take back the project a cancelled New project made", onError: _ => state.NeedsRedraw = true);
        }
    }

    /// <summary>The Import page's main action: on to the Workflow page. Not before the detection has answered.</summary>
    public void ChooseAWorkflow()
    {
        if (state.Import is { Capture: not null, Detecting: false, Reassigning: false } import && !import.Workflows.IsDefaultOrEmpty)
        {
            state.Import = import with { Refusal = null };
            state.Stage = WorkbenchStage.Workflow;
            state.NeedsRedraw = true;
        }
    }

    /// <summary>The Workflow page's Back: to what is in the folder.</summary>
    public void BackToImport()
    {
        if (state.Import is { Applying: false } import)
        {
            state.Import = import with { Refusal = null };
            state.Stage = WorkbenchStage.Import;
            state.NeedsRedraw = true;
        }
    }

    /// <summary>Selects a workflow that fits; one that does not fit is not chosen (its card says why).</summary>
    public void ChooseWorkflow(string id)
    {
        if (state.Import is { Applying: false } import
            && import.Workflows.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal)) is { Answer.Fit.Fits: true })
        {
            state.Import = import with { ChosenWorkflow = id };
            state.NeedsRedraw = true;
        }
    }

    /// <summary>
    /// Writes the selected workflow (or <paramref name="id"/>, when given) into the project through <see cref="WorkbenchWorkflows.ApplyAsync"/>
    /// off the render thread, then opens the workbench on the project. A refusal is said in words on the page.
    /// </summary>
    public void UseWorkflow(string? id = null)
    {
        if (state.Import is not { Capture: { } capture, Applying: false } import)
        {
            return;
        }

        var chosen = id ?? import.ChosenWorkflow;
        if (chosen is null || import.Workflows.FirstOrDefault(w => string.Equals(w.Id, chosen, StringComparison.Ordinal)) is not { Answer.Fit.Fits: true })
        {
            return;
        }

        var (folder, facts) = (import.Folder, WorkflowFacts.From(capture));
        state.Import = import with { Applying = true, Refusal = null, ChosenWorkflow = chosen };
        state.NeedsRedraw = true;
        tracker.RunGuarded(async ct =>
        {
            var applied = await WorkbenchWorkflows.ApplyAsync(folder, chosen, facts, ct);
            if (applied.Refusal is { } refusal)
            {
                Update(folder, current => current with { Applying = false, Refusal = $"The workflow was not used: {refusal}" });
                return;
            }

            state.Import = null;
            state.Stage = WorkbenchStage.Workbench;
            state.SelectedStep = -1;
            if (!OpenProject(WorkbenchProjects.WorkdirOf(folder)))
            {
                state.NewProjectRefusal = $"The project was made in {WorkbenchProjects.WorkdirOf(folder)}. Use Open project to show it.";
            }

            state.NeedsRedraw = true;
        }, appToken, logger, "Apply a workflow to the new project",
            onError: ex => Update(folder, current => current with { Applying = false, Refusal = $"The workflow was not used: {ex.Message}" }),
            onFinally: () => state.NeedsRedraw = true);
    }
}
