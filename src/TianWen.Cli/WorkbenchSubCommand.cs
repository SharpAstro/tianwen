using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen workbench new|process|status|run|operations|chain</c>: a project made from a capture, and its edit lists run, with no window
/// (docs/plans/stacking-workbench.md, W1). A project is a folder holding <c>project.json</c> (the original files and
/// the edits made to each) beside the step store.
/// </summary>
/// <remarks>
/// This verb is a host and nothing more: it reads the options, calls <see cref="WorkbenchRunner"/> and prints what it
/// answers. Which steps run is the runner's to decide, from each step's key.
/// </remarks>
internal sealed class WorkbenchSubCommand(IConsoleHost consoleHost, OperationCatalog operations, WorkbenchRunner runner)
{
    public Command Build() => new("workbench",
        "Run a project's edit lists without a window: a folder holding project.json (the original files and the edits made to each) "
        + "beside the step store. A step whose result is in the store is not run again.")
    {
        Subcommands = { BuildStatusCommand(), BuildRunCommand(), BuildOperationsCommand(), BuildChainCommand(), BuildNewCommand(), BuildDetectedCommand(), BuildReassignCommand(), BuildWorkflowsCommand(), BuildApplyCommand(), BuildProcessCommand() },
    };

    private Command BuildDetectedCommand()
    {
        var framesArg = new Argument<string>("folder") { Description = "The capture folder." };
        var noGradeOpt = new Option<bool>("--no-grade") { Description = "Do not grade the lights (grading finds the stars of each light, which takes a second or so a frame)." };
        var cmd = new Command("detected",
            "List what a capture folder holds: the groups (lights, darks, flats, bias, a comet track), and for each file the kind it is taken for, why "
            + "(its IMAGETYP, its folder's name, or its pixels; a guess from the pixels is marked with a ?), its exposure, filter, sensor temperature, gain, camera and "
            + "grade. A Needs a look group gathers the guesses and the lights no dark matches. What the project's reassignments (workbench reassign) set is shown as set.")
        {
            Arguments = { framesArg },
            Options = { noGradeOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var frames = parseResult.Required(framesArg);
            if (!Directory.Exists(frames))
            {
                consoleHost.WriteError($"[workbench] the capture folder {frames} does not exist");
                return 1;
            }

            var detected = await CaptureDetection.DetectAsync(frames, new CaptureDetectionOptions
            {
                Reassigned = await WorkbenchProjects.ReassignmentsAsync(frames, ct),
                GradeLights = !parseResult.GetValue(noGradeOpt),
            }, ct);
            foreach (var line in CaptureDetection.Describe(detected))
            {
                consoleHost.WriteScrollable(line.StartsWith("  ", StringComparison.Ordinal) || line.StartsWith("note:", StringComparison.Ordinal) ? line : $"[workbench] {line}");
            }

            return 0;
        });
        return cmd;
    }

    private static async Task<WorkflowFacts> FactsOfAsync(string frames, CancellationToken ct)
        => WorkflowFacts.From(await CaptureDetection.DetectAsync(frames, new CaptureDetectionOptions
        {
            Reassigned = await WorkbenchProjects.ReassignmentsAsync(frames, ct),
            GradeLights = false,
        }, ct));

    private Command BuildWorkflowsCommand()
    {
        var framesArg = new Argument<string>("folder") { Description = "The capture folder." };
        var cmd = new Command("workflows",
            "List the built-in workflows for a capture folder, each with whether it fits these frames and one line saying why or why not "
            + "(colour camera, one filter, a comet track), the best fit marked. workbench apply makes one the project's steps.")
        {
            Arguments = { framesArg },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var frames = parseResult.Required(framesArg);
            if (!Directory.Exists(frames))
            {
                consoleHost.WriteError($"[workbench] the capture folder {frames} does not exist");
                return 1;
            }

            foreach (var answer in BuiltInWorkflows.Answer(await FactsOfAsync(frames, ct)))
            {
                consoleHost.WriteScrollable($"[workbench] {answer.Template.Id}: {(answer.Fit.Fits ? "fits" : "does not fit")}{(answer.Best ? " (best)" : string.Empty)}, {answer.Fit.Why}");
                consoleHost.WriteScrollable($"  {answer.Template.Title}: {answer.Template.Summary}");
            }

            return 0;
        });
        return cmd;
    }

    private Command BuildApplyCommand()
    {
        var framesArg = new Argument<string>("folder") { Description = "The capture folder." };
        var workflowArg = new Argument<string>("workflow") { Description = $"A workflow's id ({string.Join(", ", BuiltInWorkflows.All.Select(static each => each.Id))}), or best for the best fit." };
        var replaceOpt = new Option<bool>("--replace") { Description = "Replace the steps although they were changed by hand since the last workflow was applied." };
        var cmd = new Command("apply",
            "Write a workflow's steps into the capture's project (made in its workdir where there is none). Results already made are kept and reused by their keys; "
            + "the frames' reassignments are kept. Steps changed by hand since the last workflow are replaced only with --replace.")
        {
            Arguments = { framesArg, workflowArg },
            Options = { replaceOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var frames = parseResult.Required(framesArg);
            if (!Directory.Exists(frames))
            {
                consoleHost.WriteError($"[workbench] the capture folder {frames} does not exist");
                return 1;
            }

            var facts = await FactsOfAsync(frames, ct);
            var word = parseResult.Required(workflowArg);
            if (string.Equals(word, "best", StringComparison.OrdinalIgnoreCase))
            {
                word = BuiltInWorkflows.Answer(facts).First(static each => each.Best).Template.Id;
            }

            var changes = await WorkbenchWorkflows.HandChangesAsync(frames, ct);
            if (changes > 0 && !parseResult.GetValue(replaceOpt))
            {
                consoleHost.WriteError($"[workbench] your {changes} change(s) to the steps are replaced; results already made are kept. Say --replace to go on");
                return 1;
            }

            var done = await WorkbenchWorkflows.ApplyAsync(frames, word, facts, ct);
            if (done.Project is not { } project)
            {
                consoleHost.WriteError($"[workbench] {done.Refusal}");
                return 1;
            }

            consoleHost.WriteScrollable($"[workbench] applied {project.Workflow} to {WorkbenchProjects.WorkdirOf(frames)}: {project.Images.Sum(static image => image.Edits.Length)} step(s) in {project.Images.Length} image(s)"
                + (done.Replaced > 0 ? $"; {done.Replaced} change(s) of yours replaced" : string.Empty));
            foreach (var row in WorkbenchWorkflows.Tree(project, operations))
            {
                consoleHost.WriteScrollable($"  {new string(' ', row.Depth * 2)}{row.Label}{(row.Detail.Length > 0 ? " (" + row.Detail + ")" : string.Empty)}");
            }

            return 0;
        });
        return cmd;
    }

    private Command BuildReassignCommand()
    {
        var framesArg = new Argument<string>("folder") { Description = "The capture folder." };
        var fileArg = new Argument<string>("file") { Description = "The file: its place inside the capture folder, or its path." };
        var kindArg = new Argument<string>("kind") { Description = $"What it is: {FrameReassignment.KindWords} (ignore leaves it out of the stack), or auto to take the reassignment back." };
        var cmd = new Command("reassign",
            "Take a file for another kind of frame than its cards and its folder say. The project (in the capture's workdir) records it in the stack step's settings, "
            + "so the step's key changes and the stack runs again; the stack takes it as tianwen stack --reassign does.")
        {
            Arguments = { framesArg, fileArg, kindArg },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var word = parseResult.Required(kindArg);
            FrameType? kind = null;
            if (!string.Equals(word, "auto", StringComparison.OrdinalIgnoreCase))
            {
                if (!FrameReassignment.TryParseKind(word, out var parsed))
                {
                    consoleHost.WriteError($"[workbench] '{word}' is not a kind: say one of {FrameReassignment.KindWords}, or auto");
                    return 1;
                }

                kind = parsed;
            }

            var done = await WorkbenchProjects.ReassignAsync(parseResult.Required(framesArg), parseResult.Required(fileArg), kind, ct);
            if (done.Refusal is { } refusal)
            {
                consoleHost.WriteError($"[workbench] {refusal}");
                return 1;
            }

            consoleHost.WriteScrollable($"[workbench] {done.Reassigned.Length} file(s) reassigned{(done.Reassigned.IsEmpty ? string.Empty : ": " + string.Join(", ", done.Reassigned.Select(static each => each.Format())))}");
            return 0;
        });
        return cmd;
    }

    private Command BuildChainCommand()
    {
        var projectArg = ProjectArgument();
        var imageArg = new Argument<string>("image") { Description = "The image to lay out: its one edit is an enhance." };
        var nameOpt = new Option<string?>("--name") { Description = "What the chain's result is called. Default: <image>-chain." };
        var cmd = new Command("chain",
            "The enhance laid out as a chain of the single steps (white-balance, deblur, gradient, remove-stars, denoise; SCNR on the stars; "
            + "add-stars), added to the project beside the image, each step then kept in the store on its own. The steps are read off the "
            + "enhance's own program for that image, so the chain's result is the enhance's. Nothing is run; a program or a setting no "
            + "single step can follow is refused.")
        {
            Arguments = { projectArg, imageArg },
            Options = { nameOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!operations.TryGet(EnhanceOperation.Id, out var registered) || registered is not EnhanceOperation enhance)
            {
                consoleHost.WriteError("[workbench] the enhance is not registered here");
                return 1;
            }

            return PrintAdded(await WorkbenchProjects.AddChainAsync(parseResult.Required(projectArg), runner, enhance, parseResult.Required(imageArg),
                parseResult.GetValue(nameOpt), line => consoleHost.WriteScrollable($"[workbench] {line}"), ct));
        });
        return cmd;
    }

    private Command BuildNewCommand()
    {
        var framesArg = new Argument<string>("folder") { Description = "The capture folder: a night's lights (and calibration frames, which the stack finds itself). The project and every result are kept in its workdir folder; the frames are read, never written." };
        var projectOpt = new Option<string?>("--project") { Description = "Keep the project in this folder instead of the capture's workdir (it must be outside the capture folder)." };
        var cmd = new Command("new",
            "Make a project for a capture: <folder>/workdir/project.json holding one image, the capture folder, whose one edit is the stack at its defaults. "
            + "Nothing is run (workbench run stacks it); workbench detected says what the folder holds. Refused where the workdir already holds a project, "
            + "the capture folder is not there, or it is inside the project's step store.")
        {
            Arguments = { framesArg },
            Options = { projectOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var frames = parseResult.Required(framesArg);
            var made = parseResult.GetValue(projectOpt) is { Length: > 0 } elsewhere
                ? await WorkbenchProjects.NewAsync(elsewhere, frames, ct)
                : await WorkbenchProjects.NewAsync(frames, ct);
            var folder = parseResult.GetValue(projectOpt) is { Length: > 0 } kept ? kept : WorkbenchProjects.WorkdirOf(frames);
            if (made.Project is not { } project)
            {
                consoleHost.WriteError($"[workbench] {made.Refusal}");
                return 1;
            }

            var image = project.Images[0];
            consoleHost.WriteScrollable($"[workbench] made {Path.GetFullPath(folder)}: {image.Name} from {image.Path}, one step: {image.Edits[0].Operation}");
            return 0;
        });
        return cmd;
    }

    private Command BuildProcessCommand()
    {
        var projectArg = ProjectArgument();
        var imageArg = new Argument<string>("image") { Description = "The image with the stack step." };
        var groupArg = new Argument<string>("group") { Description = "The master to process: the name of one of the stack's groups (workbench status lists them)." };
        var nameOpt = new Option<string?>("--name") { Description = "What the chain's result is called. Default: <image>-<group>, made safe and unique." };
        var cmd = new Command("process",
            "Process one master of a stack: add an image that starts from the master of that group, its edits the enhance laid out as its chain of "
            + "single steps (as workbench chain). Nothing is run. Refused where the stack has not been run or did not give that group.")
        {
            Arguments = { projectArg, imageArg, groupArg },
            Options = { nameOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!operations.TryGet(EnhanceOperation.Id, out var registered) || registered is not EnhanceOperation enhance)
            {
                consoleHost.WriteError("[workbench] the enhance is not registered here");
                return 1;
            }

            return PrintAdded(await WorkbenchProjects.AddMasterChainAsync(parseResult.Required(projectArg), runner, enhance, parseResult.Required(imageArg),
                parseResult.Required(groupArg), parseResult.GetValue(nameOpt), line => consoleHost.WriteScrollable($"[workbench] {line}"), ct));
        });
        return cmd;
    }

    // Prints what a routine added, or why it added nothing; the exit code is 1 on a refusal.
    private int PrintAdded(ChainAddedResult result)
    {
        if (result.Refusal is { } refusal)
        {
            consoleHost.WriteError($"[workbench] {refusal}");
            return 1;
        }

        foreach (var each in result.Added)
        {
            consoleHost.WriteScrollable($"[workbench] added {each.Name}" + (each.From is { } from ? $" from {from}{(each.Result is { } picked ? $"'s {picked}" : string.Empty)}" : string.Empty)
                + $": {string.Join(", ", each.Edits.Select(static step => step.Operation))}");
        }

        return 0;
    }

    private static Argument<string> ProjectArgument() => new("project") { Description = "The project's folder: it holds project.json." };

    private static Option<string?> ImageOption(string what) => new("--image") { Description = what };

    private static Option<bool> VerifyFramesOption() => new("--verify-frames")
    {
        Description = "Read every frame of every set of frames to know its key, whatever the digest index beside the store holds, and correct "
            + "the index. Without it a frame whose size and last write time have not moved is not read again, which is wrong only "
            + "for a frame changed in place under the same size and time.",
    };

    private Command BuildStatusCommand()
    {
        var projectArg = ProjectArgument();
        var imageOpt = ImageOption("Count as needed only what this image needs. Every image is still planned and reported, so every set of frames is "
            + "read (or taken from the digest index) whatever is named. Default: every image.");
        var verifyOpt = VerifyFramesOption();
        var toOpt = ToOption();
        var cmd = new Command("status",
            "What a run would do, with nothing run and no result written: each image's edits with where they stand (kept in the store, "
            + "derived, will run) and the command line that does each from a terminal. The one file it may write is the digest index "
            + "of a set of frames, so that the frames it read are not read again.")
        {
            Arguments = { projectArg },
            Options = { imageOpt, verifyOpt, toOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
            Print(
                await runner.StatusAsync(
                    parseResult.Required(projectArg),
                    new WorkbenchRunOptions
                    {
                        Image = parseResult.GetValue(imageOpt),
                        VerifyFrames = parseResult.GetValue(verifyOpt),
                        Through = ThroughOf(parseResult.GetValue(toOpt)),
                    },
                    ct),
                commandLines: true));
        return cmd;
    }

    private Command BuildRunCommand()
    {
        var projectArg = ProjectArgument();
        var imageOpt = ImageOption("Make only this image, with whatever its edits take. Every image is still planned and reported, so every set of "
            + "frames is read (or taken from the digest index) whatever is named. Default: every image.");
        var exportOpt = new Option<string?>("--export")
        {
            Description = "Also write --image's final result as this FITS file. A derived result (a crop) has no file in the store, "
                + "so this is how one is had: its operation writes it, as its verb would.",
        };
        var verifyOpt = VerifyFramesOption();
        var toOpt = ToOption();
        var cmd = new Command("run",
            "Make the project's images: work out every step's key, take from the store each step whose key is there and run the rest. "
            + "Run twice, the second run runs nothing; after a changed setting, only that step and the ones after it run.")
        {
            Arguments = { projectArg },
            Options = { imageOpt, exportOpt, verifyOpt, toOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var export = parseResult.GetValue(exportOpt);
            if (export is not null && parseResult.GetValue(imageOpt) is null)
            {
                consoleHost.WriteError("[workbench] --export writes one image: name it with --image.");
                return 1;
            }

            var report = await runner.RunAsync(
                parseResult.Required(projectArg),
                new WorkbenchRunOptions
                {
                    Image = parseResult.GetValue(imageOpt),
                    ExportPath = export,
                    VerifyFrames = parseResult.GetValue(verifyOpt),
                    Through = ThroughOf(parseResult.GetValue(toOpt)),
                    Say = line => consoleHost.WriteScrollable($"[workbench] {line}"),
                },
                ct);
            var exit = Print(report, commandLines: false);
            if (report.Exported is { } exported)
            {
                consoleHost.WriteScrollable($"[workbench] exported {exported}");
            }

            return exit;
        });
        return cmd;
    }

    private Command BuildOperationsCommand()
    {
        var cmd = new Command("operations", "The operations an edit can name, each with its verb, how its result is kept and its settings at their defaults.");
        cmd.SetAction(_ =>
        {
            foreach (var operation in operations.All)
            {
                var description = operation.Description;
                var kept = description.Output switch
                {
                    OperationOutputKind.Derived => "derived, never stored",
                    OperationOutputKind.Finding => "writes no pixels, its finding stored",
                    _ => "stored",
                };
                consoleHost.WriteScrollable($"{description.Id} (version {description.Version}): {description.Title}, {kept}; the routine of `tianwen {description.Verb}`");
                if (description.Summary.Length > 0)
                {
                    consoleHost.WriteScrollable($"    {description.Summary}");
                }

                foreach (var role in description.ExtraInputs)
                {
                    consoleHost.WriteScrollable($"    takes {role.Name}: {role.Summary}");
                }

                // What an edit's inputs are where the operation names no places for them.
                if (description.Joint)
                {
                    consoleHost.WriteScrollable("    takes the other images of its run, any number of them: one run for them all");
                }
                else if (description.AnyNumber)
                {
                    consoleHost.WriteScrollable("    takes any number of other images, as a set");
                }

                if (description.NamedByRun)
                {
                    consoleHost.WriteScrollable("    gives one result for each group the scan finds, named by the run; an image takes one with from and result, and the step has no image of its own");
                }

                foreach (var result in description.ExtraResults)
                {
                    consoleHost.WriteScrollable($"    gives {result.Name}{(result.Optional ? ", for some inputs only" : string.Empty)}: {result.Summary}");
                }

                consoleHost.WriteScrollable($"    settings {operation.DefaultSettings}");
                foreach (var setting in description.Settings)
                {
                    consoleHost.WriteScrollable($"    {setting.Name}: {setting.Summary}"
                        + (setting.Measurement is { } measurement ? $" Default: {measurement}" : string.Empty));
                }
            }

            return 0;
        });
        return cmd;
    }

    // Prints a status or a run and answers the exit code: 1 when the project could not be worked on at all, 2 when a
    // step or an export gave no result, else 0.
    private int Print(WorkbenchReport report, bool commandLines)
    {
        if (report.Images.IsEmpty && report.Refusal is { } unreadable)
        {
            consoleHost.WriteError($"[workbench] {unreadable}");
            return 1;
        }

        foreach (var image in report.Images)
        {
            consoleHost.WriteScrollable($"[workbench] {image.Name}: {image.Path}"
                + (image.Key.Length > 0 ? $" ({StepKey.Short(image.Key)})" : string.Empty)
                + (image.FileChangedSinceLastRun ? ", changed since the last run" : string.Empty));
            if (image.Refusal is { } refusal)
            {
                consoleHost.WriteError($"[workbench] {image.Name}: {refusal}");
            }

            foreach (var step in image.Steps)
            {
                // The short word for whose models serve the step (rc, tw or mix), where they say: read at a glance,
                // with the whole of it on the "served by" line below.
                consoleHost.WriteScrollable($"[workbench]   {step.Index + 1} {step.Operation}{(step.ServedTag.Length > 0 ? $" [{step.ServedTag}]" : string.Empty)}: {step.Words}"
                    + (step.Key.Length > 0 ? $" ({StepKey.Short(step.Key)})" : string.Empty)
                    + (step.ChangedSinceLastRun ? ", changed since the last run" : string.Empty)
                    + (step.Output is { } output ? $" -> {output}" : string.Empty));
                if (step.Message is { } message)
                {
                    consoleHost.WriteError($"[workbench]     {message}");
                }

                // What an automatic setting resolved to here: the enhancer, the corrector or the catalogue the step's
                // key holds. Said for a kept step as for one that runs, since it is what made (or would make) its result.
                if (step.ServedBy.Length > 0)
                {
                    consoleHost.WriteScrollable($"[workbench]     served by: {step.ServedBy}");
                }

                // A stack's masters, one per group: the names `workbench process` takes.
                if (!step.Results.IsDefaultOrEmpty)
                {
                    consoleHost.WriteScrollable($"[workbench]     masters: {string.Join(", ", step.Results)}");
                }

                if (commandLines && !step.CommandLine.IsDefaultOrEmpty)
                {
                    consoleHost.WriteScrollable($"[workbench]     {StepCommandLine.Display(step.CommandLine)}");
                }
            }
        }

        if (report.FramesDigested + report.FramesFromIndex > 0)
        {
            consoleHost.WriteScrollable($"[workbench] frames: {report.FramesDigested} read for their digest, {report.FramesFromIndex} known by the index"
                + (report.FramesIndexNotSaved ? " (the index could not be saved)" : string.Empty));
        }

        if (report.Refusal is { } refused)
        {
            consoleHost.WriteError($"[workbench] {refused}");
        }

        if (report.Note is { } note)
        {
            consoleHost.WriteScrollable($"[workbench] note: {note}");
        }

        return report.Succeeded ? 0 : 2;
    }

    private static Option<int?> ToOption() => new("--to")
    {
        Description = "Make --image only up to this step, n being the step's number as status prints it (from 1). The steps after it are not "
            + "asked for. Needs --image; not combined with --export.",
    };

    // The option's step number (from 1) as the runner's place in the edit list (from 0). A number below 1 is passed on as it is, so
    // that the runner refuses it in words.
    private static int? ThroughOf(int? to) => to is { } n ? n - 1 : null;
}
