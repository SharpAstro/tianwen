using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.IO;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen dataset relink</c> and <c>tianwen dataset prune</c>: the two passes that turn a raw
/// tree and a curated tree holding the same nights into one set of bytes.
///
/// <para>They are separate verbs because they are separate decisions. Relink chooses which copy of
/// a frame survives and is where every byte is reclaimed; prune only removes names that have
/// stopped meaning anything and frees nothing at all. Running the second without the first does
/// nothing; running the first without the second leaves a correct but redundant tree.</para>
///
/// <para><b>Both are a dry run unless <c>--apply</c> is passed</b>, and both report the same verdict
/// either way, so the dry run is the thing to read before authorising the real one.</para>
/// </summary>
internal sealed partial class DatasetSubCommand
{
    /// <summary>Progress cadence. The sweeps read whole files, so a line every this many frames is
    /// frequent enough to show it is alive over a run measured in hours.</summary>
    private const int ProgressEvery = 500;

    /// <summary>
    /// <c>tianwen dataset relink</c>: points each raw frame at the curated frame holding the same
    /// pixels, so the curated header survives and the archive stops paying for two copies.
    /// </summary>
    private Command BuildRelinkCommand()
    {
        var rawOpt = new Option<string>("--raw")
        {
            Description = "Raw tree whose frames become names for the curated ones. Its headers are the ones discarded.",
            Required = true,
        };
        var curatedOpt = new Option<string>("--curated")
        {
            Description = "Curated tree. Its files and headers survive; nothing under it is written.",
            Required = true,
        };
        var applyOpt = new Option<bool>("--apply")
        {
            Description = "Actually re-point. Omit for a dry run that reports what would change.",
        };
        var policyOpt = new Option<ArchiveLinkSweep.HeaderPolicy>("--header-policy")
        {
            Description = "curated-wins: link wherever the curated header is a superset of the raw one, which is " +
                          "the point of the sweep (default). require-identical: link only frames that are already " +
                          "byte for byte the same, for an archive whose curation is not trusted yet.",
            DefaultValueFactory = _ => ArchiveLinkSweep.HeaderPolicy.CuratedWins,
        };
        var outOpt = new Option<string?>("--out")
        {
            Description = "CSV of every frame considered and what happened to it. Written atomically at the end.",
        };
        var limitOpt = new Option<int>("--limit")
        {
            Description = "Stop after this many raw frames. 0 means all. Use it to try the sweep on a corner first.",
            DefaultValueFactory = _ => 0,
        };

        var command = new Command("relink",
            "Point raw frames at their curated twins, so one night stops costing two copies of itself.")
        {
            rawOpt, curatedOpt, applyOpt, policyOpt, outOpt, limitOpt,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var rawRoot = parseResult.Required(rawOpt);
            var curatedRoot = parseResult.Required(curatedOpt);
            if (!Directory.Exists(rawRoot))
            {
                consoleHost.WriteError($"Raw tree does not exist: {rawRoot}");
                return 1;
            }
            if (!Directory.Exists(curatedRoot))
            {
                consoleHost.WriteError($"Curated tree does not exist: {curatedRoot}");
                return 1;
            }
            // Compared as the file system names them. Given through a junction, a curated root and a
            // raw root that are really one tree would pass a string test, and every "already linked"
            // answer below is a test against REAL link names.
            rawRoot = ArchiveLinkSweep.CanonicalRoot(rawRoot);
            curatedRoot = ArchiveLinkSweep.CanonicalRoot(curatedRoot);
            // Pointing a tree at itself would re-point every frame at a sibling of its own and call
            // it a saving. Cheap to check, catastrophic to get wrong.
            if (Overlaps(rawRoot, curatedRoot))
            {
                consoleHost.WriteError("The raw and curated trees overlap. They must be separate trees.");
                return 1;
            }

            var apply = parseResult.GetValue(applyOpt);
            var policy = parseResult.GetValue(policyOpt);
            var limit = parseResult.GetValue(limitOpt);

            consoleHost.WriteScrollable($"[relink] indexing {curatedRoot} by file size");
            // Size first, digest later. A digest costs a whole file read, and most raw frames have no
            // curated twin at all, so the size index turns "read everything twice" into "read only
            // the frames that could possibly match". Size is necessary for identity here because a
            // matching data digest with a differing size is a different file shape, which the sweep
            // refuses anyway.
            var curatedBySize = new Dictionary<long, List<string>>();
            var curatedFiles = 0;
            foreach (var file in FitsFilesUnder(curatedRoot))
            {
                ct.ThrowIfCancellationRequested();
                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (!curatedBySize.TryGetValue(size, out var list))
                {
                    curatedBySize[size] = list = [];
                }
                list.Add(file);
                curatedFiles++;
            }
            consoleHost.WriteScrollable(
                $"[relink] {curatedFiles} curated frame(s) in {curatedBySize.Count} distinct size(s)");

            var digestCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string DigestOf(string path)
            {
                if (!digestCache.TryGetValue(path, out var digest))
                {
                    digestCache[path] = digest = TianWen.Lib.Imaging.Stacking.StackManifest.DigestData(path);
                }
                return digest;
            }

            consoleHost.WriteScrollable(
                $"[relink] {(apply ? "APPLYING" : "DRY RUN")}: {rawRoot} -> {curatedRoot}, header policy {policy}");

            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var rows = new List<string>();
            long released = 0;
            var seen = 0;
            foreach (var raw in FitsFilesUnder(rawRoot))
            {
                ct.ThrowIfCancellationRequested();
                if (limit > 0 && seen >= limit)
                {
                    break;
                }
                seen++;
                if (seen % ProgressEvery == 0)
                {
                    // The running tally, not just the total. Over the un-curated end of the archive
                    // every frame reports nothing, and "0.0 GB" alone cannot tell a cheap size miss
                    // from a frame that was fully read and turned out to hold different pixels. On a
                    // run measured in hours that is the difference between working and wedged.
                    var tally = string.Join(", ", counts.OrderByDescending(kv => kv.Value)
                        .Take(3).Select(kv => $"{kv.Value} {kv.Key}"));
                    consoleHost.WriteScrollable(
                        $"[relink] {seen} considered, {Gb(released)} GB " +
                        $"{(apply ? "released" : "releasable")}" + (tally.Length > 0 ? $"; {tally}" : ""));
                }

                // Answered from the directory entries, before any read. A full sweep is hours of
                // disk, so an interrupted run has to be restartable without paying for the part it
                // already finished.
                if (ArchiveLinkSweep.AlreadyLinkedInto(raw, curatedRoot))
                {
                    Count(counts, "already one file");
                    continue;
                }

                long rawSize;
                try
                {
                    rawSize = new FileInfo(raw).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Count(counts, "unreadable");
                    continue;
                }

                if (!curatedBySize.TryGetValue(rawSize, out var candidates))
                {
                    Count(counts, "no twin (no curated frame of that size)");
                    continue;
                }

                var rawDigest = DigestOf(raw);
                if (rawDigest.Length == 0)
                {
                    Count(counts, "unreadable");
                    continue;
                }

                var twin = candidates.FirstOrDefault(c => string.Equals(DigestOf(c), rawDigest, StringComparison.Ordinal));
                if (twin is null)
                {
                    Count(counts, "no twin (same size, different pixels)");
                    continue;
                }

                var result = await ArchiveLinkSweep.LinkAsync(raw, twin, policy, apply, ct);
                Count(counts, result.Outcome switch
                {
                    ArchiveLinkSweep.LinkOutcome.Linked => apply ? "linked" : "would link",
                    ArchiveLinkSweep.LinkOutcome.AlreadyOneFile => "already one file",
                    ArchiveLinkSweep.LinkOutcome.HeaderWouldLose => "REFUSED (the raw header states more)",
                    ArchiveLinkSweep.LinkOutcome.PayloadDiffers => "REFUSED (pixels differ)",
                    ArchiveLinkSweep.LinkOutcome.DifferentVolume => "REFUSED (different volume)",
                    ArchiveLinkSweep.LinkOutcome.Failed => "FAILED",
                    _ => "unreadable",
                });
                released += result.BytesReleased;
                rows.Add(string.Join(',', [
                    Csv(result.RawPath), Csv(result.CuratedPath), result.Outcome.ToString(),
                    result.BytesReleased.ToString(CultureInfo.InvariantCulture), Csv(result.Detail)]));
            }

            consoleHost.WriteScrollable(
                $"[relink] {seen} raw frame(s) considered, {Gb(released)} GB {(apply ? "released" : "releasable")}");
            foreach (var (key, n) in counts)
            {
                consoleHost.WriteScrollable($"    {n,7}  {key}");
            }
            if (!apply)
            {
                consoleHost.WriteScrollable("[relink] nothing was written. Re-run with --apply.");
            }

            WriteReport(parseResult.GetValue(outOpt), "raw,curated,outcome,bytes_released,detail", rows);
            return counts.Keys.Any(k => k.StartsWith("FAILED", StringComparison.Ordinal)) ? 1 : 0;
        });

        return command;
    }

    /// <summary>
    /// <c>tianwen dataset prune</c>: removes a folder once every byte under it has another name.
    /// </summary>
    private Command BuildPruneCommand()
    {
        var rootOpt = new Option<string>("--root")
        {
            Description = "Tree to consider. Folders under it are removed only where every file has a name outside.",
            Required = true,
        };
        var applyOpt = new Option<bool>("--apply")
        {
            Description = "Actually delete. Omit for a dry run that reports what would go.",
        };
        var outOpt = new Option<string?>("--out")
        {
            Description = "CSV of every folder considered and what happened to it. Written atomically at the end.",
        };

        var command = new Command("prune",
            "Remove raw folders that have become pure hard links, keeping every byte under another name.")
        {
            rootOpt, applyOpt, outOpt,
        };

        command.SetAction((parseResult, ct) =>
        {
            var root = parseResult.Required(rootOpt);
            if (!Directory.Exists(root))
            {
                consoleHost.WriteError($"Tree does not exist: {root}");
                return Task.FromResult(1);
            }
            var apply = parseResult.GetValue(applyOpt);

            consoleHost.WriteScrollable($"[prune] {(apply ? "APPLYING" : "DRY RUN")}: {root}");
            consoleHost.WriteScrollable(
                "[prune] a successful prune frees NO space: the condition for removing a name is that the " +
                "bytes keep another one. Space comes from relink.");

            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var rows = new List<string>();
            var pruned = 0;
            long wouldOrphan = 0;

            // Largest unit first. A whole tree that is pure indirection goes in one call; only a
            // MIXED tree is descended into, so a prunable pocket inside it is still found without
            // asking every leaf separately.
            void Consider(string folder, int depth)
            {
                ct.ThrowIfCancellationRequested();
                var verdict = ArchivePruneSweep.Consider(folder, apply, ct);
                rows.Add(string.Join(',', [
                    Csv(verdict.Folder), verdict.Outcome.ToString(),
                    verdict.Files.ToString(CultureInfo.InvariantCulture),
                    verdict.Orphans.ToString(CultureInfo.InvariantCulture),
                    verdict.OrphanBytes.ToString(CultureInfo.InvariantCulture), Csv(verdict.Detail)]));

                switch (verdict.Outcome)
                {
                    case ArchivePruneSweep.PruneOutcome.Pruned:
                        Count(counts, apply ? "pruned" : "would prune");
                        pruned++;
                        consoleHost.WriteScrollable(
                            $"    {(apply ? "pruned" : "would prune")} {verdict.Files,6} file(s)  {folder}");
                        return; // the whole subtree went with it

                    case ArchivePruneSweep.PruneOutcome.Empty:
                        Count(counts, "empty, left alone");
                        return;

                    case ArchivePruneSweep.PruneOutcome.WouldOrphan:
                        wouldOrphan += verdict.OrphanBytes;
                        Count(counts, "kept (holds bytes with no other name)");
                        break;

                    case ArchivePruneSweep.PruneOutcome.NotItsRealPath:
                        // Only the ROOT can land here (a child of a real path is real), so say how to
                        // fix it rather than just counting it.
                        Count(counts, "REFUSED (not its real path)");
                        consoleHost.WriteError($"[prune] {verdict.Folder}: {verdict.Detail}");
                        return;

                    default:
                        Count(counts, verdict.Outcome.ToString());
                        return;
                }

                // Mixed: look inside.
                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Count(counts, "unreadable");
                    return;
                }
                foreach (var child in children)
                {
                    Consider(child, depth + 1);
                }
            }

            Consider(root, 0);

            consoleHost.WriteScrollable(
                $"[prune] {pruned} folder(s) {(apply ? "removed" : "would be removed")}; " +
                $"{Gb(wouldOrphan)} GB is held only under this tree and keeps it.");
            foreach (var (key, n) in counts)
            {
                consoleHost.WriteScrollable($"    {n,7}  {key}");
            }
            if (!apply)
            {
                consoleHost.WriteScrollable("[prune] nothing was deleted. Re-run with --apply.");
            }

            WriteReport(parseResult.GetValue(outOpt), "folder,outcome,files,orphans,orphan_bytes,detail", rows);
            return Task.FromResult(0);
        });

        return command;
    }

    private static IEnumerable<string> FitsFilesUnder(string root)
        => FileEnumeration.EnumerateFiles(root, FitsFolderFrameSource.FitsExtensions, recursive: true)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

    private static void Count(SortedDictionary<string, int> counts, string key)
        => counts[key] = counts.GetValueOrDefault(key) + 1;

    private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>One CSV field, quoted because a path can hold a comma and several here do.</summary>
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    /// <summary>Written beside the target and renamed over it, so a half-written report never
    /// replaces a good one from an earlier pass.</summary>
    private void WriteReport(string? path, string header, IReadOnlyList<string> rows)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var temp = path + ".tmp";
        var text = new StringBuilder(header).Append('\n');
        foreach (var row in rows)
        {
            text.Append(row).Append('\n');
        }
        File.WriteAllText(temp, text.ToString());
        File.Move(temp, path, overwrite: true);
        consoleHost.WriteScrollable($"    report: {path} ({rows.Count} row(s))");
    }

    /// <summary>True when either path contains the other, compared as directories so that
    /// <c>/a/pics</c> and <c>/a/pics-curated</c> are correctly seen as separate.</summary>
    private static bool Overlaps(string a, string b)
    {
        var x = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var y = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase)
            || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }
}
