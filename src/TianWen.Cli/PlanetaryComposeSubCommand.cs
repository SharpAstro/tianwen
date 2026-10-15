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
/// <c>tianwen planetary compose &lt;stack&gt;... -o &lt;master.fits&gt;</c> (#1278, <see cref="PlanetaryComposition"/>): a colour master from a
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
        var viewingWeightsOpt = ViewingWeightsOption();
        var bandWeightsOpt = BandWeightsOption();
        var relightOpt = RelightOption();
        var ringFeatherOpt = RingFeatherOption();
        var noBalanceOpt = NoColourBalanceOption();
        var command = new Command("compose", "A colour master from mono planetary stacks (#1278): each moved onto one disk by its limb, de-rotated to one instant, each filter averaged, red, green and blue joined; IR or L beside it as the luminance.")
        {
            Arguments = { stacksArg },
            Options = { outputOpt, labels.Planet, labels.Utc, labels.Filter, lrgbOpt, viewingWeightsOpt, bandWeightsOpt, relightOpt, ringFeatherOpt, noBalanceOpt },
        };
        command.Subcommands.Add(BuildIngest());
        command.Subcommands.Add(BuildRegister());
        command.Subcommands.Add(BuildDerotate());
        command.Subcommands.Add(BuildJoin());
        command.Subcommands.Add(BuildLuminance());
        command.Subcommands.Add(BuildLrgb());

        command.SetAction(async (parseResult, ct) =>
        {
            if (Ingested(parseResult.GetValue(stacksArg) ?? [], labels, parseResult) is not { } stacks)
            {
                return 1;
            }
            if (BandWeightsOf(parseResult.GetValue(bandWeightsOpt)) is not { } bandWeights
                || ViewingWeightsOf(parseResult.GetValue(viewingWeightsOpt)) is not { } viewingWeights
                || RelightOf(parseResult.GetValue(relightOpt)) is not { } relight
                || !TryRingFeather(parseResult.GetValue(ringFeatherOpt), out var ringFeather))
            {
                return 1;
            }
            var (composed, derotation, refusal) = await Task.Run(() => PlanetaryComposition.Run(stacks, Say, parseResult.GetValue(lrgbOpt),
                viewingWeights, bandWeights, relight, ringFeather), ct);
            if (composed is null || derotation is null)
            {
                consoleHost.WriteError(refusal ?? "nothing composed");
                return 1;
            }
            var output = parseResult.GetValue(outputOpt) ?? DefaultName(stacks[0].Planet, derotation.Instant);
            return await WriteComposedAsync(composed, output, !parseResult.GetValue(noBalanceOpt), ct) ? 0 : 1;
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
        var viewingWeightsOpt = ViewingWeightsOption();
        var relightOpt = RelightOption();
        var ringFeatherOpt = RingFeatherOption();
        var command = new Command("derotate", "Step 3: every registered stack carried to the reference's instant on its disk, north decided by agreement between two stacks of one filter.")
        {
            Arguments = { stacksArg },
            Options = { outputOpt, viewingWeightsOpt, relightOpt, ringFeatherOpt },
        };
        command.SetAction(async (parseResult, ct) =>
        {
            if (ReadStacks(parseResult.GetValue(stacksArg) ?? []) is not { } stacks
                || ViewingWeightsOf(parseResult.GetValue(viewingWeightsOpt)) is not { } viewingWeights
                || RelightOf(parseResult.GetValue(relightOpt)) is not { } relight
                || !TryRingFeather(parseResult.GetValue(ringFeatherOpt), out var ringFeather))
            {
                return 1;
            }
            var (derotation, refusal) = await Task.Run(() => PlanetaryComposition.Derotate(stacks, Say, viewingWeights, relight, ringFeather), ct);
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
        var bandWeightsOpt = BandWeightsOption();
        var noBalanceOpt = NoColourBalanceOption();
        var command = new Command("join", "Step 4: each filter's de-rotated stacks averaged, red, green and blue joined into one colour master; IR or L beside it as the luminance. A stack's join weight (derotate --viewing-weights) is read from <stack>.weight.fits beside it.")
        {
            Arguments = { stacksArg },
            Options = { outputOpt, bandWeightsOpt, noBalanceOpt },
        };
        command.SetAction(async (parseResult, ct) =>
        {
            if (ReadStacks(parseResult.GetValue(stacksArg) ?? []) is not { } stacks)
            {
                return 1;
            }
            if (BandWeightsOf(parseResult.GetValue(bandWeightsOpt)) is not { } bandWeights)
            {
                return 1;
            }
            var (composed, refusal) = PlanetaryComposition.Join(stacks, Say, bandWeights);
            if (composed is null)
            {
                consoleHost.WriteError(refusal ?? "nothing joined");
                return 1;
            }
            var output = parseResult.GetValue(outputOpt) ?? DefaultName(stacks[0].Planet, stacks[0].Instant);
            return await WriteComposedAsync(composed, output, !parseResult.GetValue(noBalanceOpt), ct) ? 0 : 1;
        });
        return command;
    }

    private Command BuildLuminance()
    {
        var masterArg = new Argument<string>("master") { Description = "The colour master to make the luminance from (FITS): a Bayer drizzle's, as stacked." };
        var ontoOpt = new Option<string?>("--onto") { Description = "Another master of the same capture (FITS) to place the luminance on, by the two limb fits: the demosaic's, whose colours it details." };
        var wavelengthOpt = new Option<string?>("--wavelength") { Description = "The channels' effective wavelengths, nm, red, green and blue (610,530,460 when not given)." };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "The luminance to write (FITS); <master>_luminance.fits beside the master when not given." };
        var command = new Command("luminance", "A luminance from a colour master's planes (#1330): each on green's scale by the disk, weighted by its noise, and placed on another master's disk with --onto; sharpen it as a mono master at the wavelength it prints, then carry it in with the lrgb step.")
        {
            Arguments = { masterArg },
            Options = { ontoOpt, wavelengthOpt, outputOpt },
        };
        command.SetAction(async (parseResult, ct) =>
        {
            var masterPath = parseResult.GetValue(masterArg) ?? "";
            if (!Image.TryReadFitsFile(masterPath, out var master))
            {
                consoleHost.WriteError($"{masterPath}: not a readable FITS");
                return 1;
            }
            Image? onto = null;
            if (parseResult.GetValue(ontoOpt) is { } ontoPath && !Image.TryReadFitsFile(ontoPath, out onto))
            {
                consoleHost.WriteError($"{ontoPath}: not a readable FITS");
                return 1;
            }
            if (PlanetaryCaptureName.Named(master.ImageMeta.ObjectName) is not { } planet || PlanetaryBestStack.InstantOf(master, epoch: null) is not { } instant)
            {
                consoleHost.WriteError($"{masterPath}: no planet (OBJECT) or instant (DATE-OBS) in its header");
                return 1;
            }
            if (PlanetaryMasterScore.Wavelengths(consoleHost, parseResult.GetValue(wavelengthOpt) ?? "610,530,460") is not { Length: 3 } wavelengths)
            {
                consoleHost.WriteError("--wavelength: red, green and blue");
                return 1;
            }
            var (luminance, parts, noise, wavelengthNm, refusal) = await Task.Run(() => PlanetaryComposition.Luminance(master, planet, instant, wavelengths, onto, Say), ct);
            if (luminance is null)
            {
                consoleHost.WriteError(refusal ?? "no luminance");
                return 1;
            }
            var inv = CultureInfo.InvariantCulture;
            string[] names = ["red", "green", "blue"];
            for (var c = 0; c < parts.Length; c++)
            {
                consoleHost.WriteScrollable(string.Create(inv,
                    $"{names[c]}: scale {parts[c].Scale:0.0000} onto green by the disk, noise {parts[c].ScaledNoise:G4} on green's scale, weight {parts[c].Weight:P1}"));
            }
            var best = parts.Min(p => p.ScaledNoise);
            consoleHost.WriteScrollable(string.Create(inv,
                $"the luminance's noise {noise:G4} on the sky against the best single channel's {best:G4}: {best / noise:F2}x its signal to noise; sharpen it at {wavelengthNm:0} nm"));
            var output = parseResult.GetValue(outputOpt) ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(masterPath)) ?? ".", Path.GetFileNameWithoutExtension(masterPath) + "_luminance.fits");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");
            luminance.WriteToFitsFile(output);
            consoleHost.WriteScrollable($"wrote {output}");
            return 0;
        });
        return command;
    }

    private Command BuildLrgb()
    {
        var masterArg = new Argument<string>("master") { Description = "The colour master join wrote (FITS)." };
        var luminanceOpt = new Option<string>("--luminance") { Description = "The luminance join wrote beside it (FITS).", Required = true };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "The detailed master to write (FITS); <master>_lrgb.fits beside the master when not given." };
        var noBalanceOpt = NoColourBalanceOption();
        var command = new Command("lrgb", "Step 5, optional: the luminance's detail carried into each colour plane by the deep-sky LRGB step, read on the disk: each scale against the disk's level ratio, the colour's shift and the bands' correlation with the luminance.")
        {
            Arguments = { masterArg },
            Options = { luminanceOpt, outputOpt, noBalanceOpt },
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
                && await Task.Run(() => PlanetaryLimbFit.FitAt(master, planet, instant), ct) is (_, var disk, _))
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
            return await WriteComposedAsync(new PlanetaryComposed(detailed, null), output, !parseResult.GetValue(noBalanceOpt), ct) ? 0 : 1;
        });
        return command;
    }

    // A line of the run's report.
    private void Say(string line) => consoleHost.WriteScrollable(line);

    // What leaves a colour master as its filters gave it, as planetary stack's own switch does.
    private static Option<bool> NoColourBalanceOption() => new("--no-colour-balance")
    {
        Description = "Leave the colour master as its filters gave it, rather than balanced to the planet's own colour (Jupiter's or Saturn's, as planetary stack balances its masters, #1580).",
    };

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
            if (PlanetaryGeometrySubCommands.ParseUtc(utcText) is not { } parsed)
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

    private static Option<string?> ViewingWeightsOption() => new Option<string?>("--viewing-weights")
    {
        Description = "Weigh each de-rotated stack's pixels by how it saw them (#1349), written beside each stack as <name>.weight.fits: source (by how squarely it saw each pixel's source, nothing past the de-rotation's 0.9-radius source limit, so no stack's unturned rim enters where another stack saw the planet; fix C) or limb (source inside the disk, and the limb left to the stacks that saw it there: from 0.8 to 0.9 of the target's radius each stack's own unturned pixel takes over from the turned samples).",
    };

    private static Option<string?> RelightOption() => new Option<string?>("--relight")
    {
        Description = "How the de-rotation relights what it carries (#1349, the handover circle): blurred (the default: only what lies above each stack's own sky is relit, by the ratio of its own limb model through its core blur, so a sample carried toward the limb darkens as the stack's own blurred limb does) or minnaert (every compose before 2026-10-12: the whole sample, its sky included, relit by Minnaert's law, with none of the limb's blur).",
    };

    private static Option<string?> RingFeatherOption() => new Option<string?>("--ring-feather")
    {
        Description = "Fade the turn in beside Saturn's rings (#1537, the ring band's seam), as INNER,OUTER in multiples of each stack's core blur: past the rings' 2 px margin a pixel keeps its own value out to INNER sigmas and takes the turn whole from OUTER, read at the pixel and at its source. 2,4 is the measured setting. For a nearly zonal night only: it takes the seam out of a featureless Saturn, but a spot or storm beside the band stays where it was, wrong by the planet's turn (on a twin with 5 % spots, 2.5 times the seam). Off when not given.",
    };

    // --ring-feather read: absent is no feather; false and said when unreadable.
    private bool TryRingFeather(string? text, out (double Inner, double Outer)? feather)
    {
        feather = null;
        if (text is null)
        {
            return true;
        }
        var parts = text.Split(',');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var inner)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var outer)
            && inner >= 0 && outer > inner)
        {
            feather = (inner, outer);
            return true;
        }
        consoleHost.WriteError($"--ring-feather {text}: INNER,OUTER sigmas, 0 <= INNER < OUTER");
        return false;
    }

    // --relight read: absent is the blurred relight; null and said when unreadable.
    private PlanetaryTurnRelight? RelightOf(string? text)
    {
        switch (text?.ToLowerInvariant())
        {
            case null or "blurred":
                return PlanetaryTurnRelight.Blurred;
            case "minnaert":
                return PlanetaryTurnRelight.Minnaert;
            default:
                consoleHost.WriteError($"--relight {text}: blurred or minnaert");
                return null;
        }
    }

    // --viewing-weights read: absent is every pixel alike; null and said when unreadable.
    private PlanetaryViewingWeights? ViewingWeightsOf(string? text)
    {
        switch (text?.ToLowerInvariant())
        {
            case null:
                return PlanetaryViewingWeights.None;
            case "source":
                return PlanetaryViewingWeights.Source;
            case "limb":
                return PlanetaryViewingWeights.Limb;
            default:
                consoleHost.WriteError($"--viewing-weights {text}: source or limb");
                return null;
        }
    }

    private static Option<string?> BandWeightsOption() => new Option<string?>("--band-weights")
    {
        Description = "Join each filter's stacks band by band (#1349 arm B): matched (each a trous band weighed by its transfer against the sharpest stack over its noise, so the finest bands come mostly from the sharp stacks and the coarse ones from all) or wiener (the same, each band rescaled to the sharpest stack's own transfer, a multi-frame Wiener).",
    };

    // --band-weights read: absent is the plain join; null and said when unreadable.
    private PlanetaryBandWeights? BandWeightsOf(string? text)
    {
        switch (text?.ToLowerInvariant())
        {
            case null:
                return PlanetaryBandWeights.None;
            case "matched":
                return PlanetaryBandWeights.Matched;
            case "wiener":
                return PlanetaryBandWeights.Wiener;
            default:
                consoleHost.WriteError($"--band-weights {text}: matched or wiener");
                return null;
        }
    }

    // Stacks a step wrote, read back with their labels, or null with each failure said.
    private List<PlanetaryMonoStack>? ReadStacks(IReadOnlyList<string> paths)
    {
        var stacks = new List<PlanetaryMonoStack>(paths.Count);
        foreach (var path in paths)
        {
            if (PlanetaryComposition.IsJoinWeightPath(path))
            {
                // A join weight a glob over the step's folder took in: read beside its stack, never as one.
                continue;
            }
            var name = StackName(path);
            // With its join weight (#1349 fix C), where derotate --viewing-weights wrote one beside it.
            var (stack, refusal) = PlanetaryComposition.ReadStack(name, path);
            if (stack is null)
            {
                consoleHost.WriteError(refusal ?? $"{path}: not read");
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
            PlanetaryComposition.WriteStack(stack, path);
            consoleHost.WriteScrollable($"wrote {path}{(stack.JoinWeight is null ? "" : " and its join weight beside it")}");
        }
        return true;
    }

    // The colour master, balanced to the planet's own colour as the stack balances its masters unless asked not to (#1580), its planetary
    // preview beside it, and the luminance beside both when there is one.
    private async Task<bool> WriteComposedAsync(PlanetaryComposed composed, string output, bool balanceColour, System.Threading.CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".";
        Directory.CreateDirectory(folder);
        var (balanced, balance, how) = balanceColour && composed.Master.ChannelCount == 3
            ? PlanetaryComposition.BalancedToPlanet(composed.Master)
            : (null, null, "colours left as the filters gave them");
        var written = balanced ?? composed.Master;
        var stem = Path.Combine(folder, Path.GetFileNameWithoutExtension(output));
        try
        {
            written.WriteToFitsFile(output, null, balance?.HeaderCards());
            await previewRenderer.RenderPlanetaryAsync(written, stem + ".png", ct: ct);
            consoleHost.WriteScrollable($"wrote {output} and its preview {stem}.png");
            if (composed.Master.ChannelCount == 3)
            {
                consoleHost.WriteScrollable(how);
            }
        }
        finally
        {
            balanced?.Release();
        }
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
