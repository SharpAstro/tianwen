using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.IO;

namespace TianWen.Cli;

/// <summary>
/// The planetary corpus's verbs (docs/plans/planetary-restoration.md, R0): <c>planetary-survey</c> registers every capture
/// into one manifest, <c>planetary-convert</c> makes a video saved one FITS file a frame the SER it should have been, and
/// <c>planetary-crop</c> cuts a SER, or every SER in an archive, to a window tracked on its disk. They only read their
/// sources, and every write keeps the drive's reserve free.
/// </summary>
internal sealed class PlanetaryCorpusSubCommands(IConsoleHost consoleHost, ILogger<PlanetaryCorpusSubCommands> logger)
{
    private static Option<string> KeepFreeOption() => new Option<string>("--keep-free")
    {
        Description = "Space the output's drive keeps free, e.g. 109G (the default: the reserve kept on D:). Asked of the drive before every write.",
        DefaultValueFactory = _ => "109G",
    };

    private static Option<string?> SevenZipOption() => new Option<string?>("--7z")
    {
        Description = "The 7-Zip executable for archives. Default: 7z on the PATH, else the standard Windows install.",
    };

    public Command BuildSurvey()
    {
        var rootsArg = new Argument<string[]>("roots")
        {
            Description = "Folders to survey (recursively); nothing under them is written.",
            Arity = ArgumentArity.OneOrMore,
        };
        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = "The manifest to write, e.g. D:/Astro-Dataset/planetary/manifest.json.",
            Required = true,
        };
        var minFitsOpt = new Option<int>("--min-fits-sequence")
        {
            Description = "FITS files a folder needs to count as a planetary FITS sequence rather than deep-sky subs.",
            DefaultValueFactory = _ => 500,
        };
        var keepFreeOpt = KeepFreeOption();
        var sevenZipOpt = SevenZipOption();

        var command = new Command("planetary-survey", "Register every planetary capture (SER, SER in 7z, AVI, FITS sequences) into one manifest.")
        {
            Arguments = { rootsArg },
            Options = { outputOpt, minFitsOpt, keepFreeOpt, sevenZipOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var roots = parseResult.GetValue(rootsArg) ?? [];
            var missing = roots.FirstOrDefault(r => !Directory.Exists(r));
            if (missing is not null)
            {
                consoleHost.WriteError($"No such folder: {missing}");
                return 1;
            }
            if (!ScratchSpace.TryParseSize(parseResult.GetValue(keepFreeOpt), out var keepFree))
            {
                consoleHost.WriteError($"--keep-free must be a size such as 109G; got '{parseResult.GetValue(keepFreeOpt)}'");
                return 1;
            }
            var sevenZip = SevenZipTool.Find(parseResult.GetValue(sevenZipOpt));
            if (sevenZip is null)
            {
                consoleHost.WriteScrollable("No 7-Zip found: archives are listed as unread.");
            }

            var manifest = await PlanetaryCorpus.SurveyAsync(roots, new CorpusSurveyOptions(sevenZip, parseResult.GetValue(minFitsOpt)), logger, ct);
            var bytes = PlanetaryCorpus.ManifestBytes(manifest);
            var output = Path.GetFullPath(parseResult.Required(outputOpt));
            var folder = Path.GetDirectoryName(output) ?? ".";
            Directory.CreateDirectory(folder);
            if (ScratchSpace.Refusal(folder, bytes.Length, keepFree) is { } refusal)
            {
                consoleHost.WriteError(refusal);
                return 1;
            }
            var partial = output + ".partial";
            await File.WriteAllBytesAsync(partial, bytes, ct);
            File.Move(partial, output, overwrite: true);

            foreach (var group in manifest.Captures.GroupBy(c => c.Kind).OrderBy(g => g.Key))
            {
                var frames = group.Sum(c => (long)(c.Header?.FrameCount ?? c.Frames ?? 0));
                var size = ScratchSpace.Size(group.Sum(c => c.LengthBytes));
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture, $"{group.Key}: {group.Count()} captures, {frames} frames, {size}"));
            }
            foreach (var flag in manifest.Captures.SelectMany(c => c.Flags).GroupBy(f => f).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                consoleHost.WriteScrollable($"  flagged {flag.Key}: {flag.Count()}");
            }
            var problems = manifest.Captures.Count(c => c.Problem is not null);
            if (problems > 0)
            {
                consoleHost.WriteScrollable($"  unreadable: {problems} (see 'problem' in the manifest)");
            }
            consoleHost.WriteScrollable($"Wrote {output}");
            return 0;
        });
        return command;
    }

    public Command BuildCrop()
    {
        var sourceArg = new Argument<string>("source") { Description = "A SER capture, or a 7z archive whose SER members are cropped one at a time." };
        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = "The folder the crops and their sidecars go into, e.g. D:/Astro-Dataset/planetary/crops.",
            Required = true,
        };
        var widthOpt = new Option<int?>("--width") { Description = "The window's width; measured from the disk when absent." };
        var heightOpt = new Option<int?>("--height") { Description = "The window's height; measured from the disk when absent." };
        var marginOpt = new Option<int>("--margin")
        {
            Description = "Pixels kept around the disk's measured extent, on every side.",
            DefaultValueFactory = _ => 32,
        };
        var keepFreeOpt = KeepFreeOption();
        var sevenZipOpt = SevenZipOption();

        var command = new Command("planetary-crop",
            "Crop a planetary capture to a window tracked on its disk: every frame, the Bayer phase, the timestamps and each frame's window origin kept, verified pixel for pixel.")
        {
            Arguments = { sourceArg },
            Options = { outputOpt, widthOpt, heightOpt, marginOpt, keepFreeOpt, sevenZipOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.Required(sourceArg);
            if (!File.Exists(source))
            {
                consoleHost.WriteError($"No such file: {source}");
                return 1;
            }
            if (!ScratchSpace.TryParseSize(parseResult.GetValue(keepFreeOpt), out var keepFree))
            {
                consoleHost.WriteError($"--keep-free must be a size such as 109G; got '{parseResult.GetValue(keepFreeOpt)}'");
                return 1;
            }
            var output = Path.GetFullPath(parseResult.Required(outputOpt));
            var options = new CropOptions(parseResult.GetValue(widthOpt), parseResult.GetValue(heightOpt), parseResult.GetValue(marginOpt),
                KeepFreeBytes: keepFree);

            if (source.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                if (SevenZipTool.Find(parseResult.GetValue(sevenZipOpt)) is not { } sevenZip)
                {
                    consoleHost.WriteError("No 7-Zip found to unpack the archive with; pass --7z");
                    return 1;
                }
                var outcomes = await PlanetaryCrop.CropArchiveAsync(source, output, sevenZip, options, logger, ct);
                foreach (var (member, outcome) in outcomes)
                {
                    consoleHost.WriteScrollable($"{member}: {Describe(outcome)}");
                }
                return outcomes.All(o => o.Outcome.Result is not null || o.Outcome.Refusal is not null) ? 0 : 1;
            }

            var single = PlanetaryCrop.Crop(source, output, options, logger, ct);
            consoleHost.WriteScrollable(Describe(single));
            return single.Result is not null ? 0 : 1;
        });
        return command;
    }

    public Command BuildConvert()
    {
        var foldersArg = new Argument<string[]>("folders")
        {
            Description = "Folders of one FITS file a frame (a planetary video SharpCap saved as FITS); nothing in them is written.",
            Arity = ArgumentArity.OneOrMore,
        };
        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = "The folder the SERs and their sidecars go into, e.g. C:/temp/tianwen-scratch/planetary/converted.",
            Required = true,
        };
        var keepFreeOpt = KeepFreeOption();

        var command = new Command("planetary-convert",
            "Make a planetary video saved one FITS file a frame into one SER: the files' own samples, each frame's DATE-OBS in the trailer, verified against the files.")
        {
            Arguments = { foldersArg },
            Options = { outputOpt, keepFreeOpt },
        };

        command.SetAction((parseResult, ct) =>
        {
            var folders = parseResult.GetValue(foldersArg) ?? [];
            if (folders.FirstOrDefault(f => !Directory.Exists(f)) is { } missing)
            {
                consoleHost.WriteError($"No such folder: {missing}");
                return Task.FromResult(1);
            }
            if (!ScratchSpace.TryParseSize(parseResult.GetValue(keepFreeOpt), out var keepFree))
            {
                consoleHost.WriteError($"--keep-free must be a size such as 109G; got '{parseResult.GetValue(keepFreeOpt)}'");
                return Task.FromResult(1);
            }
            var output = Path.GetFullPath(parseResult.Required(outputOpt));
            var converted = 0;
            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = PlanetaryFitsVideo.Convert(folder, output, new FitsVideoOptions(keepFree), logger, ct);
                consoleHost.WriteScrollable(outcome.Result is { } result
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"{folder}: {result.Frames} frames of {result.Width}x{result.Height} {result.ColorId} {result.Depth} bit ({ScratchSpace.Size(result.OutputBytes)}), verified: {result.Output}")
                    : $"{folder}: not converted: {outcome.Refusal}");
                converted += outcome.Result is not null ? 1 : 0;
            }
            return Task.FromResult(converted == folders.Length ? 0 : 1);
        });
        return command;
    }

    private static string Describe(CropOutcome outcome) => outcome.Result is { } result
        ? string.Create(CultureInfo.InvariantCulture,
            $"{result.Frames} frames at {result.Width}x{result.Height} ({ScratchSpace.Size(result.OutputBytes)}), the disk found in {result.Located}, verified: {result.Output}")
        : $"not cropped: {outcome.Refusal}";
}
