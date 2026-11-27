using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>A new project, or why none was made.</summary>
/// <param name="Project">The project written, null when it was refused.</param>
/// <param name="Refusal">Why not, in words; null when the project was written.</param>
public readonly record struct NewProjectResult(WorkbenchProject? Project, string? Refusal);

/// <summary>The reassignments a capture's project holds, or why a change to them was refused.</summary>
/// <param name="Reassigned">The files the person set the kind of, by place, in the order the step's key has them.</param>
/// <param name="Refusal">Why not, in words; null when it was done.</param>
public readonly record struct ReassignResult(ImmutableArray<FrameReassignment> Reassigned, string? Refusal);

/// <summary>Images added to a project, or why none were.</summary>
/// <param name="Added">The images written into the project, in the order of the project; empty when refused.</param>
/// <param name="Refusal">Why not, in words; null when they were added.</param>
public readonly record struct ChainAddedResult(ImmutableArray<ProjectImage> Added, string? Refusal);

/// <summary>
/// What starts a project and what is added to it, for every host: the <c>workbench new</c>, <c>workbench process</c> and
/// <c>workbench chain</c> verbs and the processing app's buttons call these routines and print or show what they answer,
/// so the window and the terminal write the same <c>project.json</c> (#2547).
/// </summary>
public static class WorkbenchProjects
{
    /// <summary>The folder inside a capture folder that holds its project and every result (#2553).</summary>
    public const string WorkdirName = StackingPipeline.WorkdirName;

    /// <summary>The project folder of the capture in <paramref name="framesFolder"/>: its <see cref="WorkdirName"/>.</summary>
    public static string WorkdirOf(string framesFolder) => Path.Combine(Path.GetFullPath(framesFolder), WorkdirName);

    /// <summary>
    /// Makes the project of the capture in <paramref name="framesFolder"/>, in its <see cref="WorkdirName"/> (project.json
    /// and the step store inside it). The frames are read and never written, and the stack never reads anything under
    /// <c>workdir</c>. Refused in words as <see cref="NewAsync(string, string, CancellationToken)"/> refuses.
    /// </summary>
    public static Task<NewProjectResult> NewAsync(string framesFolder, CancellationToken cancellationToken = default)
        => NewAsync(WorkdirOf(framesFolder), framesFolder, cancellationToken);

    /// <summary>
    /// Makes the project in <paramref name="projectFolder"/> (created when it is not there) for the capture in
    /// <paramref name="framesFolder"/>: one image, named after the capture's folder, whose original is that folder and
    /// whose one edit is the stack at its defaults. The stack finds its calibration itself, as <c>tianwen stack</c>
    /// does, so no calibration is written. Refused in words: a folder that already holds a project, a capture folder
    /// that is not there, one that is the project's folder or inside its step store.
    /// </summary>
    public static async Task<NewProjectResult> NewAsync(string projectFolder, string framesFolder, CancellationToken cancellationToken = default)
    {
        var project = Path.GetFullPath(projectFolder);
        var frames = Path.GetFullPath(framesFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(WorkbenchProjectFile.PathIn(project)))
        {
            return Refused($"{project} already holds a {WorkbenchProjectFile.FileName}: open it, or choose another folder for the new project");
        }

        if (!Directory.Exists(frames))
        {
            return Refused($"the capture folder {frames} does not exist");
        }

        var projectTrimmed = project.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(frames, projectTrimmed, PathComparison))
        {
            return Refused($"the capture folder {frames} is the project's folder: choose a folder of its own for the project");
        }

        // The reverse: a project folder inside the capture folder would put project.json and the store among the frames
        // the stack reads next.
        // The capture's own workdir is the one place inside it a project may be: the stack leaves it out of its scan.
        var isWorkdir = string.Equals(projectTrimmed, WorkdirOf(frames).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison);
        if (!isWorkdir && (projectTrimmed.StartsWith(frames + Path.DirectorySeparatorChar, PathComparison)
            || projectTrimmed.StartsWith(frames + Path.AltDirectorySeparatorChar, PathComparison)))
        {
            return Refused($"the project's folder {projectTrimmed} is inside the capture folder {frames}, where the stack would read its files as frames: choose a folder outside the capture, or the capture's own workdir");
        }

        var store = Path.Combine(projectTrimmed, StepStore.FolderName);
        if (string.Equals(frames, store, PathComparison) || frames.StartsWith(store + Path.DirectorySeparatorChar, PathComparison)
            || frames.StartsWith(store + Path.AltDirectorySeparatorChar, PathComparison))
        {
            return Refused($"the capture folder {frames} is inside the project's step store ({store}), which a run writes into");
        }

        var name = Path.GetFileName(frames);
        var made = new WorkbenchProject
        {
            Images =
            [
                new ProjectImage
                {
                    Name = name.Length > 0 ? name : "capture",
                    Path = frames,
                    Edits = [new ProjectEdit { Operation = StackOperation.Id }],
                },
            ],
        };
        Directory.CreateDirectory(project);
        await WorkbenchProjectFile.WriteAsync(project, made, cancellationToken);
        return new NewProjectResult(made, null);
    }

    // The stack edits of the images that stack this capture: where its reassignments are kept.
    private static bool IsStackEditOf(ProjectImage image, ProjectEdit edit, string frames)
        => edit.Operation is StackOperation.Id or CometStackOperation.Id
            && image.From is null
            && image.Path.Length > 0
            && string.Equals(
                Path.GetFullPath(image.Path, WorkdirOf(frames)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                frames.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison);

    /// <summary>
    /// The kinds the person set for files of the capture in <paramref name="framesFolder"/>, as its project (in the capture's
    /// <see cref="WorkdirName"/>) records them in the stack step's settings; none where there is no project.
    /// </summary>
    public static async Task<ImmutableArray<FrameReassignment>> ReassignmentsAsync(string framesFolder, CancellationToken cancellationToken = default)
    {
        var frames = Path.GetFullPath(framesFolder);
        var (project, _) = await WorkbenchProjectFile.ReadAsync(WorkdirOf(frames), cancellationToken);
        if (project is null)
        {
            return [];
        }

        foreach (var image in project.Images)
        {
            foreach (var edit in image.Edits.Where(edit => IsStackEditOf(image, edit, frames)))
            {
                if (edit.Settings?["reassign"] is { } node
                    && node.Deserialize(OperationSettingsJsonContext.Default.ImmutableArrayFrameReassignment) is { IsDefault: false } read)
                {
                    return FrameReassignment.Sorted(read);
                }
            }
        }

        return [];
    }

    /// <summary>
    /// Records that <paramref name="file"/> (its place inside the capture, or its path) is a <paramref name="kind"/>
    /// (<see cref="FrameType.None"/> leaves it out of the stack; null takes the reassignment back, so the detection stands again),
    /// in the stack step of every image of the project that stacks the capture. The step's key changes with it, so the stack
    /// runs again. Refused in words: no project (make one with <c>workbench new</c>), a file that is not in the capture, a kind
    /// no stack takes.
    /// </summary>
    public static async Task<ReassignResult> ReassignAsync(string framesFolder, string file, FrameType? kind, CancellationToken cancellationToken = default)
    {
        var frames = Path.GetFullPath(framesFolder);
        var project = WorkdirOf(frames);
        if (!File.Exists(WorkbenchProjectFile.PathIn(project)))
        {
            return new([], $"{frames} has no project yet: make one first (workbench new {framesFolder})");
        }

        if (kind is { } named && !FrameReassignment.Kinds.Contains(named))
        {
            return new([], $"{named} is no kind of frame a stack takes: say one of {FrameReassignment.KindWords}");
        }

        var place = Path.IsPathRooted(file) ? FrameReassignment.PlaceOf(frames, file) : FrameReassignment.Normalise(file);
        if (place is null || place.Length == 0 || !File.Exists(Path.Combine(frames, place.Replace('/', Path.DirectorySeparatorChar))))
        {
            return new([], $"{file} is not a file of the capture {frames}");
        }

        if (place.StartsWith(StackingPipeline.WorkdirName + "/", StringComparison.OrdinalIgnoreCase))
        {
            return new([], $"{file} is in {StackingPipeline.WorkdirName}, which the stack never reads");
        }

        string? refusal = null;
        ImmutableArray<FrameReassignment> now = [];
        var written = await SharedFile.UpdateJsonIfAsync(
            WorkbenchProjectFile.PathIn(project),
            WorkbenchJsonContext.Default.WorkbenchProject,
            current =>
            {
                if (current is null)
                {
                    refusal = $"{WorkbenchProjectFile.PathIn(project)} cannot be read";
                    return null;
                }

                var touched = false;
                var images = current.Images.Select(image =>
                {
                    var edits = image.Edits.Select(edit =>
                    {
                        if (!IsStackEditOf(image, edit, frames))
                        {
                            return edit;
                        }

                        touched = true;
                        var existing = edit.Settings?["reassign"]?.Deserialize(OperationSettingsJsonContext.Default.ImmutableArrayFrameReassignment) ?? [];
                        var next = FrameReassignment.Sorted(
                            existing.Where(each => !string.Equals(FrameReassignment.Normalise(each.Path), place, StringComparison.OrdinalIgnoreCase))
                                .Concat(kind is { } set ? [new FrameReassignment { Path = place, Kind = set }] : []));
                        now = next;
                        var settings = edit.Settings is null ? new System.Text.Json.Nodes.JsonObject() : (System.Text.Json.Nodes.JsonObject)edit.Settings.DeepClone();
                        if (next.IsEmpty)
                        {
                            settings.Remove("reassign");
                        }
                        else
                        {
                            settings["reassign"] = JsonSerializer.SerializeToNode(next, OperationSettingsJsonContext.Default.ImmutableArrayFrameReassignment);
                        }

                        return edit with { Settings = settings.Count == 0 ? null : settings };
                    });
                    return image with { Edits = [.. edits] };
                });
                var updated = current with { Images = [.. images] };
                if (!touched)
                {
                    refusal = $"no image of the project at {project} stacks {frames}";
                    return null;
                }

                return updated;
            },
            cancellationToken: cancellationToken);
        return written ? new ReassignResult(now, null) : new ReassignResult([], refusal ?? "the project was not written");
    }

    /// <summary>
    /// Adds the enhance laid out as its chain of single steps (<see cref="EnhanceChain"/>) for the image called
    /// <paramref name="imageName"/>, whose one edit is an enhance: the chain starts where that image starts. The chain
    /// is called <paramref name="name"/>, default <c>&lt;image&gt;-chain</c>; a name the project has is refused.
    /// </summary>
    public static async Task<ChainAddedResult> AddChainAsync(string folder, WorkbenchRunner runner, EnhanceOperation enhance, string imageName,
        string? name, Action<string>? say, CancellationToken cancellationToken = default)
    {
        var (project, unreadable) = await WorkbenchProjectFile.ReadAsync(folder, cancellationToken);
        if (project is null)
        {
            return RefusedChain(unreadable ?? $"{folder} holds no project");
        }

        switch (project.Images.FirstOrDefault(each => each.Name == imageName))
        {
            case null:
                return RefusedChain($"{folder} has no image named {imageName}");
            case { Edits: not [{ Operation: EnhanceOperation.Id, Enabled: true }] }:
                return RefusedChain($"{imageName} is laid out only when its one edit is an enhance");
            case { Result: not null }:
                return RefusedChain($"{imageName} starts from another image's extra result, which the chain does not take");
        }

        var image = project.Images.First(each => each.Name == imageName);
        var edit = image.Edits[0];
        var settings = edit.Settings is null
            ? new EnhanceSettings()
            : JsonSerializer.Deserialize(edit.Settings, OperationSettingsJsonContext.Default.EnhanceSettings) ?? new EnhanceSettings();
        var sourcePath = await StartingFileAsync(folder, runner, image, cancellationToken);
        if (sourcePath is null || !Image.TryReadFitsFile(sourcePath, out var source))
        {
            return RefusedChain($"{imageName} starts from {image.From ?? image.Path}, which has no file to read yet: run the project first (workbench run).");
        }

        return await AddAsync(folder, project, enhance, settings, source, image, imageName, name ?? imageName + "-chain", say, cancellationToken);
    }

    /// <summary>
    /// The "process this master" entry: adds an image that starts from the master of <paramref name="group"/> that the
    /// stack image <paramref name="stackImage"/> made, its edits the enhance at its defaults laid out as its chain of
    /// single steps. The chain is called <paramref name="name"/>, default <c>&lt;stack image&gt;-&lt;group&gt;</c> made
    /// safe and unique; a name given is refused when taken. Refused in words: a stack not run yet, and a group the
    /// stack did not give (the groups it gave are named).
    /// </summary>
    public static async Task<ChainAddedResult> AddMasterChainAsync(string folder, WorkbenchRunner runner, EnhanceOperation enhance, string stackImage,
        string group, string? name, Action<string>? say, CancellationToken cancellationToken = default)
    {
        var (project, unreadable) = await WorkbenchProjectFile.ReadAsync(folder, cancellationToken);
        if (project is null)
        {
            return RefusedChain(unreadable ?? $"{folder} holds no project");
        }

        if (project.Images.FirstOrDefault(each => each.Name == stackImage) is not { } stack)
        {
            return RefusedChain($"{folder} has no image named {stackImage}");
        }

        if (!stack.Edits.Any(static edit => edit is { Operation: StackOperation.Id, Enabled: true }))
        {
            return RefusedChain($"{stackImage} has no stack step, so it gives no masters");
        }

        var status = await runner.StatusAsync(folder, new WorkbenchRunOptions { Image = stackImage }, cancellationToken);
        var report = status.Images.FirstOrDefault(each => each.Name == stackImage);
        var step = report?.Steps.FirstOrDefault(static each => each.Operation == StackOperation.Id);
        if (step is not { State: StepState.Kept or StepState.Ran } || step.Key.Length == 0)
        {
            return RefusedChain($"the stack of {stackImage} has not been run: run it first (workbench run {folder} --image {stackImage})");
        }

        if (!step.Results.Contains(group, StringComparer.Ordinal))
        {
            return RefusedChain(step.Results.IsEmpty
                ? $"the stack of {stackImage} gave no masters, so there is no '{group}'"
                : $"the stack of {stackImage} gave no '{group}'; it gave {string.Join(", ", step.Results)}");
        }

        // The master is found through the store's own API: the key of the stack's result under the group's name.
        var store = await StepStore.OpenAsync(Path.Combine(folder, StepStore.FolderName), cancellationToken);
        var masterPath = store.Find(StepKey.OfResult(step.Key, group));
        if (masterPath is null || !Image.TryReadFitsFile(masterPath, out var source))
        {
            return RefusedChain($"the master '{group}' of {stackImage} is not in the store any more: run the stack again");
        }

        var start = new ProjectImage { From = stackImage, Result = group };
        var settings = new EnhanceSettings();
        var chainName = name ?? UniqueName(project, SafeName($"{stackImage}-{group}"));
        return await AddAsync(folder, project, enhance, settings, source, start, stackImage + "/" + group, chainName, say, cancellationToken);
    }

    private static async Task<ChainAddedResult> AddAsync(string folder, WorkbenchProject project, EnhanceOperation enhance, EnhanceSettings settings,
        Image source, ProjectImage start, string label, string chainName, Action<string>? say, CancellationToken cancellationToken)
    {
        if (!EnhanceOperation.TryOptions(settings, out var options, out _))
        {
            return RefusedChain("the enhance's settings are not ones it takes");
        }

        var program = await enhance.ProgramAsync(enhance.Start(source, options), settings, stretchedInPipeline: false, recombine: true, say, cancellationToken);
        var (chain, notFollowed) = EnhanceChain.ImagesFor(program, settings, source, chainName, start);
        if (notFollowed is not null)
        {
            return RefusedChain($"{label} cannot be laid out as single steps: {notFollowed}.");
        }

        // The project is read again under the file's lock and the chain appended to what it holds NOW: another press,
        // or the CLI while the app is open, may have added images since the snapshot this chain was laid out from.
        string? takenName = null;
        var written = await SharedFile.UpdateJsonIfAsync(
            WorkbenchProjectFile.PathIn(folder),
            WorkbenchJsonContext.Default.WorkbenchProject,
            current =>
            {
                var latest = current ?? project;
                if (chain.FirstOrDefault(each => latest.Images.Any(other => other.Name == each.Name)) is { } taken)
                {
                    takenName = taken.Name;
                    return null;
                }

                return latest with { Images = [.. latest.Images, .. chain] };
            },
            cancellationToken: cancellationToken);
        return written
            ? new ChainAddedResult(chain, null)
            : RefusedChain($"the project already has an image named {takenName}: name the chain with --name.");
    }

    // The file an image's edits start from: its own, or the last stored result of the image it starts from.
    private static async Task<string?> StartingFileAsync(string folder, WorkbenchRunner runner, ProjectImage image, CancellationToken cancellationToken)
    {
        if (image.From is null)
        {
            return Path.Combine(folder, image.Path);
        }

        var status = await runner.StatusAsync(folder, new WorkbenchRunOptions { Image = image.From }, cancellationToken);
        var from = status.Images.FirstOrDefault(each => each.Name == image.From);
        return from?.Steps.LastOrDefault() is { State: StepState.Kept, Output: { } output } ? Path.Combine(folder, output)
            : from is { Steps.IsEmpty: true, Path.Length: > 0 } ? Path.Combine(folder, from.Path)
            : null;
    }

    // A name with only letters, digits and - _ . so it is safe as a file name and on a command line.
    private static string SafeName(string text)
    {
        var safe = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            safe.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }

        return safe.ToString().Trim('-') is { Length: > 0 } trimmed ? trimmed : "chain";
    }

    // The name, or the name with -2, -3 and so on, such that no image the chain would add is already in the project.
    private static string UniqueName(WorkbenchProject project, string name)
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? name : $"{name}-{n}";
            if (!project.Images.Any(each => each.Name == candidate || each.Name == EnhanceChain.StarlessOf(candidate) || each.Name == EnhanceChain.StarsOf(candidate)))
            {
                return candidate;
            }
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static NewProjectResult Refused(string why) => new(null, why);

    private static ChainAddedResult RefusedChain(string why) => new([], why);
}
