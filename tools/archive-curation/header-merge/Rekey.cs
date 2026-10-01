using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Phase 2: the dataset store, worked backwards. A session the bake calibrated with the pick the resolver
/// made then, and whose pick after the header merge resolves to the same frames, keeps its outputs under its
/// new id; the rest are left for a --resume bake to rebuild. See Program.cs for the call.
/// </summary>
static class Rekey
{
    /// <summary>The coverage columns that say WHICH frames a pick is, never its slug (a slug names the filter
    /// and the train, which the merge has just written).</summary>
    static readonly string[] IdentityColumns =
    [
        "flat_found", "flat_epoch", "flat_frames", "flat_gain", "flat_offset", "flat_temp_c",
        "pedestal_kind", "pedestal_frames", "pedestal_exposure_s", "pedestal_gain", "pedestal_offset",
        "dark_found", "dark_epoch", "dark_frames", "dark_exposure_s", "dark_gain", "dark_offset", "dark_temp_c",
        "dark_bias_found", "dark_bias_gain", "dark_bias_offset",
    ];

    public static async Task<int> RunAsync(string store, string beforeTsv, string afterTsv, bool apply)
    {
        var stats = Path.Combine(store, "stats");
        var ledgerPath = Path.Combine(stats, DatasetSessionLedger.FileName);
        var psfPath = Path.Combine(stats, DatasetPsfStore.FileName);
        var gradientPath = Path.Combine(stats, DatasetGradientReport.StoreFileName);
        var manifestPath = Path.Combine(store, "tiles-manifest.jsonl");
        var mastersDir = Path.GetDirectoryName(RetainedMasterStore.PathFor(store, "x")) ?? throw new InvalidOperationException("no masters dir");

        var ledger = await DatasetSessionLedger.ReadAsync(ledgerPath);
        var psf = await DatasetPsfStore.ReadAsync(psfPath);
        var before = ReadTsv(beforeTsv);
        var after = ReadTsv(afterTsv);
        // The night id the merge gave each night id the bake knew: itself, or the after-row whose id
        // without its new filter component is it.
        var afterByLegacy = after.Keys.GroupBy(id => DatasetSplitWriter.WithoutFilter(id)).ToDictionary(g => g.Key, g => g.ToList());

        var plan = new List<(string Old, string New, string Action, string Why)>();
        foreach (var (oldId, record) in psf.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var oldNight = DatasetSplitWriter.GroupIdOf(oldId);
            var flip = oldId.Length > oldNight.Length ? oldId[oldNight.Length..] : "";
            string? newNight = after.ContainsKey(oldNight) ? oldNight
                : afterByLegacy.TryGetValue(oldNight, out var cands) && cands.Count == 1 ? cands[0] : null;
            if (newNight is null || !before.TryGetValue(oldNight, out var b))
            {
                plan.Add((oldId, oldId, "skip", newNight is null ? "no session after the merge (or ambiguous)" : "no row before the merge"));
                continue;
            }
            var a = after[newNight];
            var newId = newNight + flip;
            var used = record.Calibration;
            var usedAsPicked = (used?.Flat ?? "") == b["flat_slug"] && (used?.Dark ?? "") == b["dark_slug"];
            var changed = IdentityColumns.Where(c => b[c] != a[c]).ToList();
            if (!usedAsPicked)
            {
                plan.Add((oldId, newId, "rebake", $"baked with flat {used?.Flat ?? "none"} / dark {used?.Dark ?? "none"}, the pick then was {b["flat_slug"]} / {b["dark_slug"]}"));
            }
            else if (changed.Count > 0)
            {
                plan.Add((oldId, newId, "rebake", "calibration resolves differently after the merge: " + string.Join(", ", changed.Select(c => $"{c} {b[c]} -> {a[c]}"))));
            }
            else
            {
                plan.Add((oldId, newId, oldId == newId ? "adopt" : "adopt-renamed", ""));
            }
        }

        foreach (var group in plan.GroupBy(p => p.Action).OrderBy(g => g.Key))
        {
            Console.WriteLine($"[rekey] {group.Key}: {group.Count()}");
        }
        foreach (var p in plan.Where(p => p.Action is "rebake" or "skip"))
        {
            Console.WriteLine($"  {p.Action,-7} {p.Old}\n          {p.Why}");
        }

        var renames = plan.Where(p => p.Action == "adopt-renamed").ToList();
        var orphaned = plan.Where(p => p.Action == "rebake" && p.Old != p.New).ToList();
        var adopted = plan.Where(p => p.Action.StartsWith("adopt", StringComparison.Ordinal)).ToList();
        Console.WriteLine($"[rekey] renamed and kept: {renames.Count}; renamed and re-baked (old outputs removed): {orphaned.Count}; ledger entries dropped for adoption: {adopted.Count}");
        if (!apply)
        {
            Console.WriteLine("[rekey] DRY RUN: nothing written; re-run with --apply.");
            return 0;
        }

        // The split the bake made, set aside BEFORE the ledger loses its entries: the next bake reads it
        // with --split-from, so no session changes sets.
        var priorDir = Path.Combine(stats, "split-prior");
        Directory.CreateDirectory(Path.Combine(priorDir, "stats"));
        File.Copy(ledgerPath, Path.Combine(priorDir, "stats", DatasetSessionLedger.FileName), overwrite: true);
        File.Copy(Path.Combine(store, DatasetSplitWriter.TestSessionsFileName), Path.Combine(priorDir, DatasetSplitWriter.TestSessionsFileName), overwrite: true);

        var stem = (string id) => Path.GetFileNameWithoutExtension(RetainedMasterStore.PathFor(store, id));
        var tileMoves = new Dictionary<string, (string NewId, string OldRel, string NewRel)>(StringComparer.Ordinal);
        foreach (var (oldId, newId, _, _) in renames)
        {
            var oldRel = ledger.TryGetValue(oldId, out var e) && e.TileDirRelative.Length > 0 ? e.TileDirRelative : "tiles/" + stem(oldId);
            var newRel = "tiles/" + stem(newId);
            var from = Path.Combine(store, oldRel.Replace('/', Path.DirectorySeparatorChar));
            var to = Path.Combine(store, newRel.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(from) && !Directory.Exists(to))
            {
                Directory.Move(from, to);
            }
            tileMoves[oldId] = (newId, oldRel, newRel);
            RenameMasterFiles(mastersDir, stem(oldId), stem(newId));
        }
        foreach (var (oldId, _, _, _) in orphaned)
        {
            var rel = ledger.TryGetValue(oldId, out var e) && e.TileDirRelative.Length > 0 ? e.TileDirRelative : "tiles/" + stem(oldId);
            var dir = Path.Combine(store, rel.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
            foreach (var f in Directory.EnumerateFiles(mastersDir, stem(oldId) + ".*"))
            {
                File.Delete(f);
            }
        }

        var orphanIds = orphaned.Select(o => o.Old).ToHashSet(StringComparer.Ordinal);
        var rewritten = await RewriteManifestAsync(manifestPath, tileMoves, orphanIds);
        Console.WriteLine($"[rekey] manifest: {rewritten.Renamed} rows renamed, {rewritten.Removed} rows removed");

        // The PSF and G1 records under the new ids, the PSF's calibration names as they read now.
        var gradient = await DatasetGradientStore.ReadAsync(gradientPath);
        foreach (var (oldId, newId, _, _) in adopted)
        {
            var a = after[DatasetSplitWriter.GroupIdOf(newId)];
            var calibration = (psf[oldId].Calibration ?? new CalibrationProvenance()) with
            {
                Dark = NullIfEmpty(a["dark_slug"]),
                Flat = NullIfEmpty(a["flat_slug"]),
                DarkBias = NullIfEmpty(a["dark_bias_slug"]),
            };
            await DatasetPsfStore.AppendAsync(psfPath, psf[oldId] with { SessionId = newId, Calibration = calibration });
            if (gradient.TryGetValue(stem(oldId) + ".fits", out var g))
            {
                await DatasetGradientStore.AppendAsync(gradientPath, g with { Master = stem(newId) + ".fits", Filter = a["filter"] });
            }
            await PatchMasterHeaderAsync(RetainedMasterStore.PathFor(store, newId), newId);
        }

        // Drop every adopted session's entry (old and new id) and every orphan's, so --resume adopts the
        // first through its PSF record and builds the second fresh.
        var drop = adopted.SelectMany(p => new[] { p.Old, p.New }).Concat(orphanIds).ToHashSet(StringComparer.Ordinal);
        var kept = await RewriteJsonLinesAsync(ledgerPath, node => !drop.Contains(node["SessionId"]?.GetValue<string>() ?? ""));
        Console.WriteLine($"[rekey] ledger: {kept} entries kept, {drop.Count} ids dropped");

        await File.WriteAllLinesAsync(Path.Combine(stats, "session-renames.jsonl"),
            plan.Select(p => JsonSerializer.Serialize(new Dictionary<string, string> { ["Old"] = p.Old, ["New"] = p.New, ["Action"] = p.Action, ["Why"] = p.Why })));
        Console.WriteLine($"[rekey] APPLIED. Bake next with --resume --split-from {priorDir}");
        return 0;
    }

    static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    /// <summary>A master's file and every sidecar beside it (<c>stem.coverage.fits.gz</c> and so on), renamed.</summary>
    static void RenameMasterFiles(string dir, string oldStem, string newStem)
    {
        foreach (var f in Directory.EnumerateFiles(dir, oldStem + ".*").ToList())
        {
            var name = Path.GetFileName(f);
            File.Move(f, Path.Combine(dir, newStem + name[oldStem.Length..]));
        }
    }

    /// <summary>The master's own header carries the session's FILTER and site, as its lights now do: the
    /// master's reference light was read before the merge.</summary>
    static async Task PatchMasterHeaderAsync(string masterPath, string sessionId)
    {
        var dir = sessionId.Split('|')[0].Split('/');
        if (dir.Length != 4 || !File.Exists(masterPath))
        {
            return;
        }
        var edits = HeaderPass.EditsFor("light", dir[1], dir[3]);
        var result = await FitsHeaderEditor.SetCardsAsync(masterPath, edits, allowedFrameTypes: null, FitsHeaderEditor.HardLinkPolicy.Refuse, apply: true);
        if (result.Outcome is not (FitsHeaderEditor.TagOutcome.Tagged or FitsHeaderEditor.TagOutcome.AlreadyPresent))
        {
            Console.WriteLine($"  master header not patched: {masterPath}: {result.Outcome} {result.Detail}");
        }
    }

    static async Task<(int Renamed, int Removed)> RewriteManifestAsync(
        string path, Dictionary<string, (string NewId, string OldRel, string NewRel)> moves, HashSet<string> remove)
    {
        int renamed = 0, removed = 0;
        var staging = path + ".rekey";
        await using (var writer = new StreamWriter(staging, append: false, new UTF8Encoding(false)))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0)
                {
                    continue;
                }
                var node = JsonNode.Parse(line) ?? throw new InvalidDataException("blank manifest row");
                var id = node["SessionId"]?.GetValue<string>() ?? "";
                if (remove.Contains(id))
                {
                    removed++;
                    continue;
                }
                if (moves.TryGetValue(id, out var m))
                {
                    node["SessionId"] = m.NewId;
                    foreach (var key in new[] { "Tile", "SigmaTile" })
                    {
                        if (node[key]?.GetValue<string>() is { } p && p.StartsWith(m.OldRel + "/", StringComparison.Ordinal))
                        {
                            node[key] = m.NewRel + p[m.OldRel.Length..];
                        }
                    }
                    renamed++;
                    await writer.WriteLineAsync(node.ToJsonString());
                    continue;
                }
                await writer.WriteLineAsync(line);
            }
        }
        File.Move(path, path + ".before-rekey", overwrite: true);
        File.Move(staging, path);
        return (renamed, removed);
    }

    static async Task<int> RewriteJsonLinesAsync(string path, Func<JsonNode, bool> keep)
    {
        var kept = new List<string>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (line.Length > 0 && JsonNode.Parse(line) is { } node && keep(node))
            {
                kept.Add(line);
            }
        }
        File.Copy(path, path + ".before-rekey", overwrite: true);
        var staging = path + ".rekey";
        await File.WriteAllLinesAsync(staging, kept);
        File.Move(staging, path, overwrite: true);
        return kept.Count;
    }

    static Dictionary<string, Dictionary<string, string>> ReadTsv(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = lines[0].TrimStart('﻿').Split('\t');
        var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            var cells = line.Split('\t');
            var row = header.Select((h, i) => (h, v: i < cells.Length ? cells[i] : "")).ToDictionary(t => t.h, t => t.v);
            rows[row["session_id"]] = row;
        }
        return rows;
    }
}
