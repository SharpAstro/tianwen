using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Console.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen planetary-compose &lt;stack&gt;... -o &lt;master.fits&gt;</c> (#1278, <see cref="PlanetaryComposition"/>): a colour master from a
/// mono camera's stacks, one a filter and several runs of each, de-rotated to one instant and joined. The planetary twin of
/// <c>image combine</c>: the command itself runs the recipe, and its steps are verbs of their own through FITS files
/// (<c>ingest</c>, <c>register</c>, <c>derotate</c>, <c>join</c>), each on the same routine, so run one by one they give the recipe's
/// master to the bit. A stack's planet, instant and filter come from its header or its WinJUPOS-style name
/// (<c>2026-09-01-0706_4-MPD-G-Sat</c>: 07:06:24 UTC, green, Saturn); an IR-pass or L stack is the luminance, written beside the master.
/// Renamed with the rest of the planetary verbs in the noun-verb wave (#1276).
/// </summary>
internal sealed class PlanetaryComposeSubCommand(IConsoleHost consoleHost, MasterPreviewRenderer previewRenderer)
{
    public Command Build()
    {
        var stacksArg = new Argument<string[]>("stacks") { Description = "The mono stacks (TIFF or FITS), every filter and every run.", Arity = ArgumentArity.ZeroOrMore };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "The colour master to write (FITS); <planet>_<yyyy-MM-dd-HHmm>_composed.fits in the current folder when not given." };
        var labels = LabelOptions();
        var command = new Command("planetary-compose", "A colour master from mono planetary stacks (#1278): each moved onto one disk by its limb, de-rotated to one instant, each filter averaged, red, green and blue joined; IR or L beside it as the luminance.")
        {
            Arguments = { stacksArg },
            Options = { outputOpt, labels.Planet, labels.Utc, labels.Filter },
        };
        command.Subcommands.Add(BuildIngest());
        command.Subcommands.Add(BuildRegister());
        command.Subcommands.Add(BuildDerotate());
        command.Subcommands.Add(BuildJoin());

        command.SetAction(async (parseResult, ct) =>
        {
            if (Ingested(parseResult.GetValue(stacksArg) ?? [], labels, parseResult) is not { } stacks)
            {
                return 1;
            }
            var (composed, derotation, refusal) = await Task.Run(() => PlanetaryComposition.Run(stacks, Say), ct);
            if (composed is null || derotation is null)
            {
                consoleHost.WriteError(refusal ?? "nothing composed");
                return 1;
            }
            var output = parseResult.GetValue(outputOpt) ?? DefaultName(stacks[0].Planet, derotation.Instant);
            return await WriteComposedAsync(composed, output, ct) ? 0 : 1;
        });
        return command;
    }

    private Command BuildIngest()
    {
        var stacksArg = new Argument<string[]>("stacks") { Description = "The mono stacks (TIFF or FITS).", Arity = ArgumentArity.OneOrMore };
        var outputOpt = new Option<string>("--output", "-o") { Description = "The folder to write each stack into, as <name>.ingested.fits.", Required = true };
        var labels = LabelOptions();
        var command = new Command("ingest", "Step 1: each stack labelled with its planet, instant and filter (from its header or its WinJUPOS-style name), written as FITS.")
        {
            Arguments = { stacksArg },
            Options = { outputOpt, labels.Planet, labels.Utc, labels.Filter },
        };
        command.SetAction(parseResult =>
        {
            if (Ingested(parseResult.GetValue(stacksArg) ?? [], labels, parseResult) is not { } stacks)
            {
                return 1;
            }
            return WriteStacks(stacks, parseResult.GetValue(outputOpt) ?? ".", "ingested") ? 0 : 1;
        });
        return command;
    }

    private Command BuildRegister()
    {
        var stacksArg = new Argument<string[]>("stacks") { Description = "The ingested stacks (FITS, as ingest writes them).", Arity = ArgumentArity.OneOrMore };
        var outputOpt = new Option<string>("--output", "-o") { Description = "The folder to write each stack into, as <name>.registered.fits.", Required = true };
        var command = new Command("register", "Step 2: every stack's limb fitted at its own instant, and the stack moved so its disk's centre lands on the reference's.")
        {
            Arguments = { stacksArg },
            Options = { outputOpt },
        };
        command.SetAction(async (parseResult, ct) =>
        {
            if (ReadStacks(parseResult.GetValue(stacksArg) ?? []) is not { } stacks)
            {
                return 1;
            }
            var (registration, refusal) = await Task.Run(() => PlanetaryComposition.Register(stacks, Say), ct);
            if (registration is null)
            {
                consoleHost.WriteError(refusal ?? "nothing registered");
                return 1;
            }
            return WriteStacks(registration.Stacks, parseResult.GetValue(outputOpt) ?? ".", "registered") ? 0 : 1;
        });
        return command;
    }

    private Command BuildDerotate()
    {
        var stacksArg = new Argument<string[]>("stacks") { Description = "The registered stacks (FITS, as register writes them).", Arity = ArgumentArity.OneOrMore };
        var outputOpt = new Option<string>("--output", "-o") { Description = "The folder to write each stack into, as <name>.derotated.fits.", Required = true };
        var command = new Command("derotate", "Step 3: every registered stack carried to the reference's instant on its disk, north decided by agreement between two stacks of one filter.")
        {
            Arguments = { stacksArg },
            Options = { outputOpt },
        };
        command.SetAction(async (parseResult, ct) =>
        {
            if (ReadStacks(parseResult.GetValue(stacksArg) ?? []) is not { } stacks)
            {
                return 1;
            }
            var (derotation, refusal) = await Task.Run(() => PlanetaryComposition.Derotate(stacks, Say), ct);
            if (derotation is null)
            {
                consoleHost.WriteError(refusal ?? "nothing de-rotated");
                return 1;
            }
            return WriteStacks(derotation.Stacks, parseResult.GetValue(outputOpt) ?? ".", "derotated") ? 0 : 1;
        });
        return command;
    }

    private Command BuildJoin()
    {
        var stacksArg = new Argument<string[]>("stacks") { Description = "The de-rotated stacks (FITS, as derotate writes them).", Arity = ArgumentArity.OneOrMore };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "The colour master to write (FITS); <planet>_<yyyy-MM-dd-HHmm>_composed.fits in the current folder when not given." };
        var command = new Command("join", "Step 4: each filter's de-rotated stacks averaged, red, green and blue joined into one colour master; IR or L beside it as the luminance.")
        {
            Arguments = { stacksArg },
            Options = { outputOpt },
        };
        command.SetAction(async (parseResult, ct) =>
        {
            if (ReadStacks(parseResult.GetValue(stacksArg) ?? []) is not { } stacks)
            {
                return 1;
            }
            var (composed, refusal) = PlanetaryComposition.Join(stacks);
            if (composed is null)
            {
                consoleHost.WriteError(refusal ?? "nothing joined");
                return 1;
            }
            var output = parseResult.GetValue(outputOpt) ?? DefaultName(stacks[0].Planet, stacks[0].Instant);
            return await WriteComposedAsync(composed, output, ct) ? 0 : 1;
        });
        return command;
    }

    // A line of the run's report.
    private void Say(string line) => consoleHost.WriteScrollable(line);

    // The options that label a stack its header and name do not: the planet, the instant and the filter, for every stack given.
    private static (Option<string?> Planet, Option<string?> Utc, Option<string?> Filter) LabelOptions() => (
        new Option<string?>("--planet") { Description = "jupiter or saturn; read off each stack's header or name when not given." },
        new Option<string?>("--utc") { Description = "Every stack's instant (ISO 8601, UTC); each its own header's or WinJUPOS-style name's when not given." },
        new Option<string?>("--filter") { Description = "Every stack's filter (R, G, B, IR or L); each its own header's or name's when not given." });

    // Every path read and ingested, or null with each failure said.
    private List<PlanetaryMonoStack>? Ingested(IReadOnlyList<string> paths, (Option<string?> Planet, Option<string?> Utc, Option<string?> Filter) labels,
        ParseResult parseResult)
    {
        if (paths.Count == 0)
        {
            consoleHost.WriteError("no stacks given");
            return null;
        }
        CatalogIndex? planet = null;
        if (parseResult.GetValue(labels.Planet) is { } planetText)
        {
            if (PlanetaryCaptureName.Named(planetText) is not { } named)
            {
                consoleHost.WriteError($"--planet {planetText}: jupiter or saturn");
                return null;
            }
            planet = named;
        }
        DateTimeOffset? utc = null;
        if (parseResult.GetValue(labels.Utc) is { } utcText)
        {
            if (!DateTimeOffset.TryParse(utcText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                consoleHost.WriteError($"--utc {utcText}: not an ISO 8601 time");
                return null;
            }
            utc = parsed;
        }
        Filter? filter = null;
        if (parseResult.GetValue(labels.Filter) is { } filterText)
        {
            if (PlanetaryComposition.RoleOf(PlanetaryCaptureName.WavelengthNm(filterText)) is not { } role)
            {
                consoleHost.WriteError($"--filter {filterText}: R, G, B, IR or L");
                return null;
            }
            filter = role;
        }
        var stacks = new List<PlanetaryMonoStack>(paths.Count);
        foreach (var path in paths)
        {
            if (!Image.TryReadImageFile(path, out var image))
            {
                consoleHost.WriteError($"{path}: not a readable image");
                return null;
            }
            var (stack, refusal) = PlanetaryComposition.Ingest(path, image, planet, utc, filter);
            if (stack is null)
            {
                consoleHost.WriteError(refusal ?? $"{path}: not ingested");
                return null;
            }
            stacks.Add(stack);
        }
        return stacks;
    }

    // Stacks a step wrote, read back with their labels, or null with each failure said.
    private List<PlanetaryMonoStack>? ReadStacks(IReadOnlyList<string> paths)
    {
        var stacks = new List<PlanetaryMonoStack>(paths.Count);
        foreach (var path in paths)
        {
            var name = StackName(path);
            if (!Image.TryReadFitsFile(path, out var image) || PlanetaryComposition.FromIngested(name, image) is not { } stack)
            {
                consoleHost.WriteError($"{path}: not a stack a compose step wrote (one plane, with OBJECT, DATE-OBS and FILTER)");
                return null;
            }
            stacks.Add(stack);
        }
        return stacks;
    }

    // Each stack as <folder>/<name>.<step>.fits.
    private bool WriteStacks(IEnumerable<PlanetaryMonoStack> stacks, string folder, string step)
    {
        Directory.CreateDirectory(folder);
        foreach (var stack in stacks)
        {
            var path = Path.Combine(folder, $"{stack.Name}.{step}.fits");
            stack.Image.WriteToFitsFile(path);
            consoleHost.WriteScrollable($"wrote {path}");
        }
        return true;
    }

    // The colour master, its planetary preview beside it, and the luminance beside both when there is one.
    private async Task<bool> WriteComposedAsync(PlanetaryComposed composed, string output, System.Threading.CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".";
        Directory.CreateDirectory(folder);
        composed.Master.WriteToFitsFile(output);
        var stem = Path.Combine(folder, Path.GetFileNameWithoutExtension(output));
        await previewRenderer.RenderPlanetaryAsync(composed.Master, stem + ".png", ct: ct);
        consoleHost.WriteScrollable($"wrote {output} and its preview {stem}.png");
        if (composed.Luminance is { } luminance)
        {
            var path = stem + "_luminance.fits";
            luminance.WriteToFitsFile(path);
            consoleHost.WriteScrollable($"wrote the luminance {path}");
        }
        return true;
    }

    // A stack's own name, a step's suffix taken off.
    private static string StackName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        foreach (var step in new[] { ".ingested", ".registered", ".derotated" })
        {
            if (name.EndsWith(step, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^step.Length];
            }
        }
        return name;
    }

    private static string DefaultName(CatalogIndex planet, DateTimeOffset instant)
        => string.Create(CultureInfo.InvariantCulture, $"{planet.ToString().ToLowerInvariant()}_{instant:yyyy-MM-dd-HHmm}_composed.fits");
}
