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
        var lrgbOpt = new Option<bool>("--lrgb") { Description = "Carry the IR or L stacks' detail into the colour planes by the deep-sky LRGB step (the lrgb step); an option, since IR's belt contrast is its own." };
        var command = new Command("planetary-compose", "A colour master from mono planetary stacks (#1278): each moved onto one disk by its limb, de-rotated to one instant, each filter averaged, red, green and blue joined; IR or L beside it as the luminance.")
        {
            Arguments = { stacksArg },
            Options = { outputOpt, labels.Planet, labels.Utc, labels.Filter, lrgbOpt },
        };
        command.Subcommands.Add(BuildIngest());
        command.Subcommands.Add(BuildRegister());
        command.Subcommands.Add(BuildDerotate());
        command.Subcommands.Add(BuildJoin());
        command.Subcommands.Add(BuildLrgb());

        command.SetAction(async (parseResult, ct) =>
        {
            if (Ingested(parseResult.GetValue(stacksArg) ?? [], labels, parseResult) is not { } stacks)
            {
                return 1;
            }
            var (composed, derotation, refusal) = await Task.Run(() => PlanetaryComposition.Run(stacks, Say, parseResult.GetValue(lrgbOpt)), ct);
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

    private Command BuildLrgb()
    {
        var masterArg = new Argument<string>("master") { Description = "The colour master join wrote (FITS)." };
        var luminanceOpt = new Option<string>("--luminance") { Description = "The luminance join wrote beside it (FITS).", Required = true };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "The detailed master to write (FITS); <master>_lrgb.fits beside the master when not given." };
        var command = new Command("lrgb", "Step 5, optional: the luminance's detail carried into each colour plane by the deep-sky LRGB step, read on the disk: each scale against the disk's level ratio, the colour's shift and the bands' correlation with the luminance.")
        {
            Arguments = { masterArg },
            Options = { luminanceOpt, outputOpt },
        };
        command.SetAction(async (parseResult, ct) =>
        {
            var masterPath = parseResult.GetValue(masterArg) ?? "";
            var luminancePath = parseResult.GetValue(luminanceOpt) ?? "";
            if (!Image.TryReadFitsFile(masterPath, out var master) || !Image.TryReadFitsFile(luminancePath, out var luminance))
            {
                consoleHost.WriteError($"{masterPath} or {luminancePath}: not a readable FITS");
                return 1;
            }
            var (detailed, scales, refusal) = PlanetaryComposition.WithLuminance(master, luminance);
            if (detailed is null)
            {
                consoleHost.WriteError(refusal ?? "nothing detailed");
                return 1;
            }
            var inv = CultureInfo.InvariantCulture;
            if (PlanetaryCaptureName.Named(master.ImageMeta.ObjectName) is { } planet
                && PlanetaryBestStack.InstantOf(master, epoch: null) is { } instant
                && await Task.Run(() => LimbOf(master, planet, instant), ct) is { } disk)
            {
                var (ratios, chromaShift, correlations, colourRms, luminanceRms) = PlanetaryComposition.ReadLuminance(master, luminance, detailed, disk);
                string[] names = ["red", "green", "blue"];
                for (var c = 0; c < 3; c++)
                {
                    consoleHost.WriteScrollable(string.Create(inv,
                        $"{names[c]}: the scale {scales[c]:0.0000}, the disk's level ratio {ratios[c]:0.0000}, {scales[c] / ratios[c]:0.000} of it"));
                }
                consoleHost.WriteScrollable(string.Create(inv,
                    $"the disk's mean colour moved {chromaShift:0.0000} in chromaticity; the detailed luminance against the luminance, bands 1 to 4: {string.Join(", ", correlations.Select(r => r.ToString("0.000", inv)))}"));
                consoleHost.WriteScrollable(string.Create(inv,
                    $"the detail over the disk's level, bands 1 to 4: the colours' own {string.Join(", ", colourRms.Select(r => r.ToString("0.0000", inv)))}; the luminance's {string.Join(", ", luminanceRms.Select(r => r.ToString("0.0000", inv)))}"));
            }
            else
            {
                consoleHost.WriteScrollable(string.Create(inv, $"the scales red {scales[0]:0.0000}, green {scales[1]:0.0000}, blue {scales[2]:0.0000}; no planet, instant or limb to read them on the disk"));
            }
            var output = parseResult.GetValue(outputOpt) ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(masterPath)) ?? ".", Path.GetFileNameWithoutExtension(masterPath) + "_lrgb.fits");
            return await WriteComposedAsync(new PlanetaryComposed(detailed, null), output, ct) ? 0 : 1;
        });
        return command;
    }

    // A master's disk, its limb fitted at its instant (Saturn's rings in the model), or null when it does not fit.
    private static MetricDisk? LimbOf(Image master, CatalogIndex planet, DateTimeOffset instant)
    {
        var options = PlanetaryLimbFit.OptionsFor(TianWen.Lib.Astrometry.PhysicalEphemeris.Compute(planet, instant));
        return PlanetaryLimbFit.Fit(master, options) is { } fit ? MetricDisk.From(fit, options) : null;
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
