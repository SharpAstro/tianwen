using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>Where a step stands.</summary>
public enum StepState
{
    /// <summary>The edit is switched off: the edit after it takes what the edit before it made.</summary>
    Off,

    /// <summary>A derived step: made from its input when the step after it asks, and never stored.</summary>
    Derived,

    /// <summary>Its result is in the store, and nothing ran.</summary>
    Kept,

    /// <summary>Its result is not in the store and what was asked for needs it, so a run makes it.</summary>
    WillRun,

    /// <summary>Its result is not in the store and nothing asked for needs it: a step after it has its own result
    /// there. It is made again only when something asks for it.</summary>
    NotNeeded,

    /// <summary>This run made its result.</summary>
    Ran,

    /// <summary>It cannot be planned, or it ran and gave no result; the message says why.</summary>
    Refused,

    /// <summary>A step before it, or an image it takes, gave no result.</summary>
    Blocked,
}

/// <summary>One edit of an image, as a status or a run found it.</summary>
/// <param name="Index">Its place in the edit list, from 0.</param>
/// <param name="Operation">The operation's id.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Key">The step's key; empty for an edit that is off or could not be planned.</param>
/// <param name="Output">Its result in the store, when it is there.</param>
/// <param name="CommandLine">The command that does this step from a terminal, program name first; empty where there is no key.</param>
/// <param name="Message">Why it was refused or blocked; null otherwise.</param>
/// <param name="ChangedSinceLastRun">True when the project recorded another key for this edit: its settings, or
/// something before it, changed since a run last made it.</param>
public sealed record StepReport(
    int Index,
    string Operation,
    StepState State,
    string Key,
    string? Output,
    ImmutableArray<string> CommandLine,
    string? Message,
    bool ChangedSinceLastRun);

/// <summary>One image of a project, as a status or a run found it.</summary>
/// <param name="Name">The image's name.</param>
/// <param name="Path">Its original file.</param>
/// <param name="Key">The file's key; empty when it could not be read.</param>
/// <param name="Steps">Its edits, in order.</param>
/// <param name="Refusal">Why the image cannot be worked on at all (its file is not there); null otherwise.</param>
/// <param name="FileChangedSinceLastRun">True when the file is not the one a run last read.</param>
public sealed record ImageReport(
    string Name,
    string Path,
    string Key,
    ImmutableArray<StepReport> Steps,
    string? Refusal,
    bool FileChangedSinceLastRun);

/// <summary>What a status or a run found.</summary>
/// <param name="Images">Every image of the project.</param>
/// <param name="Refusal">Why the project could not be worked on, or an export could not be written; null otherwise.</param>
/// <param name="Exported">The file an export wrote; null when none was asked for or it failed.</param>
public sealed record WorkbenchReport(ImmutableArray<ImageReport> Images, string? Refusal, string? Exported = null)
{
    /// <summary>Something a person should know that is not a refusal (the run's results could not be recorded in an
    /// unreadable project file); the run still succeeded. Null when there is nothing to say.</summary>
    public string? Note { get; init; }

    /// <summary>True when nothing was refused: the project read, every image has its file, and no step is refused or blocked.</summary>
    public bool Succeeded => Refusal is null
        && Images.All(static image => image.Refusal is null
            && image.Steps.All(static step => step.State is not (StepState.Refused or StepState.Blocked)));
}

/// <summary>What a run is asked for beyond "make every image".</summary>
public sealed record WorkbenchRunOptions
{
    /// <summary>Only this image, with whatever its edits take; null for every image of the project.</summary>
    public string? Image { get; init; }

    /// <summary>Also write <see cref="Image"/>'s final result as this file. A derived result (a crop) has no file of
    /// its own, so this is how one is had: the operation's own routine writes it, as its verb would.</summary>
    public string? ExportPath { get; init; }

    /// <summary>Takes each line a step reports, with the step named in front.</summary>
    public Action<string>? Say { get; init; }
}

/// <summary>
/// Runs a project's edit lists (docs/plans/stacking-workbench.md, W1): works out every step's key, takes from the step
/// store each step whose key is there, and runs the rest.
/// </summary>
/// <remarks>
/// <para><b>Nothing is decided by a date.</b> A step runs when its key is not in the store and something asked for
/// needs it. A project run twice runs nothing the second time; a changed setting changes the key of its step and of
/// the steps after it, so those run and the ones before it do not (the plan's rule 2).</para>
/// <para><b>Work is pulled from the end.</b> A step whose result is in the store needs nothing before it, so an
/// earlier result that is gone is not made again for its sake.</para>
/// <para><b>A stored step reads a file and writes a file.</b> Only a derived step hands its result over in memory,
/// and only to the step after it.</para>
/// <para>Call it off a render thread: it digests the original files and reads and writes images.</para>
/// </remarks>
public sealed class WorkbenchRunner(OperationCatalog operations)
{
    /// <summary>What a run would do, with nothing run and nothing written.</summary>
    /// <param name="projectFolder">The project's folder.</param>
    /// <param name="image">Only what this image needs counts as needed; null for every image.</param>
    /// <param name="cancellationToken">Ends the work.</param>
    public Task<WorkbenchReport> StatusAsync(string projectFolder, string? image = null, CancellationToken cancellationToken = default)
        => GoAsync(projectFolder, new WorkbenchRunOptions { Image = image }, run: false, cancellationToken);

    /// <summary>Makes what is asked for, running only the steps whose results are not in the store, and records each
    /// step's key and result in the project file.</summary>
    public Task<WorkbenchReport> RunAsync(string projectFolder, WorkbenchRunOptions? options = null, CancellationToken cancellationToken = default)
        => GoAsync(projectFolder, options ?? new WorkbenchRunOptions(), run: true, cancellationToken);

    private async Task<WorkbenchReport> GoAsync(string projectFolder, WorkbenchRunOptions options, bool run, CancellationToken cancellationToken)
    {
        var folder = Path.GetFullPath(projectFolder);
        var read = await WorkbenchProjectFile.ReadAsync(folder, cancellationToken);
        if (read.Project is not { } project)
        {
            return new WorkbenchReport([], read.Refusal ?? $"{folder} holds no project");
        }

        var images = OrEmpty(project.Images);
        var byName = new Dictionary<string, ProjectImage>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            if (string.IsNullOrWhiteSpace(image.Name))
            {
                return new WorkbenchReport([], "an image of the project has no name");
            }

            if (!byName.TryAdd(image.Name, image))
            {
                return new WorkbenchReport([], $"two images of the project are named '{image.Name}'");
            }
        }

        if (options.Image is { } only && !byName.ContainsKey(only))
        {
            return new WorkbenchReport([], $"the project has no image named '{only}'");
        }

        if (options.ExportPath is not null && options.Image is null)
        {
            return new WorkbenchReport([], "an export needs the image to write named");
        }

        var store = await StepStore.OpenAsync(Path.Combine(folder, StepStore.FolderName), cancellationToken);
        var planner = new Planner(folder, byName, operations, store);
        foreach (var image in images)
        {
            planner.Plan(image.Name);
        }

        if (options.ExportPath is { } wanted)
        {
            // No original is ever written, and the store holds only what it wrote itself.
            var target = Path.GetFullPath(wanted);
            if (images.Any(image => SamePath(planner.Plan(image.Name).Path, target)))
            {
                return new WorkbenchReport([], $"an export never writes over an original: {target} is an image of the project");
            }

            if (target.StartsWith(store.Folder + Path.DirectorySeparatorChar, PathComparison))
            {
                return new WorkbenchReport([], $"an export never writes into the step store: {target}");
            }

            if (SamePath(WorkbenchProjectFile.PathIn(folder), target))
            {
                return new WorkbenchReport([], $"an export never writes over the project file: {target}");
            }
        }

        ImmutableArray<string> asked = options.Image is { } name ? [name] : [.. images.Select(static image => image.Name)];
        // An image is made as far as it can be planned: a refused edit stops what is after it, never what is before.
        foreach (var each in asked)
        {
            if (planner.Plan(each).Furthest is { } furthest)
            {
                MarkNeeded(furthest);
            }
        }

        planner.Settle();

        string? refusal = null;
        string? exported = null;
        if (run)
        {
            foreach (var each in asked)
            {
                if (planner.Plan(each).Furthest is { } furthest)
                {
                    await EnsureAsync(furthest, store, options.Say, cancellationToken);
                }
            }

            if (options is { ExportPath: { } exportPath, Image: { } exportImage })
            {
                refusal = await ExportAsync(planner.Plan(exportImage), Path.GetFullPath(exportPath), store, options.Say, cancellationToken);
                exported = refusal is null ? Path.GetFullPath(exportPath) : null;
            }
        }

        // The report is read off the plan before the project file takes this run's keys, so "changed since the last
        // run" is said against the run before this one.
        var report = new WorkbenchReport([.. images.Select(image => planner.Plan(image.Name).Report())], refusal, exported);
        if (run && await RecordAsync(folder, project, planner, cancellationToken) is { } note)
        {
            report = report with { Note = note };
        }

        return report;
    }

    // A step whose result is in the store needs nothing before it; any other needs every input it has.
    private static void MarkNeeded(Node node)
    {
        if (node.Needed)
        {
            return;
        }

        node.Needed = true;
        if (node.Operation is null || node.File is not null)
        {
            return;
        }

        foreach (var input in node.Inputs)
        {
            MarkNeeded(input);
        }
    }

    // What a step can be handed for this node: its file, a derivation of its inputs, or (a stored step not in the
    // store) the file it has just been run into. Null when it, or something before it, gave no result.
    private static async ValueTask<OperationInput?> EnsureAsync(Node node, StepStore store, Action<string>? say, CancellationToken cancellationToken)
    {
        if (node.File is { } file)
        {
            return OperationInput.FromFile(file);
        }

        if (node.Operation is not { } operation || node.Step is not { } step || step.State is StepState.Refused or StepState.Blocked)
        {
            return null;
        }

        if (operation is not IDerivedOperation && store.Find(node.Key) is { } made)
        {
            // Another image of the project has the same step, and this run has made it already: one result, one run.
            node.File = made;
            step.State = StepState.Kept;
            return OperationInput.FromFile(made);
        }

        var inputs = await EnsureInputsAsync(node, step, store, say, cancellationToken);
        if (inputs is null)
        {
            return null;
        }

        if (operation is IDerivedOperation derived)
        {
            return derived.Derive(node.Settings, inputs);
        }

        using var staging = store.Stage(node.Key);
        var outcome = await operation.RunAsync(node.Settings, inputs, staging.Path, Named(say, node), cancellationToken);
        if (outcome.Succeeded && !File.Exists(staging.Path))
        {
            outcome = OperationOutcome.Failed($"'{operation.Description.Id}' reported a result and wrote no file");
        }

        if (!outcome.Succeeded)
        {
            step.State = StepState.Refused;
            step.Message = outcome.Message;
            return null;
        }

        node.File = await store.CommitAsync(staging, operation.Description.Id, cancellationToken);
        step.State = StepState.Ran;
        return OperationInput.FromFile(node.File);
    }

    private static async ValueTask<List<OperationInput>?> EnsureInputsAsync(Node node, StepPlan step, StepStore store, Action<string>? say, CancellationToken cancellationToken)
    {
        var inputs = new List<OperationInput>(node.Inputs.Length);
        foreach (var input in node.Inputs)
        {
            if (await EnsureAsync(input, store, say, cancellationToken) is not { } value)
            {
                step.State = StepState.Blocked;
                step.Message = $"'{input.Label}', which it takes, gave no result";
                return null;
            }

            inputs.Add(value);
        }

        return inputs;
    }

    // Writes an image's final result as a file; null when written, else why not.
    private static async ValueTask<string?> ExportAsync(ImagePlan plan, string exportPath, StepStore store, Action<string>? say, CancellationToken cancellationToken)
    {
        var name = plan.Image.Name;
        if (plan.Head is not { } head)
        {
            return $"'{name}' cannot be made, so there is nothing to export";
        }

        if (head.Operation is not { } operation || head.Step is not { } step)
        {
            return $"'{name}' has no edit switched on: its result is its own file, {head.File}";
        }

        try
        {
            if (Path.GetDirectoryName(exportPath) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            if (head.File is { } stored)
            {
                // A stored result is already the file its verb writes.
                File.Copy(stored, exportPath, overwrite: true);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{exportPath} cannot be written: {ex.Message}";
        }

        if (operation is not IDerivedOperation)
        {
            return $"'{name}' gave no result, so there is nothing to export";
        }

        // A derived result has no file: the operation's own routine writes one, as its verb would.
        var inputs = await EnsureInputsAsync(head, step, store, say, cancellationToken);
        if (inputs is null)
        {
            return $"'{name}' cannot be made, so there is nothing to export";
        }

        try
        {
            var outcome = await operation.RunAsync(head.Settings, inputs, exportPath, Named(say, head), cancellationToken);
            return outcome.Succeeded ? null : outcome.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{exportPath} cannot be written: {ex.Message}";
        }
    }

    // The project file with this run's keys and results in it, written only when one of them changed. A run can last
    // minutes, so the file is read again under the file's lock and the keys go onto the edits that are still the ones
    // the run planned: an edit made to project.json meanwhile is kept, and an edit changed since gets no key of a
    // result it did not make.
    // Returns a note when the file could not be read under the lock (missing, or saved half way): it is then left as
    // it is, never overwritten with the run's start-of-run copy, and the next run finds the results again by their keys.
    private static async Task<string?> RecordAsync(string folder, WorkbenchProject project, Planner planner, CancellationToken cancellationToken)
    {
        if (!TryRecord(folder, project, planner, out _))
        {
            return null;
        }

        // A null from the callback leaves the file as the person left it: nothing is staged, so nothing is overwritten.
        var written = await SharedFile.UpdateJsonIfAsync(
            WorkbenchProjectFile.PathIn(folder),
            WorkbenchJsonContext.Default.WorkbenchProject,
            onDisk =>
            {
                if (onDisk is null)
                {
                    return null;
                }

                TryRecord(folder, onDisk, planner, out var recorded);
                return recorded;
            },
            logger: null,
            cancellationToken);
        return written
            ? null
            : $"{WorkbenchProjectFile.PathIn(folder)} could not be read at the end of the run (it was removed or saved half way), so this run's results are not recorded in it; the next run finds them again";
    }

    // The keys and outputs of this run's plan put onto what is in <paramref name="current"/>, wherever the edit is still
    // the planned one; true when that changed anything.
    private static bool TryRecord(string folder, WorkbenchProject current, Planner planner, out WorkbenchProject recorded)
    {
        var changed = false;
        var images = ImmutableArray.CreateBuilder<ProjectImage>();
        foreach (var image in OrEmpty(current.Images))
        {
            if (!planner.Knows(image.Name) || planner.Plan(image.Name) is not { } plan || !string.Equals(plan.Image.Path, image.Path, StringComparison.Ordinal))
            {
                images.Add(image);
                continue;
            }

            var edits = ImmutableArray.CreateBuilder<ProjectEdit>(OrEmpty(image.Edits).Length);
            for (var index = 0; index < OrEmpty(image.Edits).Length; index++)
            {
                var edit = image.Edits[index];
                if (index < plan.Steps.Count
                    && plan.Steps[index] is { Node: { } node, State: StepState.Kept or StepState.Ran or StepState.Derived } step
                    && SameEdit(step.Edit, edit))
                {
                    var output = node.File is { } file ? Path.GetRelativePath(folder, file).Replace('\\', '/') : null;
                    if (!string.Equals(edit.Key, node.Key, StringComparison.Ordinal) || !string.Equals(edit.Output, output, StringComparison.Ordinal))
                    {
                        edit = edit with { Key = node.Key, Output = output };
                        changed = true;
                    }
                }

                edits.Add(edit);
            }

            var digest = plan.Key.Length > 0 ? plan.Key : image.Digest;
            changed |= !string.Equals(digest, image.Digest, StringComparison.Ordinal);
            images.Add(image with { Digest = digest, Edits = edits.ToImmutable() });
        }

        recorded = changed ? current with { Images = images.ToImmutable() } : current;
        return changed;
    }

    // Two edits that ask for the same step: the operation, its switch, its settings and the images it takes.
    private static bool SameEdit(ProjectEdit planned, ProjectEdit onDisk)
        => string.Equals(planned.Operation, onDisk.Operation, StringComparison.Ordinal)
            && planned.Enabled == onDisk.Enabled
            && OrEmpty(planned.Inputs).SequenceEqual(OrEmpty(onDisk.Inputs), StringComparer.Ordinal)
            && JsonNode.DeepEquals(planned.Settings ?? new JsonObject(), onDisk.Settings ?? new JsonObject());

    private static Action<string>? Named(Action<string>? say, Node node)
        => say is null ? null : line => say($"[{node.Label}] {line}");

    private static ImmutableArray<T> OrEmpty<T>(ImmutableArray<T> items) => items.IsDefault ? [] : items;

    // Two names of one file, as the file system this runs on compares them.
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool SamePath(string one, string other) => string.Equals(Path.GetFullPath(one), Path.GetFullPath(other), PathComparison);

    /// <summary>Something a step can take: an original file, or a step's result.</summary>
    private sealed class Node
    {
        /// <summary>What a message calls it: the image, and for a step its place and operation.</summary>
        public required string Label { get; init; }

        public required string Key { get; init; }

        /// <summary>Null for an original file.</summary>
        public IOperation? Operation { get; init; }

        /// <summary>The step's settings in canonical form.</summary>
        public string Settings { get; init; } = string.Empty;

        public ImmutableArray<Node> Inputs { get; init; } = [];

        public StepPlan? Step { get; init; }

        /// <summary>An original's file; a stored step's result once it is in the store.</summary>
        public string? File { get; set; }

        public bool Needed { get; set; }
    }

    private sealed class StepPlan
    {
        public required int Index { get; init; }

        public required ProjectEdit Edit { get; init; }

        public Node? Node { get; set; }

        public StepState State { get; set; }

        public string? Message { get; set; }

        public ImmutableArray<string> CommandLine { get; set; } = [];
    }

    private sealed class ImagePlan
    {
        public required ProjectImage Image { get; init; }

        public required string Path { get; init; }

        public string Key { get; set; } = string.Empty;

        public string? Refusal { get; set; }

        public List<StepPlan> Steps { get; } = [];

        /// <summary>The image as its edits leave it; null when it cannot be made.</summary>
        public Node? Head { get; set; }

        /// <summary>The image as far as its edits could be planned: <see cref="Head"/>, or what is before the first
        /// edit that was refused; null when its file is not there.</summary>
        public Node? Furthest { get; set; }

        public ImageReport Report() => new(
            Image.Name,
            Path,
            Key,
            [.. Steps.Select(static step => new StepReport(
                step.Index,
                step.Edit.Operation,
                step.State,
                step.Node?.Key ?? string.Empty,
                step.Node?.File,
                step.CommandLine,
                step.Message,
                step is { Node: { } node, Edit.Key: { } recorded } && !string.Equals(recorded, node.Key, StringComparison.Ordinal)))],
            Refusal,
            Image.Digest is { } digest && Key.Length > 0 && !string.Equals(digest, Key, StringComparison.Ordinal));
    }

    /// <summary>Works out every image's steps and keys, each image once.</summary>
    private sealed class Planner(string folder, Dictionary<string, ProjectImage> byName, OperationCatalog operations, StepStore store)
    {
        private readonly Dictionary<string, ImagePlan> _plans = new(StringComparer.Ordinal);

        // The images being planned, outermost first. An image named again while it is in here takes itself.
        private readonly List<string> _planning = [];

        public bool Knows(string name) => byName.ContainsKey(name);

        public ImagePlan Plan(string name)
        {
            if (_plans.TryGetValue(name, out var planned))
            {
                return planned;
            }

            var image = byName[name];
            var plan = new ImagePlan { Image = image, Path = System.IO.Path.GetFullPath(image.Path, folder) };
            _planning.Add(name);
            try
            {
                PlanSteps(plan);
            }
            finally
            {
                _planning.RemoveAt(_planning.Count - 1);
            }

            _plans[name] = plan;
            return plan;
        }

        /// <summary>A step not in the store that nothing asked for needs is not going to run.</summary>
        public void Settle()
        {
            foreach (var plan in _plans.Values)
            {
                foreach (var step in plan.Steps)
                {
                    if (step is { State: StepState.WillRun, Node.Needed: false })
                    {
                        step.State = StepState.NotNeeded;
                    }
                }
            }
        }

        private void PlanSteps(ImagePlan plan)
        {
            var image = plan.Image;
            if (string.IsNullOrWhiteSpace(image.Path))
            {
                plan.Refusal = "it names no file";
            }
            else if (!File.Exists(plan.Path))
            {
                plan.Refusal = $"its file is not there: {plan.Path}";
            }
            else
            {
                plan.Key = StepKey.OfFile(plan.Path);
                if (plan.Key.Length == 0)
                {
                    plan.Refusal = $"its file cannot be read: {plan.Path}";
                }
            }

            var current = plan.Refusal is null ? new Node { Label = image.Name, Key = plan.Key, File = plan.Path } : null;
            plan.Furthest = current;
            var edits = OrEmpty(image.Edits);
            for (var i = 0; i < edits.Length; i++)
            {
                var step = new StepPlan { Index = i, Edit = edits[i] };
                plan.Steps.Add(step);
                if (!step.Edit.Enabled)
                {
                    step.State = StepState.Off;
                    continue;
                }

                if (current is null)
                {
                    step.State = StepState.Blocked;
                    step.Message = "what is before it cannot be made";
                    continue;
                }

                current = PlanStep(plan, step, current);
                plan.Furthest = current ?? plan.Furthest;
            }

            plan.Head = current;
        }

        // The step's node, with its key and where it stands; null, with the step refused or blocked, when it cannot be planned.
        private Node? PlanStep(ImagePlan plan, StepPlan step, Node previous)
        {
            var edit = step.Edit;
            if (!operations.TryGet(edit.Operation, out var operation))
            {
                return Refuse(step, $"there is no operation named '{edit.Operation}'");
            }

            if (!operation.TryReadSettings(edit.Settings?.ToJsonString(), out var settings, out var refusal))
            {
                return Refuse(step, refusal);
            }

            var id = operation.Description.Id;
            var roles = operation.Description.ExtraInputs;
            var named = OrEmpty(edit.Inputs);
            if (named.Length != roles.Length)
            {
                return Refuse(step, roles.Length == 0
                    ? $"'{id}' takes no other image, and {named.Length} are named"
                    : $"'{id}' takes {roles.Length} other image(s) ({string.Join(", ", roles.Select(static role => role.Name))}), and {named.Length} are named");
            }

            var inputs = ImmutableArray.CreateBuilder<Node>(1 + named.Length);
            inputs.Add(previous);
            foreach (var name in named)
            {
                if (!byName.ContainsKey(name))
                {
                    return Refuse(step, $"it takes the image '{name}', and the project has none of that name");
                }

                if (_planning.Contains(name))
                {
                    return Refuse(step, $"the images take one another in a circle: {string.Join(" -> ", _planning)} -> {name}");
                }

                if (Plan(name).Head is not { } head)
                {
                    step.State = StepState.Blocked;
                    step.Message = $"the image '{name}', which it takes, cannot be made";
                    return null;
                }

                inputs.Add(head);
            }

            var key = StepKey.OfStep(operation.Description, settings, operation.ServedBy(settings), [.. inputs.Select(static input => input.Key)]);
            var node = new Node
            {
                Label = $"{plan.Image.Name} {step.Index + 1} {id}",
                Key = key,
                Operation = operation,
                Settings = settings,
                Inputs = inputs.MoveToImmutable(),
                Step = step,
            };
            step.Node = node;
            // A step's command line names files: an original, a result in the store, or where in the store a result
            // would be. A derived step has no file, so run by hand it writes one there, which the store does not take
            // for a result (its index does not name it).
            step.CommandLine = operation.CommandLine(settings, [.. node.Inputs.Select(input => input.File ?? store.PathOf(input.Key))], store.PathOf(key));
            if (operation is IDerivedOperation)
            {
                step.State = StepState.Derived;
            }
            else if (store.Find(key) is { } stored)
            {
                node.File = stored;
                step.State = StepState.Kept;
            }
            else
            {
                // Settle() makes this NotNeeded where nothing asked for needs it.
                step.State = StepState.WillRun;
            }

            return node;
        }

        private static Node? Refuse(StepPlan step, string why)
        {
            step.State = StepState.Refused;
            step.Message = why;
            return null;
        }
    }
}
