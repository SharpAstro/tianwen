using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>What a detected capture says that decides which workflow fits (#2553, "Choose a workflow").</summary>
/// <param name="FramesFolder">The capture folder.</param>
/// <param name="LightCount">How many light frames the capture holds.</param>
/// <param name="Colour">The camera is colour; false for mono; null where the files do not say.</param>
/// <param name="Filters">The distinct filters of the lights, as the files name them; none where they name none.</param>
/// <param name="CometTrack">The comet track file's full path where the folder holds one; null otherwise.</param>
/// <param name="LightSlug">The name the stack gives the results of the main light group: what an image that starts from one of them names.</param>
/// <param name="TargetName">The target of the main light group, as the files name it; empty where they name none.</param>
public sealed record WorkflowFacts(
    string FramesFolder,
    int LightCount,
    bool? Colour,
    ImmutableArray<string> Filters,
    string? CometTrack,
    string LightSlug,
    string TargetName = "")
{
    /// <summary>The facts of <paramref name="capture"/>: the lights are those taken for lights after any reassignment, the main group is the one with most files.</summary>
    public static WorkflowFacts From(DetectedCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var lightGroups = capture.Groups.Where(static group => group.Kind == DetectedGroupKind.Lights).ToList();
        var lights = lightGroups.SelectMany(static group => group.Files).Where(static file => file.Kind == FrameType.Light).ToList();
        var known = lights.Select(static file => file.Colour).Where(static colour => colour.HasValue).Select(static colour => colour!.Value).ToList();
        bool? colour = known.Count == 0 ? null : known.All(static each => each);
        var main = lightGroups.OrderByDescending(static group => group.Files.Length).FirstOrDefault();
        var track = capture.Groups.Where(static group => group.Kind == DetectedGroupKind.CometTrack).SelectMany(static group => group.Files).FirstOrDefault();
        return new WorkflowFacts(
            capture.Folder,
            lights.Count,
            colour,
            [.. lights.Select(static file => file.Filter).Where(static filter => filter.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
            track?.FullPath,
            main?.Slug ?? string.Empty,
            main is null ? string.Empty : ObjectOf(main));
    }

    // The target is the first word group of the heading: "Lights · 10P/Tempel 2 · 60 s ...".
    private static string ObjectOf(DetectedGroup group)
        => group.Detail.Split(" · ", 2)[0] is { } first && !first.EndsWith(" s", StringComparison.Ordinal) ? first : string.Empty;
}

/// <summary>Whether a workflow fits a capture, and one line saying why or why not.</summary>
/// <param name="Fits">The workflow fits.</param>
/// <param name="Why">One line: what makes it fit (colour camera, one filter, a comet track) or the first thing that rules it out.</param>
public readonly record struct WorkflowFit(bool Fits, string Why);

/// <summary>
/// A built-in workflow: a fixed edit list with a rule for when it fits (#2553). No gates: a gate is W9's
/// (docs/plans/stacking-workbench.md, "Workflows: operations in order, with gates").
/// </summary>
public sealed record WorkflowTemplate
{
    /// <summary>The id a verb names it by.</summary>
    public required string Id { get; init; }

    /// <summary>The name shown: <c>Comet, colour camera</c>.</summary>
    public required string Title { get; init; }

    /// <summary>What it does, in one line.</summary>
    public required string Summary { get; init; }

    /// <summary>Whether it fits the facts, and why.</summary>
    public required Func<WorkflowFacts, WorkflowFit> Fit { get; init; }

    /// <summary>The images and edits it writes into a project for these facts.</summary>
    public required Func<WorkflowFacts, ImmutableArray<ProjectImage>> Build { get; init; }
}

/// <summary>A workflow with its answer for one capture.</summary>
public readonly record struct WorkflowAnswer(WorkflowTemplate Template, WorkflowFit Fit, bool Best);

/// <summary>The built-in workflows, and which fits.</summary>
public static class BuiltInWorkflows
{
    /// <summary>Comet, colour camera.</summary>
    public const string CometColour = "comet-colour";

    /// <summary>Deep sky, colour camera.</summary>
    public const string DeepSkyColour = "deep-sky-colour";

    /// <summary>Quick look.</summary>
    public const string QuickLook = "quick-look";

    /// <summary>Mono camera, L R G B filters.</summary>
    public const string MonoLrgb = "mono-lrgb";

    /// <summary>Mono camera, narrowband filters.</summary>
    public const string Narrowband = "narrowband";

    /// <summary>The empty workflow.</summary>
    public const string Empty = "empty";

    /// <summary>The workflows, in the order the best fit is chosen: the first that fits is the best. The empty one fits every capture, so it is the last resort.</summary>
    public static ImmutableArray<WorkflowTemplate> All { get; } =
    [
        new WorkflowTemplate
        {
            Id = CometColour,
            Title = "Comet, colour camera",
            Summary = "Stacks the night on the comet and on the stars, finishes each, and blends the comet onto the stars.",
            Fit = CometFit,
            Build = BuildComet,
        },
        new WorkflowTemplate
        {
            Id = DeepSkyColour,
            Title = "Deep sky, colour camera",
            Summary = "Stacks, balances the colour, takes the gradient out, separates the stars, denoises the plate and puts the stars back.",
            Fit = DeepSkyFit,
            Build = BuildDeepSky,
        },
        new WorkflowTemplate
        {
            Id = MonoLrgb,
            Title = "Mono LRGB",
            Summary = "Stacks each filter of a mono camera; the filters are then combined in the workbench.",
            Fit = static facts => MonoRefusal(facts, "L, R, G and B", LrgbNames) ?? new(true, $"mono camera, {FilterText(facts)}"),
            Build = BuildStackOnly,
        },
        new WorkflowTemplate
        {
            Id = Narrowband,
            Title = "Narrowband SHO",
            Summary = "Stacks each narrowband filter of a mono camera; the filters are then combined in the workbench.",
            Fit = static facts => MonoRefusal(facts, "Ha, OIII and SII", NarrowbandNames) ?? new(true, $"mono camera, {FilterText(facts)}"),
            Build = BuildStackOnly,
        },
        new WorkflowTemplate
        {
            Id = QuickLook,
            Title = "Quick look",
            Summary = "Stacks and balances the colour, nothing more: to see what the night gave.",
            Fit = static facts => facts.LightCount > 0
                ? new(true, $"{facts.LightCount} {(facts.LightCount == 1 ? "light" : "lights")}: a stack and a white balance, for any camera")
                : NoLights,
            Build = BuildQuickLook,
        },
        new WorkflowTemplate
        {
            Id = Empty,
            Title = "Empty",
            Summary = "No steps: the capture only, to add them one by one.",
            Fit = static _ => new(true, "no steps; fits any folder"),
            Build = BuildEmpty,
        },
    ];

    /// <summary>The workflow of this id (case does not matter), or null.</summary>
    public static WorkflowTemplate? Find(string id) => All.FirstOrDefault(each => string.Equals(each.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every workflow with its answer for <paramref name="facts"/>, in the order of <see cref="All"/>; the first that fits is marked best.</summary>
    public static ImmutableArray<WorkflowAnswer> Answer(WorkflowFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var fits = All.Select(each => (Template: each, Fit: each.Fit(facts))).ToList();
        var best = fits.FindIndex(static each => each.Fit.Fits);
        return [.. fits.Select((each, index) => new WorkflowAnswer(each.Template, each.Fit, index == best))];
    }

    private static readonly WorkflowFit NoLights = new(false, "the folder holds no light frames");

    private static string FilterText(WorkflowFacts facts)
        => facts.Filters.Length switch
        {
            0 => "no filter named",
            1 => $"one filter ({facts.Filters[0]})",
            _ => $"{facts.Filters.Length} filters ({string.Join(", ", facts.Filters)})",
        };

    // The colour camera and filter conditions shared by the two colour workflows; null when they hold.
    private static WorkflowFit? ColourRefusal(WorkflowFacts facts)
        => facts.LightCount == 0 ? NoLights
            : facts.Colour == false ? new WorkflowFit(false, "the camera is mono: this workflow is for a colour camera's frames")
            : facts.Colour is null ? new WorkflowFit(false, "the files do not say whether the camera is colour")
            : facts.Filters.Length > 1 ? new WorkflowFit(false, $"{facts.Filters.Length} filters ({string.Join(", ", facts.Filters)}): this workflow stacks one filter")
            : null;

    private static readonly string[] LrgbNames = ["l", "lum", "luminance", "r", "red", "g", "green", "b", "blue"];
    private static readonly string[] NarrowbandNames = ["ha", "h-alpha", "oiii", "o3", "sii", "s2"];

    // The conditions of the two mono workflows: a mono camera, and filters of the kind the workflow combines; null when they hold.
    private static WorkflowFit? MonoRefusal(WorkflowFacts facts, string wanted, string[] names)
        => facts.LightCount == 0 ? NoLights
            : facts.Colour == true ? new WorkflowFit(false, "the camera is colour: this workflow is for a mono camera's frames, one filter at a time")
            : facts.Filters.Length < 2 ? new WorkflowFit(false, facts.Filters.Length == 0
                ? $"no filter is named in the frames, and this workflow combines {wanted}"
                : $"one filter ({facts.Filters[0]}): this workflow combines {wanted}")
            : !facts.Filters.Any(filter => names.Any(name => filter.Contains(name, StringComparison.OrdinalIgnoreCase)))
                ? new WorkflowFit(false, $"the filters ({string.Join(", ", facts.Filters)}) are not {wanted}")
                : null;

    private static ImmutableArray<ProjectImage> BuildStackOnly(WorkflowFacts facts)
        => [new ProjectImage { Name = BaseName(facts), Path = facts.FramesFolder, Edits = [Edit(StackOperation.Id)] }];

    private static WorkflowFit CometFit(WorkflowFacts facts)
        => ColourRefusal(facts) ?? (facts.CometTrack is null
            ? new WorkflowFit(false, "no comet track in the folder (tianwen comet track writes one)")
            : new WorkflowFit(true, $"colour camera, {FilterText(facts)}, comet track {Path.GetFileName(facts.CometTrack)}"));

    private static WorkflowFit DeepSkyFit(WorkflowFacts facts)
        => ColourRefusal(facts) ?? new WorkflowFit(true, facts.CometTrack is null
            ? $"colour camera, {FilterText(facts)}, no comet track"
            : $"colour camera, {FilterText(facts)}; a comet track is here too, which the comet workflow uses");

    private static string BaseName(WorkflowFacts facts)
    {
        var name = Path.GetFileName(facts.FramesFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Length > 0 ? name : "capture";
    }

    private static ProjectEdit Edit(string operation, JsonObject? settings = null, params string[] inputs)
        => new() { Operation = operation, Settings = settings, Inputs = [.. inputs] };

    private static ImmutableArray<ProjectImage> BuildEmpty(WorkflowFacts facts)
        => [new ProjectImage { Name = BaseName(facts), Path = facts.FramesFolder }];

    private static ImmutableArray<ProjectImage> BuildQuickLook(WorkflowFacts facts)
    {
        var name = BaseName(facts);
        return
        [
            new ProjectImage { Name = name, Path = facts.FramesFolder, Edits = [Edit(StackOperation.Id)] },
            new ProjectImage { Name = name + "-look", From = name, Result = facts.LightSlug, Edits = [Edit(WhiteBalanceOperation.Id)] },
        ];
    }

    // The enhance's own order for a colour plate (EnhanceChain): the plate, its stars, and the result that puts them back.
    private static ImmutableArray<ProjectImage> BuildDeepSky(WorkflowFacts facts)
    {
        var name = BaseName(facts);
        var starless = EnhanceChain.StarlessOf(name);
        var stars = EnhanceChain.StarsOf(name);
        return
        [
            new ProjectImage { Name = name + "-stack", Path = facts.FramesFolder, Edits = [Edit(StackOperation.Id)] },
            new ProjectImage
            {
                Name = starless, From = name + "-stack", Result = facts.LightSlug,
                Edits =
                [
                    Edit(WhiteBalanceOperation.Id),
                    Edit(GradientOperation.Id),
                    Edit(RemoveStarsOperation.Id, new JsonObject { ["catalogue"] = "none" }),
                    Edit(DenoiseOperation.Id),
                ],
            },
            new ProjectImage { Name = stars, From = starless, Result = RemoveStarsOperation.Stars, Edits = [Edit(ScnrOperation.Id)] },
            new ProjectImage { Name = name, From = starless, Edits = [Edit(AddStarsOperation.Id, null, stars)] },
        ];
    }

    // One comet run gives the comet master (the slug) and the star layer (slug + "_stars"); each is finished on its own and the
    // comet is blended onto the stars.
    private static ImmutableArray<ProjectImage> BuildComet(WorkflowFacts facts)
    {
        var name = BaseName(facts);
        var track = facts.CometTrack ?? string.Empty;
        return
        [
            new ProjectImage { Name = "comet-track", Path = track },
            new ProjectImage { Name = name + "-comet-stack", Path = facts.FramesFolder, Edits = [Edit(CometStackOperation.Id, null, "comet-track")] },
            new ProjectImage
            {
                Name = name + "-comet", From = name + "-comet-stack", Result = facts.LightSlug,
                Edits = [Edit(GradientOperation.Id), Edit(DenoiseOperation.Id)],
            },
            new ProjectImage
            {
                Name = name + "-stars", From = name + "-comet-stack", Result = facts.LightSlug + CometStackOperation.StarsEnding,
                Edits = [Edit(WhiteBalanceOperation.Id), Edit(GradientOperation.Id), Edit(DenoiseOperation.Id)],
            },
            new ProjectImage { Name = name, From = name + "-stars", Edits = [Edit(AddStarsOperation.Id, null, name + "-comet")] },
        ];
    }
}

/// <summary>A workflow applied to a project, or why it was not.</summary>
/// <param name="Project">The project written, null when refused.</param>
/// <param name="Replaced">How many hand changes to the previous workflow's steps were replaced (<see cref="WorkbenchWorkflows.HandChangesAsync"/>).</param>
/// <param name="Refusal">Why not, in words; null when applied.</param>
public readonly record struct ApplyWorkflowResult(WorkbenchProject? Project, int Replaced, string? Refusal);

/// <summary>A step added or removed, or why not.</summary>
/// <param name="Project">The project written, null when refused.</param>
/// <param name="Refusal">Why not, in words; null when done.</param>
public readonly record struct StepChangeResult(WorkbenchProject? Project, string? Refusal);

/// <summary>What a row of the project's tree is.</summary>
public enum WorkflowRowKind
{
    /// <summary>A file the steps take and that has no steps of its own (the comet track).</summary>
    Source,

    /// <summary>An image that has no step yet.</summary>
    Image,

    /// <summary>A step.</summary>
    Step,

    /// <summary>A step that gives more than one result which an image goes on from (remove stars): its branches follow, indented.</summary>
    Split,

    /// <summary>A step that takes another image of the project (add stars): the branches come back out into it.</summary>
    Join,
}

/// <summary>One row of the project's tree.</summary>
/// <param name="Kind">What the row is.</param>
/// <param name="Depth">How far in it is drawn: 0 is the trunk, a split's branches are one deeper, a join is back out.</param>
/// <param name="Image">The image it belongs to.</param>
/// <param name="EditIndex">The edit's index in that image; -1 for a source or an image with no step.</param>
/// <param name="Label">The row's words: the step's title, or the image's name.</param>
/// <param name="Detail">Anything beside it: what a join takes, the result a branch starts from; empty otherwise.</param>
public sealed record WorkflowRow(WorkflowRowKind Kind, int Depth, string Image, int EditIndex, string Label, string Detail);

/// <summary>
/// The steps of a project as a workflow: applying a built-in one, adding and removing a step, counting hand changes, and the
/// tree the screen draws (#2553). Every host (the processing app and the <c>workbench workflows|apply</c> verbs) calls these
/// and shows what they answer.
/// </summary>
public static class WorkbenchWorkflows
{
    /// <summary>
    /// The edits of the images as a list of lines, one per edit, naming the image, the operation, its settings (leaving the
    /// reassignments out, which are the capture's and not the steps'), its switch and its inputs; never what a run stamped on it.
    /// </summary>
    internal static ImmutableArray<string> SignatureOf(ImmutableArray<ProjectImage> images)
        => [.. images.SelectMany(image => image.Edits.Select((edit, index) =>
        {
            var settings = edit.Settings is null ? new JsonObject() : (JsonObject)edit.Settings.DeepClone();
            settings.Remove("reassign");
            return string.Create(CultureInfo.InvariantCulture,
                $"{image.Name}|{image.Path}|{image.From}|{image.Result}|{index}|{edit.Operation}|{settings.ToJsonString()}|{edit.Enabled}|{string.Join(",", edit.Inputs)}");
        })).Order(StringComparer.Ordinal)];

    /// <summary>How many steps of <paramref name="project"/> differ from the workflow it was made from: a step added, removed or changed counts one. 0 for a project that did not start from a workflow.</summary>
    public static int HandChanges(WorkbenchProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Workflow is null)
        {
            return 0;
        }

        var now = SignatureOf(project.Images).ToList();
        var made = project.WorkflowOrigin.ToList();
        var gone = new List<string>();
        foreach (var line in made)
        {
            if (!now.Remove(line))
            {
                gone.Add(line);
            }
        }

        // What is left in now is what the workflow did not write. A step of the same image and operation that is gone and
        // present with other settings is one step changed, one change; every other step gone or present is a change of its own.
        static string Pair(string line)
        {
            var parts = line.Split('|', 7);
            return $"{parts[0]}|{parts[1]}|{parts[2]}|{parts[3]}|{parts[5]}";
        }

        var changes = 0;
        foreach (var line in gone)
        {
            var match = now.FindIndex(each => Pair(each) == Pair(line));
            if (match >= 0)
            {
                now.RemoveAt(match);
            }

            changes++;
        }

        return changes + now.Count;
    }

    /// <summary>The hand changes of the project in the capture's workdir (<see cref="HandChanges"/>); 0 where there is no project.</summary>
    public static async Task<int> HandChangesAsync(string framesFolder, CancellationToken cancellationToken = default)
    {
        var (project, _) = await WorkbenchProjectFile.ReadAsync(WorkbenchProjects.WorkdirOf(framesFolder), cancellationToken);
        return project is null ? 0 : HandChanges(project);
    }

    /// <summary>
    /// Writes the workflow <paramref name="workflowId"/> for <paramref name="facts"/> into the project in the capture's workdir (made
    /// when there is none): its images and edits replace the project's, its name and what it wrote are recorded so a later switch can tell
    /// whether the steps were changed by hand, and the person's reassignments of frames are kept. The step store is not touched: a step whose
    /// settings and inputs are the same finds its result by its key. A workflow that does not fit is applied all the same: the screen shows
    /// the reason and the person chooses.
    /// </summary>
    public static async Task<ApplyWorkflowResult> ApplyAsync(string framesFolder, string workflowId, WorkflowFacts facts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var frames = Path.GetFullPath(framesFolder);
        if (BuiltInWorkflows.Find(workflowId) is not { } template)
        {
            return new(null, 0, $"there is no workflow '{workflowId}': say one of {string.Join(", ", BuiltInWorkflows.All.Select(static each => each.Id))}");
        }

        if (!Directory.Exists(frames))
        {
            return new(null, 0, $"the capture folder {frames} does not exist");
        }

        var workdir = WorkbenchProjects.WorkdirOf(frames);
        var reassigned = await WorkbenchProjects.ReassignmentsAsync(frames, cancellationToken);
        var images = template.Build(facts with { FramesFolder = frames });
        if (!reassigned.IsDefaultOrEmpty)
        {
            images = [.. images.Select(image => image with
            {
                Edits = [.. image.Edits.Select(edit =>
                {
                    if (edit.Operation is not (StackOperation.Id or CometStackOperation.Id) || image.From is not null || image.Path != frames)
                    {
                        return edit;
                    }

                    var settings = edit.Settings is null ? new JsonObject() : (JsonObject)edit.Settings.DeepClone();
                    settings["reassign"] = JsonSerializer.SerializeToNode(reassigned, OperationSettingsJsonContext.Default.ImmutableArrayFrameReassignment);
                    return edit with { Settings = settings };
                })],
            })];
        }

        var origin = SignatureOf(images);
        var replaced = 0;
        WorkbenchProject? written = null;
        Directory.CreateDirectory(workdir);
        var path = WorkbenchProjectFile.PathIn(workdir);
        if (File.Exists(path))
        {
            var ok = await SharedFile.UpdateJsonIfAsync(path, WorkbenchJsonContext.Default.WorkbenchProject, current =>
            {
                if (current is null)
                {
                    return null;
                }

                replaced = HandChanges(current);
                written = current with { Images = images, Workflow = template.Id, WorkflowOrigin = origin };
                return written;
            }, cancellationToken: cancellationToken);
            return ok && written is not null ? new(written, replaced, null) : new(null, 0, $"{path} cannot be read");
        }

        written = new WorkbenchProject { Images = images, Workflow = template.Id, WorkflowOrigin = origin };
        await WorkbenchProjectFile.WriteAsync(workdir, written, cancellationToken);
        return new(written, 0, null);
    }

    /// <summary>
    /// Adds a step of the operation <paramref name="operationId"/> at its defaults to the image <paramref name="imageName"/> of the project in
    /// <paramref name="projectFolder"/>, after the edit at index <paramref name="afterIndex"/> (-1 puts it first). Refused in words: no project, no such image,
    /// an index the image does not have, an operation the catalogue does not have, and one that takes images the step does not name
    /// (<paramref name="inputs"/> names them, one for each of the operation's required extra inputs).
    /// </summary>
    public static async Task<StepChangeResult> AddStepAsync(string projectFolder, OperationCatalog catalog, string imageName, int afterIndex, string operationId,
        IReadOnlyList<string>? inputs = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.TryGet(operationId, out var operation))
        {
            return new(null, $"there is no operation '{operationId}'");
        }

        var needed = operation.Description.ExtraInputs.Where(static role => !role.Optional).ToList();
        inputs ??= [];
        if (inputs.Count < needed.Count)
        {
            return new(null, $"'{operationId}' takes {needed.Count} other image(s): {string.Join(", ", needed.Select(static role => role.Name))}");
        }

        return await ChangeAsync(projectFolder, imageName, (image, project) =>
        {
            if (afterIndex < -1 || afterIndex >= image.Edits.Length)
            {
                return ($"{imageName} has {image.Edits.Length} step(s): there is no step {afterIndex} to add after", null);
            }

            if (inputs.FirstOrDefault(each => project.Images.All(other => other.Name != each)) is { } missing)
            {
                return ($"the project has no image named {missing}", null);
            }

            var edit = new ProjectEdit { Operation = operationId, Inputs = [.. inputs] };
            return (null, image with { Edits = [.. image.Edits.Take(afterIndex + 1), edit, .. image.Edits.Skip(afterIndex + 1)] });
        }, cancellationToken);
    }

    /// <summary>
    /// Removes the edit at <paramref name="editIndex"/> from the image <paramref name="imageName"/> of the project in <paramref name="projectFolder"/>.
    /// Refused in words: no project, no such image or step, and a step another image's steps start from (a split's result the branches take).
    /// </summary>
    public static Task<StepChangeResult> RemoveStepAsync(string projectFolder, OperationCatalog catalog, string imageName, int editIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ChangeAsync(projectFolder, imageName, (image, project) =>
        {
            if (editIndex < 0 || editIndex >= image.Edits.Length)
            {
                return ($"{imageName} has {image.Edits.Length} step(s): there is no step {editIndex}", null);
            }

            var edit = image.Edits[editIndex];
            var gives = catalog.TryGet(edit.Operation, out var operation) ? operation.Description : null;
            if (gives is not null && (gives.NamedByRun || !gives.ExtraResults.IsDefaultOrEmpty)
                && project.Images.FirstOrDefault(other => other.From == imageName && other.Result is { Length: > 0 }) is { } branch)
            {
                return ($"the image {branch.Name} starts from a result of {edit.Operation}: remove {branch.Name} first", null);
            }

            return (null, image with { Edits = [.. image.Edits.Where((_, index) => index != editIndex)] });
        }, cancellationToken);
    }

    /// <summary>
    /// Writes one setting of the edit at <paramref name="editIndex"/> of the image <paramref name="imageName"/> into the project in
    /// <paramref name="projectFolder"/>: the value joins the settings already there, and the result must read as the operation's settings
    /// (<see cref="IOperation.TryReadSettings"/>), or nothing is written and the refusal is said in words. A null <paramref name="value"/> leaves
    /// the setting out (its own value). The step's key changes with the settings, so the runner then answers it as not made.
    /// </summary>
    public static Task<StepChangeResult> SetStepSettingAsync(string projectFolder, OperationCatalog catalog, string imageName, int editIndex, string setting,
        JsonNode? value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ChangeAsync(projectFolder, imageName, (image, project) =>
        {
            if (editIndex < 0 || editIndex >= image.Edits.Length)
            {
                return ($"{imageName} has {image.Edits.Length} step(s): there is no step {editIndex}", null);
            }

            var edit = image.Edits[editIndex];
            if (!catalog.TryGet(edit.Operation, out var operation))
            {
                return ($"there is no operation '{edit.Operation}'", null);
            }

            if (!OperationSettingsForm.Describe(operation).Any(field => string.Equals(field.Name, setting, StringComparison.Ordinal)))
            {
                return ($"'{edit.Operation}' has no setting '{setting}'", null);
            }

            var settings = edit.Settings?.DeepClone() as JsonObject ?? [];
            settings[setting] = value?.DeepClone();
            if (!operation.TryReadSettings(settings.ToJsonString(), out _, out var refusal))
            {
                return (refusal, null);
            }

            return (null, image with { Edits = [.. image.Edits.Select((each, index) => index == editIndex ? each with { Settings = settings } : each)] });
        }, cancellationToken);
    }

    /// <summary>
    /// Switches the edit at <paramref name="editIndex"/> on or off. An edit that is off is passed over: the edit after it takes what the edit
    /// before it made, and switching it on again finds its earlier result in the store.
    /// </summary>
    public static Task<StepChangeResult> SetStepEnabledAsync(string projectFolder, string imageName, int editIndex, bool enabled,
        CancellationToken cancellationToken = default)
        => ChangeAsync(projectFolder, imageName, (image, _) =>
        {
            if (editIndex < 0 || editIndex >= image.Edits.Length)
            {
                return ($"{imageName} has {image.Edits.Length} step(s): there is no step {editIndex}", null);
            }

            return (null, image with { Edits = [.. image.Edits.Select((each, index) => index == editIndex ? each with { Enabled = enabled } : each)] });
        }, cancellationToken);

    private static async Task<StepChangeResult> ChangeAsync(string projectFolder, string imageName,
        Func<ProjectImage, WorkbenchProject, (string? Refusal, ProjectImage? Image)> change, CancellationToken cancellationToken)
    {
        var path = WorkbenchProjectFile.PathIn(projectFolder);
        if (!File.Exists(path))
        {
            return new(null, $"{projectFolder} holds no {WorkbenchProjectFile.FileName}");
        }

        string? refusal = null;
        WorkbenchProject? written = null;
        var ok = await SharedFile.UpdateJsonIfAsync(path, WorkbenchJsonContext.Default.WorkbenchProject, current =>
        {
            if (current is null)
            {
                refusal = $"{path} cannot be read";
                return null;
            }

            if (current.Images.FirstOrDefault(each => each.Name == imageName) is not { } image)
            {
                refusal = $"the project has no image named {imageName}";
                return null;
            }

            var (why, changed) = change(image, current);
            if (changed is null)
            {
                refusal = why;
                return null;
            }

            written = current with { Images = [.. current.Images.Select(each => each.Name == imageName ? changed : each)] };
            return written;
        }, cancellationToken: cancellationToken);
        return ok && written is not null ? new(written, null) : new(null, refusal ?? "the project was not written");
    }

    /// <summary>
    /// The project's steps as a tree for the screen to draw, worked out from the images' starts and the edits' inputs. An image that starts
    /// from another image's named result is a <b>branch</b>: drawn one deeper, right after the step that gave the result (a split). An image
    /// that goes on from another's end continues it. A step that takes another image is a <b>join</b>; where it takes a sibling branch it is
    /// drawn back out, one level up from the branch it continues.
    /// </summary>
    public static ImmutableArray<WorkflowRow> Tree(WorkbenchProject project, OperationCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var rows = new List<WorkflowRow>();
        var byName = project.Images.ToDictionary(static image => image.Name, StringComparer.Ordinal);
        var referenced = project.Images.SelectMany(static image => image.Edits.SelectMany(static edit => edit.Inputs)).ToHashSet(StringComparer.Ordinal);

        string Title(string id) => catalog is not null && catalog.TryGet(id, out var op) ? op.Description.Title : id;

        // The branch an image hangs from (its parent image) and the depth it is drawn at.
        int Walk(ProjectImage image, int depth, string? branchParent)
        {
            if (image.Edits.IsEmpty)
            {
                rows.Add(new WorkflowRow(image.Path.Length > 0 && referenced.Contains(image.Name) ? WorkflowRowKind.Source : WorkflowRowKind.Image, depth, image.Name, -1, image.Name,
                    image.Result is { Length: > 0 } r ? "from " + r : string.Empty));
            }

            for (var i = 0; i < image.Edits.Length; i++)
            {
                var edit = image.Edits[i];
                var branches = project.Images.Where(other => other.From == image.Name && other.Result is { Length: > 0 } && GivenBy(image, i, other.Result, catalog)).ToList();
                // A split: the step gives results that more than one image goes on from, or an extra result (the stars). A stack that
                // gives one group's result to the one image after it is not a split, only a step.
                var split = branches.Count >= 2 || (branches.Count == 1 && GivesExtra(edit, branches[0].Result, catalog));
                var kind = split ? WorkflowRowKind.Split : edit.Inputs.Length > 0 ? WorkflowRowKind.Join : WorkflowRowKind.Step;
                var joinsOut = edit.Inputs.Length > 0 && branchParent is not null
                    && edit.Inputs.Any(input => byName.TryGetValue(input, out var sibling) && sibling.From is not null && BranchParentOf(sibling, byName) == branchParent);
                var rowDepth = joinsOut ? Math.Max(0, depth - 1) : depth;
                rows.Add(new WorkflowRow(kind, rowDepth, image.Name, i, Title(edit.Operation), edit.Inputs.Length > 0 ? "takes " + string.Join(", ", edit.Inputs) : string.Empty));
                foreach (var branch in branches)
                {
                    Walk(branch, split ? depth + 1 : depth, split ? image.Name : branchParent);
                }
            }

            foreach (var next in project.Images.Where(other => other.From == image.Name && other.Result is not { Length: > 0 }))
            {
                // A continuation of a branch that takes a sibling branch comes back out; otherwise it stays where its parent is.
                var comesOut = next.Edits.Any(edit => edit.Inputs.Any(input => byName.TryGetValue(input, out var sibling) && sibling.From is not null && branchParent is not null
                    && BranchParentOf(sibling, byName) == branchParent));
                Walk(next, comesOut ? Math.Max(0, depth - 1) : depth, comesOut ? null : branchParent);
            }

            return depth;
        }

        foreach (var root in project.Images.Where(static image => image.From is null))
        {
            Walk(root, 0, null);
        }

        return [.. rows];
    }

    // The image a branch image hangs from, following continuations back to the one that starts from a named result; null for a root.
    private static string? BranchParentOf(ProjectImage image, Dictionary<string, ProjectImage> byName)
    {
        for (var guard = 0; guard < 64 && image.From is { } from; guard++)
        {
            if (image.Result is { Length: > 0 })
            {
                return from;
            }

            if (!byName.TryGetValue(from, out var parent))
            {
                return null;
            }

            image = parent;
        }

        return null;
    }

    private static bool GivesExtra(ProjectEdit edit, string? result, OperationCatalog? catalog)
        => catalog is not null && catalog.TryGet(edit.Operation, out var op)
            && op.Description.ExtraResults.Any(role => string.Equals(role.Name, result, StringComparison.Ordinal));

    // Whether edit index of the image is the step that gives the named result: the named-by-run or multi-result step the branch starts from.
    // Without a catalogue, the last edit does.
    private static bool GivenBy(ProjectImage image, int index, string result, OperationCatalog? catalog)
    {
        if (catalog is null)
        {
            return index == image.Edits.Length - 1;
        }

        for (var i = image.Edits.Length - 1; i >= 0; i--)
        {
            if (catalog.TryGet(image.Edits[i].Operation, out var op)
                && (op.Description.NamedByRun || op.Description.ExtraResults.Any(role => string.Equals(role.Name, result, StringComparison.Ordinal))))
            {
                return i == index;
            }
        }

        return index == image.Edits.Length - 1;
    }
}
