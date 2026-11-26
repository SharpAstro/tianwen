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
}
