using System.CommandLine;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen workbench status|run|operations</c>: a project's edit lists run with no window
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
        Subcommands = { BuildStatusCommand(), BuildRunCommand(), BuildOperationsCommand() },
    };

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
        var cmd = new Command("status",
            "What a run would do, with nothing run and no result written: each image's edits with where they stand (kept in the store, "
            + "derived, will run) and the command line that does each from a terminal. The one file it may write is the digest index "
            + "of a set of frames, so that the frames it read are not read again.")
        {
            Arguments = { projectArg },
            Options = { imageOpt, verifyOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
            Print(
                await runner.StatusAsync(
                    parseResult.Required(projectArg),
                    new WorkbenchRunOptions { Image = parseResult.GetValue(imageOpt), VerifyFrames = parseResult.GetValue(verifyOpt) },
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
        var cmd = new Command("run",
            "Make the project's images: work out every step's key, take from the store each step whose key is there and run the rest. "
            + "Run twice, the second run runs nothing; after a changed setting, only that step and the ones after it run.")
        {
            Arguments = { projectArg },
            Options = { imageOpt, exportOpt, verifyOpt },
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
                consoleHost.WriteScrollable($"[workbench]   {step.Index + 1} {step.Operation}: {Describe(step)}"
                    + (step.Key.Length > 0 ? $" ({StepKey.Short(step.Key)})" : string.Empty)
                    + (step.ChangedSinceLastRun ? ", changed since the last run" : string.Empty)
                    + (step.Output is { } output ? $" -> {output}" : string.Empty));
                if (step.Message is { } message)
                {
                    consoleHost.WriteError($"[workbench]     {message}");
                }

                if (commandLines && !step.CommandLine.IsDefaultOrEmpty)
                {
                    consoleHost.WriteScrollable($"[workbench]     {StepCommandLine.Display(step.CommandLine)}");
                }
            }
        }

        if (report.FramesDigested + report.FramesFromIndex > 0)
        {
            consoleHost.WriteScrollable($"[workbench] frames: {report.FramesDigested} read for their digest, {report.FramesFromIndex} known by the index");
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

    private static string Describe(StepReport step) => step.State switch
    {
        StepState.Off => "off",
        StepState.Derived => "derived",
        StepState.Kept => "kept",
        StepState.WillRun => "will run",
        StepState.NotNeeded => "not in the store, and not needed",
        StepState.Ran => "ran",
        StepState.Refused => "refused",
        StepState.Blocked => "blocked",
        _ => step.State.ToString(),
    };
}
