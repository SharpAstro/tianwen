using System;
using System.Linq;
using System.Collections.Immutable;
using System.Threading;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.UI.Abstractions;

/// <summary>
/// What the processing app knows of the project on show, as the runner last answered: the project, the image on show and
/// that image's report. It is one immutable value, replaced whole, so the render thread never sees half of an answer.
/// </summary>
/// <param name="Folder">The project folder (the one holding <c>project.json</c>).</param>
/// <param name="Project">The project file, read; null when it could not be read.</param>
/// <param name="ImageNames">The name of every image of the project, in file order.</param>
/// <param name="ImageName">The image on show; null when the project holds none.</param>
/// <param name="Image">The runner's report of that image: each edit's state; null when the project was refused.</param>
/// <param name="Refusal">Why the project cannot be worked on at all (no project file, one that does not read, a status the
/// runner refused); null when it can.</param>
public sealed record WorkbenchView(
    string Folder,
    WorkbenchProject? Project,
    ImmutableArray<string> ImageNames,
    string? ImageName,
    ImageReport? Image,
    string? Refusal)
{
    /// <summary>
    /// The files among the steps' outputs that were there when the runner answered, worked out off the render thread with the
    /// answer, so a frame asks this value and never the disk.
    /// </summary>
    public ImmutableHashSet<string> Existing { get; init; } = [];

    /// <summary>What the image's original is to the viewer, worked out with the answer; null before there is an image.</summary>
    public WorkbenchOriginal? Original { get; init; }

    /// <summary>The image on show, as the project file states it (its edits' settings).</summary>
    public ProjectImage? ProjectImage
        => Project is { } project && ImageName is { } name
            ? project.Images.FirstOrDefault(image => string.Equals(image.Name, name, StringComparison.Ordinal))
            : null;
}

/// <summary>
/// The state of the processing app's window: what the runner said about the project, which step is selected and the
/// flags the loop reads. Everything the runner said is set from its answer, never from a click.
/// </summary>
public sealed class WorkbenchState
{
    private WorkbenchView? _view;

    /// <summary>What the runner last said about the project on show; null before a project is opened.</summary>
    public WorkbenchView? View
    {
        get => Volatile.Read(ref _view);
        set => Volatile.Write(ref _view, value);
    }

    private string? _newProjectRefusal;

    /// <summary>Why the New project button made no project, in words (<see cref="WorkbenchProjects.NewAsync"/>'s refusal); null
    /// when the last one was made or none was asked. The next successful open clears it.</summary>
    public string? NewProjectRefusal
    {
        get => Volatile.Read(ref _newProjectRefusal);
        set => Volatile.Write(ref _newProjectRefusal, value);
    }

    private string? _masterRefusal;

    /// <summary>Why "Process this master" added nothing, in words (the routine's refusal); null when the last press worked or none
    /// was made. The next open of a project clears it.</summary>
    public string? MasterRefusal
    {
        get => Volatile.Read(ref _masterRefusal);
        set => Volatile.Write(ref _masterRefusal, value);
    }

    private int _comparisonDeclined;

    /// <summary>True when the viewer declined to keep the before pixels (memory pressure) so the comparison on show is the result alone;
    /// the panel says so in words. Cleared when the screen asks for another picture.</summary>
    public bool ComparisonDeclined
    {
        get => Volatile.Read(ref _comparisonDeclined) != 0;
        set => Volatile.Write(ref _comparisonDeclined, value ? 1 : 0);
    }

    private int _isProcessingMaster;

    /// <summary>True from a press of "Process this master" until its images are in the project: no second press is taken meanwhile.</summary>
    public bool IsProcessingMaster
    {
        get => Volatile.Read(ref _isProcessingMaster) != 0;
        set => Volatile.Write(ref _isProcessingMaster, value ? 1 : 0);
    }

    /// <summary>The selected step's place in the edit list, from 0; -1 for none.</summary>
    public int SelectedStep { get; set; } = -1;

    /// <summary>True while the runner is asked for a status: the step bar then keeps what it had and says so.</summary>
    public bool IsReading { get; set; }

    /// <summary>The window needs a frame.</summary>
    public bool NeedsRedraw { get; set; } = true;

    /// <summary>Set when the selected step's command line was last put on the clipboard; cleared by choosing a step.</summary>
    public bool CommandCopied { get; set; }

    /// <summary>What the original is, in words, when the viewer cannot draw it (a folder of frames, a SER capture);
    /// null when the viewer is given the file.</summary>
    public string? OriginalNote { get; set; }

    /// <summary>The original's file as the viewer was last given it; null when it was given none.</summary>
    public string? OriginalFile { get; set; }

    /// <summary>The switch "Run on until a step needs me": on, a run goes to the end of the image's list and stops at a step that
    /// is refused or blocked; off, a run makes only the next step that is not kept and stops with it selected.</summary>
    public bool RunOn { get; set; } = true;

    /// <summary>What a run to the selected step would do, as the runner said it (a status through that step); null before the
    /// answer is in, and after any change of step or project. It is the runner's word, never the click's.</summary>
    public WorkbenchPlan? Plan
    {
        get => Volatile.Read(ref _plan);
        set => Volatile.Write(ref _plan, value);
    }

    private WorkbenchPlan? _plan;

    /// <summary>The view the person chose over the canvas; one that the selected step cannot offer is shown as the original.</summary>
    public WorkbenchViewKind ChosenView { get; set; } = WorkbenchViewKind.Original;

    /// <summary>The step to select once the next status is in (after a run that stopped at it), or -1 for none.</summary>
    public int SelectAfterStatus { get; set; } = -1;
}

/// <summary>Which picture the canvas shows.</summary>
public enum WorkbenchViewKind
{
    /// <summary>The image's original file.</summary>
    Original,

    /// <summary>The selected step's stored result.</summary>
    Result,

    /// <summary>The selected step's input against its result, through the viewer's split.</summary>
    Comparison,
}

/// <summary>
/// The runner's answer to "what would a run to this step do": a status through the step, so each earlier step is Kept or WillRun.
/// </summary>
/// <param name="Folder">The project folder it was asked of.</param>
/// <param name="Image">The image.</param>
/// <param name="Index">The step the status goes through.</param>
/// <param name="Steps">The image's steps as that status found them.</param>
public sealed record WorkbenchPlan(string Folder, string Image, int Index, ImmutableArray<StepReport> Steps)
{
    /// <summary>The outputs among its steps that were there when it was read, worked out off the render thread.</summary>
    public ImmutableHashSet<string> Existing { get; init; } = [];
}

/// <summary>What an image's original is to the viewer: a file it draws, or in words why not.</summary>
/// <param name="File">The file the viewer is given; null when it cannot draw the original.</param>
/// <param name="Note">What the original is in words when it cannot be drawn; null when there is a file.</param>
public sealed record WorkbenchOriginal(string? File, string? Note)
{
    /// <summary>Looks at the disk once; call it off the render thread.</summary>
    public static WorkbenchOriginal Of(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return new WorkbenchOriginal(null, null);
        }

        if (System.IO.Directory.Exists(path))
        {
            return new WorkbenchOriginal(null, "This image's original is a folder of frames. A step stacks them, and there is nothing to draw until it has run.");
        }

        if (string.Equals(System.IO.Path.GetExtension(path), ".ser", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkbenchOriginal(null, "This image's original is a SER capture. The planetary stack step makes its master, and there is nothing to draw until it has run.");
        }

        return System.IO.File.Exists(path)
            ? new WorkbenchOriginal(path, null)
            : new WorkbenchOriginal(null, $"The original is not there: {path}");
    }

    /// <summary>The outputs among <paramref name="steps"/> that exist now; call it off the render thread.</summary>
    public static ImmutableHashSet<string> ExistingOutputs(System.Collections.Generic.IEnumerable<StepReport> steps)
        => steps.Select(static s => s.Output).OfType<string>().Where(static o => o.Length > 0 && System.IO.File.Exists(o)).ToImmutableHashSet();
}

/// <summary>What the "Run to here" or "Run on" button says it will do.</summary>
/// <param name="Label">The button's words.</param>
/// <param name="CanRun">True when pressing it starts a run.</param>
/// <param name="Index">The step the run goes through, from 0; -1 for none.</param>
public sealed record WorkbenchRunOffer(string Label, bool CanRun, int Index);

/// <summary>What the selected step can show: its result and its input as files, or in words why not.</summary>
/// <param name="ResultFile">The step's stored result as an image file; null when it has none of its own.</param>
/// <param name="ResultNote">Why there is no result to show; null when there is one.</param>
/// <param name="InputFile">The file of the step's input; null when there is none to compare with.</param>
/// <param name="ComparisonNote">Why there is no comparison; null when there is one.</param>
public sealed record WorkbenchViewOffer(string? ResultFile, string? ResultNote, string? InputFile, string? ComparisonNote)
{
    /// <summary>True when Result can be chosen.</summary>
    public bool ResultOffered => ResultFile is not null;

    /// <summary>True when Comparison can be chosen.</summary>
    public bool ComparisonOffered => ResultFile is not null && InputFile is not null;
}

/// <summary>What the viewer is to show: the view and the one or two files it takes (the input first for a comparison).</summary>
public sealed record WorkbenchShown(WorkbenchViewKind Kind, string? Input, string? File);
