using System.Collections.Immutable;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.UI.Abstractions;

/// <summary>Which page of <c>tianwen-edit</c> is on show: the two pages of New project, or the workbench itself (#2553).</summary>
public enum WorkbenchStage
{
    /// <summary>The workbench: the step bar, the panel and the canvas.</summary>
    Workbench,

    /// <summary>New project, 1 of 2: what is in the capture folder.</summary>
    Import,

    /// <summary>New project, 2 of 2: which workflow to start from.</summary>
    Workflow,
}

/// <summary>A built-in workflow with the answer for the capture on show and the tree it becomes.</summary>
/// <param name="Answer">The workflow, whether it fits and why (<see cref="BuiltInWorkflows.Answer"/>).</param>
/// <param name="Tree">The steps it writes, as the tree the project shows (<see cref="WorkbenchWorkflows.Tree"/>).</param>
public sealed record WorkflowChoice(WorkflowAnswer Answer, ImmutableArray<WorkflowRow> Tree)
{
    /// <summary>The workflow's id.</summary>
    public string Id => Answer.Template.Id;

    /// <summary>How many steps it writes, for the card.</summary>
    public int StepCount => Tree.Count(static row => row.Kind is not (WorkflowRowKind.Source or WorkflowRowKind.Image));
}

/// <summary>
/// What the two pages of New project show: the capture folder, what the detection found there, the workflows it fits, and which
/// is chosen. One immutable value, replaced whole by the actions from the answers of the Lib routines, so the render thread never
/// sees half of one and a click never sets what the Lib has not said.
/// </summary>
/// <param name="Folder">The capture folder.</param>
public sealed record WorkbenchImport(string Folder)
{
    /// <summary>What the detection found; null until it has answered.</summary>
    public DetectedCapture? Capture { get; init; }

    /// <summary>True while the detection is asked (off the render thread).</summary>
    public bool Detecting { get; init; }

    /// <summary>True when the project in the capture's workdir was made by this New project, so Cancel takes it back.</summary>
    public bool MadeProject { get; init; }

    /// <summary>The group on show on the Import page, an index into <see cref="DetectedCapture.Groups"/>.</summary>
    public int SelectedGroup { get; init; }

    /// <summary>Why the last reassignment, detection or apply was refused, in words; null when it was not.</summary>
    public string? Refusal { get; init; }

    /// <summary>The workflows with their answers for the capture, built-in order; empty until the detection has answered.</summary>
    public ImmutableArray<WorkflowChoice> Workflows { get; init; } = [];

    /// <summary>The workflow the Workflow page has selected (the best fit until another is chosen); null for none.</summary>
    public string? ChosenWorkflow { get; init; }

    /// <summary>True from the press of Use selected workflow until the project is written and its read is asked.</summary>
    public bool Applying { get; init; }

    /// <summary>True while a reassignment is being saved and the capture detected again.</summary>
    public bool Reassigning { get; init; }

    /// <summary>The group on show, or null before the detection answered.</summary>
    public DetectedGroup? Group
        => Capture is { } capture && SelectedGroup >= 0 && SelectedGroup < capture.Groups.Length ? capture.Groups[SelectedGroup] : null;

    /// <summary>The files of the detection that need a look (kind a guess, or a note).</summary>
    public int NeedsALook => Capture?.Groups.FirstOrDefault(static group => group.Kind == DetectedGroupKind.NeedsALook)?.Files.Length ?? 0;
}
