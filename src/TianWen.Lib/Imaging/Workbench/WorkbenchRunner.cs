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
/// <param name="Output">Its result in the store, when it is there: an image, or the finding of a step that writes
/// no pixels.</param>
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

    /// <summary>Frames of the project's sets of frames that were read to know their digest.</summary>
    public int FramesDigested { get; init; }

    /// <summary>Frames whose digest the index had (<see cref="FrameDigestIndex"/>), their size and time unmoved.</summary>
    public int FramesFromIndex { get; init; }

    /// <summary>True when the frame digest index could not be written (the run is unaffected; the frames are read again next time).</summary>
    public bool FramesIndexNotSaved { get; init; }

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
    /// its own, so this is how one is had: the operation's own routine writes it, as its verb would. A step that
    /// writes no pixels (the plate solve) keeps only its finding, a header-only FITS file with no image, so the
    /// image to have is this export.</summary>
    public string? ExportPath { get; init; }

    /// <summary>Takes each line a step reports, with the step named in front.</summary>
    public Action<string>? Say { get; init; }

    /// <summary>Read every frame of every set of frames to know its key, whatever the digest index holds
    /// (<see cref="FrameDigestIndex"/>), and correct the index: what finds a frame changed in place under an unchanged
    /// size and time.</summary>
    public bool VerifyFrames { get; init; }
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
/// earlier result that is gone is not made again for its sake. A step that writes no pixels is the exception: what
/// it keeps is a finding, not an image, so it needs its input whenever its own result is needed.</para>
/// <para><b>A stored step reads a file and writes a file.</b> Only a derived step hands its result over in memory,
/// and only to the step after it.</para>
/// <para><b>A step that writes no pixels keeps its finding.</b> The finding is in the store under a key of its own
/// (<see cref="StepKey.OfFinding"/>), so the step is not run again; what it hands on is its input with the finding
/// applied, so it needs what is before it whenever it is needed itself.</para>
/// <para>Call it off a render thread: it digests the original files and reads and writes images.</para>
/// </remarks>
public sealed class WorkbenchRunner(OperationCatalog operations)
{
    /// <summary>What a run would do, with nothing run and no result written. It writes one file, the digest index of a set
    /// of frames (store/frames.json, creating store/), so that what it read is not read again.</summary>
    /// <param name="projectFolder">The project's folder.</param>
    /// <param name="image">Only what this image needs counts as needed; null for every image.</param>
    /// <param name="cancellationToken">Ends the work.</param>
    public Task<WorkbenchReport> StatusAsync(string projectFolder, string? image = null, CancellationToken cancellationToken = default)
        => GoAsync(projectFolder, new WorkbenchRunOptions { Image = image }, run: false, cancellationToken);

    /// <summary>As <see cref="StatusAsync(string, string?, CancellationToken)"/>, with the options a status reads: the
    /// image, and whether every frame is read again (<see cref="WorkbenchRunOptions.VerifyFrames"/>). Nothing is
    /// exported and no step runs.</summary>
    public Task<WorkbenchReport> StatusAsync(string projectFolder, WorkbenchRunOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return GoAsync(projectFolder, options with { ExportPath = null }, run: false, cancellationToken);
    }

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
        var frames = await FrameDigestIndex.OpenAsync(store.Folder, cancellationToken);
        frames.KeepOnly(images.Where(static image => !string.IsNullOrWhiteSpace(image.Path)).Select(image => Path.GetFullPath(image.Path, folder)));
        var planner = new Planner(folder, byName, operations, store, frames, options.VerifyFrames, cancellationToken);
        try
        {
            foreach (var image in images)
            {
                planner.Plan(image.Name);
            }
        }
        finally
        {
            // What a set of frames was digested as is kept, by a status too: reading a night again to say the same thing
            // is the cost the index is there to save. A cancelled walk keeps what it learned (an unfinished walk
            // prunes nothing), so the next run does not start the night from zero.
            await frames.SaveAsync(CancellationToken.None);
        }

        if (options.ExportPath is { } wanted)
        {
            // No original is ever written, and the store holds only what it wrote itself.
            var target = Path.GetFullPath(wanted);
            if (images.Any(image => planner.Plan(image.Name).Path is { Length: > 0 } original && SamePath(original, target)))
            {
                return new WorkbenchReport([], $"an export never writes over an original: {target} is an image of the project");
            }

            if (images.Any(image => planner.Plan(image.Name).Path is { Length: > 0 } original && Directory.Exists(original) && HoldsOrIs(original, target)))
            {
                return new WorkbenchReport([], $"an export never writes among an original's frames: {target}");
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
                var asking = planner.Plan(each);
                if (asking.Furthest is { } furthest
                    && await EnsureAsync(furthest, store, options.Say, cancellationToken) is null
                    && furthest is { Producer.Step.State: StepState.Ran or StepState.Kept, ResultName: { Length: > 0 } leftOut }
                    && asking.Refusal is null)
                {
                    // The image ends at a result its step may leave out (its own, or the one of the image it starts
                    // from, with no edit switched on after it), and this run the step left it out: the image has
                    // nothing, which is said and not passed over. An edit after it says so itself, as blocked.
                    asking.Refusal = LeftOut(leftOut);
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
        var report = new WorkbenchReport([.. images.Select(image => planner.Plan(image.Name).Report())], refusal, exported)
        {
            FramesDigested = frames.Digested,
            FramesFromIndex = frames.FromIndex,
            FramesIndexNotSaved = frames.NotSaved,
        };
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
        if (node.File is not null)
        {
            return;
        }

        if (node.Producer is { } producer)
        {
            // An extra result is made by the step that gives it.
            MarkNeeded(producer);
            return;
        }

        if (node.Operation is null)
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
        var handed = await MakeAsync(node, store, say, cancellationToken);
        if (handed is null && node is { Attempt: { } attempt, Step: { State: StepState.Refused, Message: { } why } })
        {
            // What it was refused for is kept for the length of the call, under the step's key or the key of the run
            // it shares: another edit list that asks for the same one is told the same, and nothing runs again.
            attempt.Refusal ??= why;
        }

        return handed;
    }

    private static async ValueTask<OperationInput?> MakeAsync(Node node, StepStore store, Action<string>? say, CancellationToken cancellationToken)
    {
        if (node.File is { } file)
        {
            return OperationInput.FromFile(file);
        }

        if (node.Producer is { } producer)
        {
            // An extra result is in the store once the step that gives it has run.
            if (await EnsureAsync(producer, store, say, cancellationToken) is null)
            {
                return null;
            }

            node.File = store.Find(node.Key);
            return node.File is { } extra ? OperationInput.FromFile(extra) : null;
        }

        if (node.Operation is not { } operation || node.Step is not { } step || step.State is StepState.Refused or StepState.Blocked)
        {
            return null;
        }

        if (operation is not (IDerivedOperation or IFindingOperation) && InStore(store, node) is { } made)
        {
            // Another image of the project has the same step, and this run has made it already: one result, one run.
            node.File = made;
            step.State = StepState.Kept;
            return OperationInput.FromFile(made);
        }

        if (node.Attempt is { Refusal: { } refusedEarlier })
        {
            // Another edit list of this call asked for this step, or for the run it shares, and it gave nothing. A
            // remover that fails after minutes of work is not run again for each master of its run, to fail again.
            step.State = StepState.Refused;
            step.Message = refusedEarlier;
            return null;
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

        if (operation is IFindingOperation finder)
        {
            return await FindAsync(node, step, finder, inputs, store, say, cancellationToken);
        }

        if (operation is IJointOperation joint)
        {
            return await RunJointAsync(node, step, joint, inputs, store, say, cancellationToken);
        }

        if (operation is IMultiResultOperation several)
        {
            return await RunSeveralAsync(node, step, several, inputs, store, say, cancellationToken);
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

    // A step that writes no pixels: its finding is taken from the store or made into it, and what is handed on is the
    // step's input with the finding applied.
    private static async ValueTask<OperationInput?> FindAsync(Node node, StepPlan step, IFindingOperation finder, List<OperationInput> inputs, StepStore store,
        Action<string>? say, CancellationToken cancellationToken)
    {
        var finding = node.Finding;
        if (finding is null)
        {
            if (store.Find(node.FindingKey) is { } found)
            {
                // Another image of the project has the same step, and this run has made its finding already.
                finding = found;
                step.State = StepState.Kept;
            }
            else
            {
                using var staging = store.Stage(node.FindingKey);
                var outcome = await finder.FindAsync(node.Settings, inputs, staging.Path, Named(say, node), cancellationToken);
                if (outcome.Succeeded && !File.Exists(staging.Path))
                {
                    outcome = OperationOutcome.Failed($"'{finder.Description.Id}' reported a finding and wrote no file");
                }

                if (!outcome.Succeeded)
                {
                    step.State = StepState.Refused;
                    step.Message = outcome.Message;
                    return null;
                }

                finding = await store.CommitAsync(staging, finder.Description.Id, cancellationToken);
                step.State = StepState.Ran;
            }

            node.Finding = finding;
        }

        return finder.Apply(node.Settings, inputs, finding);
    }

    // One run for every image of it: each image's results are staged, the operation writes them all, and all are taken
    // into the store, each under its own key. The other images' edit lists then find theirs there.
    private static async ValueTask<OperationInput?> RunJointAsync(Node node, StepPlan step, IJointOperation joint, List<OperationInput> members, StepStore store,
        Action<string>? say, CancellationToken cancellationToken)
    {
        var stagings = new List<StepStaging>();
        try
        {
            var outputs = new List<StepOutputs>(node.Inputs.Length);
            foreach (var member in node.Inputs)
            {
                var memberKey = StepKey.OfMember(node.RunKey, member.Key);
                var main = store.Stage(memberKey);
                stagings.Add(main);
                var extras = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
                foreach (var result in joint.Description.ExtraResults)
                {
                    var staged = store.Stage(StepKey.OfResult(memberKey, result.Name));
                    stagings.Add(staged);
                    extras[result.Name] = staged.Path;
                }

                outputs.Add(new StepOutputs(main.Path) { Extra = extras.ToImmutable() });
            }

            var outcome = await joint.RunJointAsync(node.Settings, members, outputs, Named(say, node), cancellationToken);
            if (outcome.Succeeded && stagings.Find(static staged => !File.Exists(staged.Path)) is { } missing)
            {
                outcome = OperationOutcome.Failed($"'{joint.Description.Id}' reported its results and did not write {Path.GetFileName(missing.Path)}");
            }

            if (!outcome.Succeeded)
            {
                step.State = StepState.Refused;
                step.Message = outcome.Message;
                return null;
            }

            foreach (var staged in stagings)
            {
                await store.CommitAsync(staged, joint.Description.Id, cancellationToken);
            }
        }
        finally
        {
            foreach (var staged in stagings)
            {
                staged.Dispose();
            }
        }

        node.File = store.Find(node.Key);
        step.State = StepState.Ran;
        return node.File is { } stored ? OperationInput.FromFile(stored) : null;
    }

    // One run with fixed inputs that gives several results: the main one and each extra one are staged, the operation
    // writes them all, and all are taken into the store, each under its own key.
    private static async ValueTask<OperationInput?> RunSeveralAsync(Node node, StepPlan step, IMultiResultOperation several, List<OperationInput> inputs, StepStore store,
        Action<string>? say, CancellationToken cancellationToken)
    {
        var stagings = new List<StepStaging>();
        try
        {
            var main = store.Stage(node.Key);
            stagings.Add(main);
            var extras = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            // The places of the results the step may leave out, by name: one of these not written is no failure.
            var mayBeLeftOut = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var result in several.Description.ExtraResults)
            {
                var staged = store.Stage(StepKey.OfResult(node.Key, result.Name));
                stagings.Add(staged);
                extras[result.Name] = staged.Path;
                if (result.Optional)
                {
                    mayBeLeftOut[staged.Path] = result.Name;
                }
            }

            var outcome = await several.RunAsync(node.Settings, inputs, new StepOutputs(main.Path) { Extra = extras.ToImmutable() }, Named(say, node), cancellationToken);
            if (outcome.Succeeded && stagings.Find(staged => !mayBeLeftOut.ContainsKey(staged.Path) && !File.Exists(staged.Path)) is { } missing)
            {
                outcome = OperationOutcome.Failed($"'{several.Description.Id}' reported its results and did not write {Path.GetFileName(missing.Path)}");
            }

            if (!outcome.Succeeded)
            {
                step.State = StepState.Refused;
                step.Message = outcome.Message;
                return null;
            }

            // The extra results first and the main one last. The order does not decide what is kept (InStore needs the main
            // result and every extra, so a run that ends between two commits is made again either way). The main one's
            // entry names the results the step left out, so a result the store loses later is not taken for one of them.
            ImmutableArray<string> leftOut = [.. mayBeLeftOut.Where(static place => !File.Exists(place.Key)).Select(static place => place.Value).Order(StringComparer.Ordinal)];
            for (var i = stagings.Count - 1; i >= 1; i--)
            {
                // A result the step left out has no file to take in.
                if (File.Exists(stagings[i].Path))
                {
                    await store.CommitAsync(stagings[i], several.Description.Id, cancellationToken);
                }
            }

            await store.CommitAsync(main, several.Description.Id, leftOut, cancellationToken);
        }
        finally
        {
            foreach (var staged in stagings)
            {
                staged.Dispose();
            }
        }

        node.File = store.Find(node.Key);
        step.State = StepState.Ran;
        return node.File is { } stored ? OperationInput.FromFile(stored) : null;
    }

    // What an image is told when the result it starts from is one its step may leave out, and did.
    private static string LeftOut(string result) => $"the step it starts from gave no '{result}' for these images";

    // A step's main result in the store, when its extra results are there too: a step is kept whole or made again. A
    // result it may leave out is there, or named by the main result's entry as left out; one it gave and the store has
    // lost since makes the step run again.
    private static string? InStore(StepStore store, Node node)
        => store.Find(node.Key) is { } file
            && node.ExtraKeys.All(key => store.Find(key) is not null)
            && node.OptionalResults.All(name => store.Find(StepKey.OfResult(node.Key, name)) is not null || store.LeftOut(node.Key, name))
            ? file
            : null;

    private static async ValueTask<List<OperationInput>?> EnsureInputsAsync(Node node, StepPlan step, StepStore store, Action<string>? say, CancellationToken cancellationToken)
    {
        var inputs = new List<OperationInput>(node.Inputs.Length);
        foreach (var input in node.Inputs)
        {
            if (await EnsureAsync(input, store, say, cancellationToken) is not { } value)
            {
                step.State = StepState.Blocked;
                // A result its step may leave out, and did, is said so in words rather than as no result.
                step.Message = input is { ResultName: { Length: > 0 } leftOut, Producer.Step.State: StepState.Ran or StepState.Kept }
                    ? $"'{input.Label}', which it takes: {LeftOut(leftOut)}"
                    : $"'{input.Label}', which it takes, gave no result";
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

        if (head.Operation is null && head.Producer is null)
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

        if (head.Step is not { } step || head.Operation is not { } operation || operation is not (IDerivedOperation or IFindingOperation))
        {
            return $"'{name}' gave no result, so there is nothing to export";
        }

        // A derived result has no file, and neither has the result of a step that writes no pixels: the operation's
        // own routine writes one, as its verb would.
        var inputs = await EnsureInputsAsync(head, step, store, say, cancellationToken);
        if (inputs is null)
        {
            return $"'{name}' cannot be made, so there is nothing to export";
        }

        try
        {
            if (operation is IFindingOperation finder)
            {
                // From the finding the run made or kept: nothing is found out a second time.
                if (head.Finding is not { } finding)
                {
                    return $"'{name}' gave no result, so there is nothing to export";
                }

                var written = finder.Write(head.Settings, inputs, finding, exportPath);
                return written.Succeeded ? null : written.Message;
            }

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
            if (!planner.Knows(image.Name) || planner.Plan(image.Name) is not { } plan
                || !string.Equals(plan.Image.Path, image.Path, StringComparison.Ordinal)
                || !string.Equals(plan.Image.From, image.From, StringComparison.Ordinal)
                || !string.Equals(plan.Image.Result, image.Result, StringComparison.Ordinal))
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
                    var output = (node.File ?? node.Finding) is { } file ? Path.GetRelativePath(folder, file).Replace('\\', '/') : null;
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

    // Whether <paramref name="inner"/> is <paramref name="folder"/> or lies anywhere under it.
    internal static bool HoldsOrIs(string folder, string inner)
    {
        var (outer, within) = (Path.GetFullPath(folder), Path.GetFullPath(inner));
        return string.Equals(Path.TrimEndingDirectorySeparator(outer), Path.TrimEndingDirectorySeparator(within), PathComparison)
            || FrameDigestIndex.IsUnder(within, outer);
    }

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

        /// <summary>For a node that is an extra result of a step: the result's name. Null otherwise.</summary>
        public string? ResultName { get; init; }

        /// <summary>For a step several images go through in one run: the run's key. Its <see cref="Inputs"/> are then
        /// the images of the run, sorted by key. Empty otherwise.</summary>
        public string RunKey { get; init; } = string.Empty;

        /// <summary>What this step came to in this call, shared by every node of the same key (the run's key, for a
        /// step several images go through in one run), so a refusal is run into once and said to each. Null for an
        /// original and for a derived step, which run nothing.</summary>
        public Attempt? Attempt { get; init; }

        /// <summary>The keys of the step's extra results.</summary>
        public ImmutableArray<string> ExtraKeys { get; init; } = [];

        /// <summary>The names of the results the step may leave out: each is in the store, or the step's entry says it
        /// was left out.</summary>
        public ImmutableArray<string> OptionalResults { get; init; } = [];

        /// <summary>For an extra result: the step that gives it. Null otherwise.</summary>
        public Node? Producer { get; init; }

        /// <summary>For a step that writes no pixels: the key its finding is kept under. Empty otherwise.</summary>
        public string FindingKey { get; init; } = string.Empty;

        /// <summary>The finding's file once it is in the store. <see cref="File"/> stays null for such a step: it has no
        /// image there.</summary>
        public string? Finding { get; set; }
    }

    // What a step, or the run several images share, came to in one call.
    private sealed class Attempt
    {
        /// <summary>Why it gave nothing, once it has run and been refused.</summary>
        public string? Refusal { get; set; }
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
            Path.Length > 0 ? Path : Image.Result is { Length: > 0 } result ? $"from {Image.From} ({result})" : $"from {Image.From}",
            Key,
            [.. Steps.Select(static step => new StepReport(
                step.Index,
                step.Edit.Operation,
                step.State,
                step.Node?.Key ?? string.Empty,
                step.Node?.File ?? step.Node?.Finding,
                step.CommandLine,
                step.Message,
                step is { Node: { } node, Edit.Key: { } recorded } && !string.Equals(recorded, node.Key, StringComparison.Ordinal)))],
            Refusal,
            Image.Digest is { } digest && Key.Length > 0 && !string.Equals(digest, Key, StringComparison.Ordinal));
    }

    /// <summary>Works out every image's steps and keys, each image once.</summary>
    private sealed class Planner(string folder, Dictionary<string, ProjectImage> byName, OperationCatalog operations, StepStore store,
        FrameDigestIndex frames, bool verifyFrames, CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, ImagePlan> _plans = new(StringComparer.Ordinal);

        // What each step of this call came to, by its key, or by its run's key where several images share the run.
        private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);

        // The images being planned, outermost first. An image named again while it is in here takes itself.
        private readonly List<string> _planning = [];

        // What serves an operation under given settings, asked once a plan. For a step a model serves the answer can
        // cost a licence probe (an RC-Astro role names its backend by asking whether its product is licensed), and a
        // plan would otherwise ask it for every step of every image, on a status as on a run.
        private readonly Dictionary<(IOperation Operation, string Settings), string> _served = [];

        public bool Knows(string name) => byName.ContainsKey(name);

        public ImagePlan Plan(string name)
        {
            if (_plans.TryGetValue(name, out var planned))
            {
                return planned;
            }

            var image = byName[name];
            var plan = new ImagePlan { Image = image, Path = string.IsNullOrWhiteSpace(image.Path) ? string.Empty : System.IO.Path.GetFullPath(image.Path, folder) };
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
            Node? current;
            if (image.From is { Length: > 0 } origin)
            {
                current = StartFrom(plan, origin);
            }
            else
            {
                if (image.Result is { Length: > 0 })
                {
                    plan.Refusal = "it names a result of another image and not the image: say which it starts from";
                }
                else if (string.IsNullOrWhiteSpace(image.Path))
                {
                    plan.Refusal = "it names no file";
                }
                else if (Directory.Exists(plan.Path))
                {
                    // A set of frames (a night's lights and its calibration frames): its key is every frame's place
                    // and digest, a digest taken from the index where the frame's size and time have not moved.
                    if (HoldsOrIs(plan.Path, store.Folder))
                    {
                        plan.Refusal = "its folder holds this project's store, so a result would be one of its frames: keep the project's folder outside the frames";
                    }
                    else
                    {
                        plan.Key = frames.KeyOf(plan.Path, verifyFrames, out var unreadable, cancellationToken);
                        plan.Refusal = unreadable;
                    }
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

                current = plan.Refusal is null ? new Node { Label = image.Name, Key = plan.Key, File = plan.Path } : null;
            }

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

        // Where an image that starts from another starts: that image as its edits leave it, or one of its extra results.
        // Null, with the reason on the plan, when there is no such start.
        private Node? StartFrom(ImagePlan plan, string origin)
        {
            var image = plan.Image;
            if (!string.IsNullOrWhiteSpace(image.Path))
            {
                plan.Refusal = "it names a file and an image to start from: one or the other";
                return null;
            }

            if (!byName.ContainsKey(origin))
            {
                plan.Refusal = $"it starts from the image '{origin}', and the project has none of that name";
                return null;
            }

            if (_planning.Contains(origin))
            {
                plan.Refusal = $"the images take one another in a circle: {string.Join(" -> ", _planning)} -> {origin}";
                return null;
            }

            var source = Plan(origin);
            if (image.Result is not { Length: > 0 } wanted)
            {
                if (source.Head is not { } head)
                {
                    // The reason that image has, when it has one, is said here too: one hop past a result its step
                    // left out, a status and a second run say that, as the first run does.
                    plan.Refusal = source.Refusal is { Length: > 0 } why
                        ? $"the image '{origin}', which it starts from, cannot be made: {why}"
                        : $"the image '{origin}', which it starts from, cannot be made";
                    return null;
                }

                plan.Key = head.Key;
                return head;
            }

            // The last of that image's edits that gives a result of this name. An edit that is switched on and could
            // not be planned stops the search where it is the one that would give it, or where no operation has its
            // name and so nothing says what it gives: an earlier edit's result is never taken for the last one's.
            for (var i = source.Steps.Count - 1; i >= 0; i--)
            {
                var each = source.Steps[i];
                if (!each.Edit.Enabled || (operations.TryGet(each.Edit.Operation, out var named) && !Gives(named, wanted)))
                {
                    continue;
                }

                if (each.Node is not { } producer)
                {
                    plan.Refusal = $"edit {each.Index + 1} of the image '{origin}' ({each.Edit.Operation}), the last that would give '{wanted}', cannot be made";
                    return null;
                }

                var key = StepKey.OfResult(producer.Key, wanted);
                plan.Key = key;
                var whole = InStore(store, producer) is not null;
                var file = whole ? store.Find(key) : null;
                if (whole && file is null)
                {
                    // The step is whole in the store without this result, so it is one the step may leave out, and
                    // for these images it did: a status says so as a run does.
                    plan.Refusal = LeftOut(wanted);
                    return null;
                }

                return new Node
                {
                    Label = $"{origin} {wanted}",
                    Key = key,
                    Producer = producer,
                    File = file,
                    ResultName = wanted,
                };
            }

            plan.Refusal = $"no edit of the image '{origin}' gives a result named '{wanted}'";
            return null;
        }

        // True for an operation one of whose results has this name.
        private static bool Gives(IOperation operation, string result)
            => operation.Description.ExtraResults.Any(role => string.Equals(role.Name, result, StringComparison.Ordinal));

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
            var joint = operation as IJointOperation;
            var needed = roles.Count(static role => !role.Optional);
            var anyNumber = operation.Description.AnyNumber;
            if (joint is null && !anyNumber && (named.Length < needed || named.Length > roles.Length))
            {
                var count = needed == roles.Length ? $"{needed}" : $"{needed} to {roles.Length}";
                return Refuse(step, roles.Length == 0
                    ? $"'{id}' takes no other image, and {named.Length} are named"
                    : $"'{id}' takes {count} other image(s) ({string.Join(", ", roles.Select(static role => role.Name))}), and {named.Length} are named");
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

            if (!operation.Description.TakesFrameSet)
            {
                foreach (var input in inputs)
                {
                    if (input.File is { Length: > 0 } file && Directory.Exists(file))
                    {
                        return Refuse(step, $"{file} is a folder of frames, and '{id}' takes an image");
                    }
                }
            }

            Node node;
            var label = $"{plan.Image.Name} {step.Index + 1} {id}";
            if (joint is not null)
            {
                // The images of one run in an order every one of their edit lists agrees on, by key, so each list asks
                // for the same run and it happens once.
                var members = inputs.ToImmutable().Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                for (var i = 1; i < members.Length; i++)
                {
                    if (string.Equals(members[i - 1].Key, members[i].Key, StringComparison.Ordinal))
                    {
                        return Refuse(step, $"'{id}' is handed one image twice: '{members[i].Label}' is '{members[i - 1].Label}'");
                    }
                }

                var runKey = StepKey.OfStep(operation.Description, settings, Served(operation, settings), [.. members.Select(static member => member.Key)]);
                var memberKey = StepKey.OfMember(runKey, previous.Key);
                node = new Node
                {
                    Label = label,
                    Key = memberKey,
                    Operation = operation,
                    Settings = settings,
                    Inputs = members,
                    Step = step,
                    RunKey = runKey,
                    Attempt = AttemptOf(runKey),
                    ExtraKeys = [.. operation.Description.ExtraResults.Select(result => StepKey.OfResult(memberKey, result.Name))],
                };
                step.CommandLine = joint.CommandLine(
                    settings,
                    [.. members.Select(member => member.File ?? store.PathOf(member.Key))],
                    [.. members.Select(member => OutputsOf(operation.Description, StepKey.OfMember(runKey, member.Key)))]);
            }
            else
            {
                var taken = inputs.MoveToImmutable();
                if (anyNumber)
                {
                    // A set: in an order every edit list that holds this step agrees on, by key, so the lists share one
                    // result and it does not depend on the order an edit names the others in.
                    taken = taken.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                    for (var i = 1; i < taken.Length; i++)
                    {
                        if (string.Equals(taken[i - 1].Key, taken[i].Key, StringComparison.Ordinal))
                        {
                            return Refuse(step, $"'{id}' is handed one image twice: '{taken[i].Label}' is '{taken[i - 1].Label}'");
                        }
                    }
                }

                var key = StepKey.OfStep(operation.Description, settings, Served(operation, settings), [.. taken.Select(static input => input.Key)]);
                node = new Node
                {
                    Label = label,
                    Key = key,
                    Operation = operation,
                    Settings = settings,
                    Inputs = taken,
                    Step = step,
                    Attempt = operation is IDerivedOperation ? null : AttemptOf(key),
                    FindingKey = operation is IFindingOperation ? StepKey.OfFinding(key) : string.Empty,
                    // The results a step is not whole without: one it may leave out is not among them.
                    ExtraKeys = [.. operation.Description.ExtraResults.Where(static result => !result.Optional).Select(result => StepKey.OfResult(key, result.Name))],
                    OptionalResults = [.. operation.Description.ExtraResults.Where(static result => result.Optional).Select(static result => result.Name)],
                };
                // A step's command line names files: an original, a result in the store, or where in the store a result
                // would be. A derived step has no file, and neither has a step that writes no pixels, so run by hand it
                // writes one there, which the store does not take for a result (its index does not name it).
                ImmutableArray<string> files = [.. node.Inputs.Select(input => input.File ?? store.PathOf(input.Key))];
                step.CommandLine = operation is IMultiResultOperation withResults
                    ? withResults.CommandLine(settings, files, OutputsOf(operation.Description, key))
                    : operation.CommandLine(settings, files, store.PathOf(key));
            }

            step.Node = node;
            if (operation is IDerivedOperation)
            {
                step.State = StepState.Derived;
            }
            else if (operation is IFindingOperation)
            {
                // Settle() makes a finding that is not there NotNeeded where nothing asked for needs it.
                node.Finding = store.Find(node.FindingKey);
                step.State = node.Finding is null ? StepState.WillRun : StepState.Kept;
            }
            else if (InStore(store, node) is { } stored)
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

        // The one record of what a step, or the run several images share, came to in this call, by its key.
        private Attempt AttemptOf(string key)
        {
            if (!_attempts.TryGetValue(key, out var attempt))
            {
                attempt = new Attempt();
                _attempts[key] = attempt;
            }

            return attempt;
        }

        // Where in the store the results under this key would be: what a step's command line names for them.
        private StepOutputs OutputsOf(OperationDescription description, string key)
            => new(store.PathOf(key))
            {
                Extra = description.ExtraResults.ToImmutableDictionary(
                    static result => result.Name, result => store.PathOf(StepKey.OfResult(key, result.Name)), StringComparer.Ordinal),
            };

        private string Served(IOperation operation, string settings)
        {
            if (!_served.TryGetValue((operation, settings), out var served))
            {
                served = operation.ServedBy(settings);
                _served[(operation, settings)] = served;
            }

            return served;
        }

        private static Node? Refuse(StepPlan step, string why)
        {
            step.State = StepState.Refused;
            step.Message = why;
            return null;
        }
    }
}
