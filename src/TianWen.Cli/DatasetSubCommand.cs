using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.IO;
using System.Linq;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using TianWen.UI.Abstractions;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen dataset build</c> -- training-dataset builder (docs/plans/ai-denoise-deconv.md §2.4).
/// CLI contract: NO machine specifics; archive roots and the output dir are required parameters
/// with fail-fast validation; behavioural knobs carry portable defaults only.
/// </summary>
internal sealed class DatasetSubCommand(IConsoleHost consoleHost, ILogger<DatasetSubCommand>? logger = null)
{
    public Command Build()
    {
        var archiveRootOpt = new Option<string[]>("--archive-root")
        {
            Description = "Archive root scanned recursively for raw lights + calibration (repeatable; " +
                          "pass the canonical root first; it wins deduplication ties).",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var outOpt = new Option<string>("--out", "-o")
        {
            Description = "Output root for tiles/, manifest.jsonl, masters/ cache and stats/ (created if missing).",
            Required = true,
        };
        var minExposureOpt = new Option<double>("--min-exposure")
        {
            Description = "Minimum light exposure in seconds (shorter = planetary/lucky bursts, excluded).",
            DefaultValueFactory = _ => 10d,
        };
        var maxExposureOpt = new Option<double>("--max-exposure")
        {
            Description = "Maximum light exposure in seconds (longer = live-stack accumulations, excluded).",
            DefaultValueFactory = _ => 300d,
        };
        var excludeInstrumeOpt = new Option<string>("--exclude-instrume")
        {
            Description = "Case-insensitive wildcard on INSTRUME; matching frames are excluded " +
                          "(synthetic frames poison the noise model).",
            DefaultValueFactory = _ => "*simulator*",
        };
        var excludeObjectOpt = new Option<string>("--exclude-object")
        {
            Description = "Case-insensitive wildcard on OBJECT; matching lights are excluded " +
                          "(sessions are grouped by target, so e.g. '*vela*' drops one pointing " +
                          "cleanly even when it shares a dated LIGHT folder). Empty = no exclusion.",
            DefaultValueFactory = _ => "",
        };
        var excludePathOpt = new Option<string[]>("--exclude-path")
        {
            Description = "Case-insensitive wildcard(s) matched against each PATH SEGMENT; a frame " +
                          "under a matching directory is excluded (repeatable). Appended to the " +
                          "built-in processed-data exclusions. Use for deliberately-bad or " +
                          "processed folders, e.g. '*BAD LIGHT*'.",
            AllowMultipleArgumentsPerToken = true,
        };
        var minSubsOpt = new Option<int>("--min-subs")
        {
            Description = "Sessions with fewer gated lights are skipped.",
            DefaultValueFactory = _ => 10,
        };
        var tileSizeOpt = new Option<int>("--tile-size")
        {
            Description = "Tile edge length in pixels; must match the inference tiling contract.",
            DefaultValueFactory = _ => 256,
        };
        var cellsOpt = new Option<int>("--cells-per-session")
        {
            Description = "Upper bound of sampled grid cells per session (structure-biased).",
            DefaultValueFactory = _ => 300,
        };
        var subsPerCellOpt = new Option<int>("--subs-per-cell")
        {
            Description = "Sub tiles exported per sampled cell (any two form a Noise2Noise pair).",
            DefaultValueFactory = _ => 8,
        };
        var testFractionOpt = new Option<double>("--test-fraction")
        {
            Description = "Fraction of sessions held out as the pinned TEST split (by session, never by tile).",
            DefaultValueFactory = _ => 0.15d,
        };
        var requireDarkOpt = new Option<bool>("--require-dark")
        {
            Description = "Skip any session that resolves no master dark (instead of registering it " +
                          "uncalibrated). An uncalibrated N2N pair shares the sensor's fixed-pattern " +
                          "dark signal, so it is not a valid training sample; use this to drop e.g. a " +
                          "camera whose dark library is missing from the archive.",
        };
        var requireGainMatchOpt = new Option<bool>("--require-gain-match")
        {
            Description = "Reject a dark whose gain is known and differs from the lights (not just " +
                          "score-penalise it). The fixed-pattern amplitude a dark subtracts is gain-" +
                          "dependent, so a wrong-gain dark mis-scales it. Pairs with --require-dark to " +
                          "skip a session left with no same-gain dark. Unknown gain stays a wildcard; " +
                          "flats are unaffected.",
        };
        var softwareOpt = new Option<string>("--software")
        {
            Description = "Case-insensitive wildcard on SWCREATE; only LIGHTS authored by matching " +
                          "software are kept (e.g. '*N.I.N.A.*' to exclude SharpCap planetary/EAA " +
                          "captures). Applies to lights only; calibration frames resolve regardless " +
                          "of authoring tool. Empty = no filter.",
            DefaultValueFactory = _ => "",
        };
        var discoverOnlyOpt = new Option<bool>("--discover-only")
        {
            Description = "Stop after session discovery and print the inventory (no tiles written).",
        };
        var resumeOpt = new Option<bool>("--resume")
        {
            Description = "Continue a stopped run: keep the existing manifest as the checkpoint and " +
                          "skip every session already fully exported to it (the interrupted session " +
                          "re-runs cleanly). Use the SAME roots and gates as the stopped run.",
        };

        var buildCommand = new Command("build", "Build the training tile set from raw archive lights.")
        {
            Options =
            {
                archiveRootOpt, outOpt,
                minExposureOpt, maxExposureOpt, excludeInstrumeOpt, excludeObjectOpt, excludePathOpt, minSubsOpt,
                tileSizeOpt, cellsOpt, subsPerCellOpt, testFractionOpt, requireDarkOpt, requireGainMatchOpt, softwareOpt, discoverOnlyOpt, resumeOpt,
            },
        };
        buildCommand.SetAction(async (parseResult, ct) =>
        {
            var roots = parseResult.GetValue(archiveRootOpt)!;
            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                {
                    consoleHost.WriteError($"Archive root does not exist: {root}");
                    return 1;
                }
            }
            var outDir = parseResult.GetValue(outOpt)!;
            var minExposure = parseResult.GetValue(minExposureOpt);
            var maxExposure = parseResult.GetValue(maxExposureOpt);
            if (minExposure <= 0 || maxExposure <= minExposure)
            {
                consoleHost.WriteError($"Invalid exposure range [{minExposure}, {maxExposure}] s.");
                return 1;
            }

            var options = new DatasetBuildOptions
            {
                ArchiveRoots = [.. roots],
                OutputDir = outDir,
                MinExposure = TimeSpan.FromSeconds(minExposure),
                MaxExposure = TimeSpan.FromSeconds(maxExposure),
                ExcludeInstrumePattern = parseResult.GetValue(excludeInstrumeOpt)!,
                ExcludeObjectPattern = parseResult.GetValue(excludeObjectOpt)!,
                MinSubsPerSession = parseResult.GetValue(minSubsOpt),
                TileSize = parseResult.GetValue(tileSizeOpt),
                CellsPerSession = parseResult.GetValue(cellsOpt),
                SubsPerCell = parseResult.GetValue(subsPerCellOpt),
                TestFraction = parseResult.GetValue(testFractionOpt),
                RequireDarkCalibration = parseResult.GetValue(requireDarkOpt),
                RequireGainMatch = parseResult.GetValue(requireGainMatchOpt),
                SoftwareIncludePattern = parseResult.GetValue(softwareOpt)!,
                Resume = parseResult.GetValue(resumeOpt),
            };

            // User path exclusions append to the built-in processed-data defaults (never replace them).
            var extraExcludePaths = parseResult.GetValue(excludePathOpt);
            if (extraExcludePaths is { Length: > 0 })
            {
                options = options with { ExcludePathSegments = options.ExcludePathSegments.AddRange(extraExcludePaths) };
            }

            consoleHost.WriteScrollable($"[dataset] scanning {roots.Length} root(s) for raw lights ...");
            var (sessions, stats) = await SessionDiscovery.DiscoverAsync(options, logger, ct);

            consoleHost.WriteScrollable(
                $"[dataset] scanned {stats.Scanned} FITS: {stats.Sessions} sessions / {stats.Lights} lights kept; " +
                $"dropped {stats.NotLight} non-light, {stats.ExposureOutOfRange} exposure-out-of-range, " +
                $"{stats.InstrumentExcluded} excluded-instrument, {stats.SoftwareExcluded} excluded-software, " +
                $"{stats.ObjectExcluded} excluded-object, " +
                $"{stats.PathExcluded} excluded-path, " +
                $"{stats.ProductExcluded} products, {stats.Duplicates} duplicates, " +
                $"{stats.SessionsTooSmall} too-small sessions");

            // The filter census, printed before the per-session lines: it is what tells you whether
            // a night needs a sidecar (a large "(no FILTER header)" count) and whether one filter is
            // spelled two ways, both of which are invisible in the session list itself.
            var byFilter = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var session in sessions)
            {
                var key = session.FilterName.Length > 0 ? session.FilterName : "(no FILTER header)";
                byFilter[key] = byFilter.GetValueOrDefault(key) + session.Lights.Length;
            }
            if (byFilter.Count > 0)
            {
                consoleHost.WriteScrollable(
                    "[dataset] filters: " + string.Join(", ", byFilter.Select(kv => $"{kv.Key} x{kv.Value}")));
            }

            if (stats.Sidecar is { IsEmpty: false } sc)
            {
                consoleHost.WriteScrollable(
                    $"[dataset] {FrameMetaSidecarResolver.FileName}: {sc.Files} file(s), " +
                    $"filled {sc.FilterFilled} frame filter(s)" +
                    (sc.FilterAlreadyPresent > 0 ? $", {sc.FilterAlreadyPresent} frame(s) already had one" : "") +
                    (sc.Malformed > 0 ? $", {sc.Malformed} MALFORMED (ignored)" : ""));
            }

            foreach (var session in sessions)
            {
                var first = session.Lights[0].Meta;
                consoleHost.WriteScrollable(
                    $"[dataset]   {session.Id}: {session.Lights.Length} lights, " +
                    $"{first.ExposureDuration.TotalSeconds:0}s g{first.Gain}");
            }

            if (parseResult.GetValue(discoverOnlyOpt))
            {
                return 0;
            }

            // Full build: scan -> sessions + calibration groups -> pinned split -> per session
            // (resolve calibrator -> register -> export tiles) -> parity gate -> PSF/noise report.
            var progress = new Progress<string>(s => consoleHost.WriteScrollable(s));
            var result = await DatasetBuildRunner.RunAsync(options, logger, progress, ct);

            consoleHost.WriteScrollable(
                $"[dataset] {result.Registered}/{result.Sessions} sessions" +
                $"{(result.Resumed > 0 ? $" (+{result.Resumed} resumed)" : "")} -> {result.TotalTiles} tiles" +
                $"{(result.Failed > 0 ? $" ({result.Failed} FAILED, see log)" : "")}" +
                $"{(result.SkippedNoDark > 0 ? $" ({result.SkippedNoDark} skipped: no dark calibration)" : "")}; " +
                $"{result.TestSessions} test sessions held out; " +
                $"parity {(result.ParityChecked ? result.ParityMaxDiff == 0d ? "OK" : $"DIFF {result.ParityMaxDiff}" : "n/a")}");
            consoleHost.WriteScrollable($"[dataset] manifest: {result.ManifestPath}");
            consoleHost.WriteScrollable($"[dataset] split:    {result.SplitPath}");
            consoleHost.WriteScrollable($"[dataset] report:   {result.ReportPath}");

            // A non-zero parity diff means the stored tiles no longer equal the C# stretch of their
            // source -- train/inference skew. Fail the command so CI catches it.
            return result.ParityChecked && result.ParityMaxDiff != 0d ? 1 : 0;
        });

        return new Command("dataset", "Training-dataset tooling (see docs/plans/ai-denoise-deconv.md).")
        {
            Subcommands = { buildCommand, BuildTagFilterCommand() },
        };
    }

    /// <summary>
    /// <c>tianwen dataset tag-filter</c> writes a FILTER card into frames whose capture software
    /// never recorded one (a hand-fitted filter: N.I.N.A. only models a motorised wheel).
    ///
    /// <para>The alternative is <c>.tianwen-meta.json</c>, which declares the same thing and writes
    /// nothing. This exists for when you want the fact in the frames themselves, where every other
    /// tool can see it. It edits the primary header surgically and copies every other byte verbatim
    /// (see <see cref="FitsHeaderEditor"/>), and it is a DRY RUN unless <c>--apply</c> is passed.</para>
    ///
    /// <para>A de-duplicated archive files some nights twice, as hard links to one frame, and those
    /// are refused by default. <c>--hard-links relink</c> brings the other names along; because a
    /// shared frame has no per-name header, that necessarily changes files outside <c>--path</c>, so
    /// the summary lists them and the dry run lists them first.</para>
    /// </summary>
    private Command BuildTagFilterCommand()
    {
        var pathOpt = new Option<string>("--path")
        {
            Description = "Directory holding the frames to tag (see --recursive).",
            Required = true,
        };
        var filterOpt = new Option<string>("--filter")
        {
            Description = "Filter name to write, exactly as the capture software would have (e.g. \"Optolong L-Ultimate 3nm\").",
            Required = true,
        };
        var recursiveOpt = new Option<bool>("--recursive") { Description = "Descend into subdirectories.", DefaultValueFactory = _ => true };
        var applyOpt = new Option<bool>("--apply") { Description = "Actually write. Omit for a dry run that reports what would change." };
        var overwriteOpt = new Option<bool>("--overwrite-existing")
        {
            Description = "Also replace a FILTER card that already has a value. Off by default: filling in what " +
                          "was never recorded is a different and far safer act than relabelling a frame that stated its own.",
        };
        var frameTypesOpt = new Option<string[]>("--frame-type")
        {
            Description = "IMAGETYP values to tag. Defaults to Light+Flat+DarkFlat, the types a filter is " +
                          "meaningful for, so bad-pixel maps and master darks sitting in the same folder are left alone.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => ["Light", "Flat", "DarkFlat"],
        };
        var hardLinksOpt = new Option<FitsHeaderEditor.HardLinkPolicy>("--hard-links")
        {
            Description = "What to do with a frame that other paths also point at (a de-duplicated archive files " +
                          "one night twice). refuse: skip it and report where the other names are (default). " +
                          "relink: amend one name, then re-point the others at the amended frame, so all the names " +
                          "keep sharing one physical file. diverge: amend this name only and leave the others on " +
                          "the old header.",
            DefaultValueFactory = _ => FitsHeaderEditor.HardLinkPolicy.Refuse,
        };

        var command = new Command("tag-filter", "Write a FILTER card into frames that never recorded one (header-surgical; dry run by default).")
        {
            pathOpt, filterOpt, recursiveOpt, applyOpt, overwriteOpt, frameTypesOpt, hardLinksOpt,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var path = parseResult.GetValue(pathOpt)!;
            if (!Directory.Exists(path))
            {
                consoleHost.WriteError($"Directory does not exist: {path}");
                return 1;
            }
            var filterName = parseResult.GetValue(filterOpt)!;
            var apply = parseResult.GetValue(applyOpt);
            var overwrite = parseResult.GetValue(overwriteOpt);
            var hardLinks = parseResult.GetValue(hardLinksOpt);

            var allowed = new HashSet<FrameType>();
            foreach (var name in parseResult.GetValue(frameTypesOpt)!)
            {
                if (FrameType.FromFITSValue(name) is { } ft)
                {
                    allowed.Add(ft);
                }
                else
                {
                    consoleHost.WriteError($"Unrecognised frame type: {name}");
                    return 1;
                }
            }

            var option = parseResult.GetValue(recursiveOpt) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.EnumerateFiles(path, "*.*", option)
                .Where(p => FitsFolderFrameSource.FitsExtensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            consoleHost.WriteScrollable(
                $"[tag-filter] {(apply ? "APPLYING" : "DRY RUN")}: FILTER='{filterName}' over {files.Length} FITS file(s) under {path}");

            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var failures = 0;
            var refused = 0;
            // Every other name a relink reaches, so the summary can say what was touched beyond the
            // files that were actually walked. A shared frame cannot be tagged under one name only,
            // so this list is the honest scope of the command and not a footnote.
            var reached = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = await FitsHeaderEditor.SetStringCardAsync(
                        file, "FILTER", filterName, "Filter name", allowed,
                        overwriteExisting: overwrite, hardLinks: hardLinks, apply: apply,
                        cancellationToken: ct);
                    var key = result.Outcome switch
                    {
                        FitsHeaderEditor.TagOutcome.Tagged => "tagged",
                        FitsHeaderEditor.TagOutcome.TaggedAndRelinked =>
                            $"tagged + {(apply ? "re-pointed" : "would re-point")} its other names",
                        FitsHeaderEditor.TagOutcome.AlreadyPresent => $"skipped (already has {result.ExistingValue})",
                        FitsHeaderEditor.TagOutcome.FrameTypeExcluded => $"skipped ({result.Detail})",
                        FitsHeaderEditor.TagOutcome.MultiplyLinked => "SKIPPED (hard-linked, see below)",
                        _ => $"UNREADABLE ({result.Detail})",
                    };
                    if (result.Outcome is FitsHeaderEditor.TagOutcome.MultiplyLinked)
                    {
                        refused++;
                    }
                    if (result.Outcome is FitsHeaderEditor.TagOutcome.TaggedAndRelinked)
                    {
                        foreach (var other in result.OtherLinks)
                        {
                            reached.Add(other);
                        }
                    }
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Report and continue: one locked or bad file must not abandon the rest, and the
                    // editor guarantees it left that original untouched.
                    failures++;
                    consoleHost.WriteError($"[tag-filter] FAILED {file}: {ex.Message}");
                }
            }

            foreach (var (key, count) in counts)
            {
                consoleHost.WriteScrollable($"[tag-filter]   {key}: {count}");
            }
            if (refused > 0)
            {
                consoleHost.WriteScrollable(
                    $"[tag-filter] {refused} file(s) are hard-linked, so another path holds the same frame and would " +
                    "keep the untagged header. That is how one night ends up filed twice and grouped as two " +
                    "different sessions. Pass --hard-links relink to bring the other names along (they name the " +
                    "same frame, so the same filter applies to all of them), or declare the filter with a " +
                    ".tianwen-meta.json sidecar instead, which writes nothing and breaks no links.");
            }
            // Names already in the walked set are reported by their own outcome line, so what is left
            // is the honest answer to "what did this change that I did not point it at". Subtracting
            // them also makes a dry run and an --apply agree: without it the dry run counts a swept
            // sibling that the real run finds already tagged, and the two totals disagree for no
            // reason a reader could work out.
            reached.ExceptWith(files.Select(Path.GetFullPath));
            if (reached.Count > 0)
            {
                // Worth its own paragraph rather than a footnote: the command was given one directory
                // and this is what it touches beyond it. Unavoidable, since a shared frame has no
                // per-name header to differ in, but never something to discover afterwards.
                consoleHost.WriteScrollable(
                    $"[tag-filter] {reached.Count} further file(s) are OTHER NAMES for the same frames, outside the " +
                    $"{files.Length} walked, and are amended with them (a shared frame cannot carry two headers):");
                foreach (var other in reached.Take(20))
                {
                    consoleHost.WriteScrollable($"[tag-filter]     {other}");
                }
                if (reached.Count > 20)
                {
                    consoleHost.WriteScrollable($"[tag-filter]     ... and {reached.Count - 20} more");
                }
            }
            if (!apply)
            {
                consoleHost.WriteScrollable("[tag-filter] nothing was written; re-run with --apply to commit.");
            }
            return failures > 0 ? 1 : 0;
        });

        return command;
    }
}
