// The 2026-10-01 header merge (the owner's call: "write a throwaway program to fix this once and for all").
//
//   headers [--apply] [--organized D:/Astro-Organized] [--log <csv>]
//   rekey --store <dir> --before <coverage.tsv> --after <coverage.tsv> [--apply]
//
// Every light, flat and dark-flat in Astro-Organized gets the FILTER its curated folder names, and every
// light the site cards of the site it was taken from, where the card is missing or a placeholder. One
// rewrite per frame (FitsHeaderEditor.SetCardsAsync); hard-linked names are amended together
// (HardLinkPolicy.Relink: one frame, one best header, data identical). A frame that states a different
// specific value is left alone and reported. Dry run unless --apply.
using System.Globalization;
using System.Text;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.IO;

var apply = args.Contains("--apply");
switch (args.FirstOrDefault())
{
    case "headers":
        return await HeaderPass.RunAsync(Arg("--organized") ?? "D:/Astro-Organized", apply,
            Arg("--log") ?? $"C:/temp/e2/header-merge-{(apply ? "apply" : "dry")}.csv");
    case "rekey" when Arg("--store") is { } store && Arg("--before") is { } before && Arg("--after") is { } after:
        return await Rekey.RunAsync(store, before, after, apply);
    default:
        Console.Error.WriteLine("usage: headers [--apply] [--organized D:/Astro-Organized] [--log <csv>]");
        Console.Error.WriteLine("       rekey --store <dir> --before <coverage.tsv> --after <coverage.tsv> [--apply]");
        return 2;
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static class HeaderPass
{
    /// <summary>The FILTER each curated folder names: the spelling the archive already uses where it has
    /// one, the owner-approved name otherwise (2026-10-01); a provisional slug is written verbatim so it
    /// reads as provisional.</summary>
    static readonly Dictionary<string, string> FilterBySlug = new(StringComparer.Ordinal)
    {
        ["IDAS-LPS-D3"] = "IDAS LPS D3",
        ["Luminance"] = "Luminance",
        ["Optolong-L-Quad-Enhance"] = "Optolong L-Quad Enhance",
        ["Optolong-L-Ultimate-3nm"] = "Optolong L-Ultimate 3nm",
        ["Optolong-L-eNhance"] = "Optolong L-eNhance",
        ["UV-IR-Cut"] = "UV/IR Cut",
        ["Baader-Semi-APO"] = "Baader Semi-APO",
        ["Ha"] = "Ha",
        ["SII"] = "SII",
        ["Unidentified-Broadband"] = "Unidentified-Broadband",
        ["Unidentified-Broadband-BlueCut"] = "Unidentified-Broadband-BlueCut",
        ["Unidentified-HaOIII"] = "Unidentified-HaOIII",
    };

    /// <summary>Values that state no filter (digest-and-header-merge.md Rule 4: a specific value beats a
    /// placeholder). A processing mode (RGB, LUM, all channels) is not a filter either.</summary>
    static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "None", "NoFilter", "No Filter", "Unknown", "VALID", "RGB", "LUM", "all channels", "N/A", "-",
    };

    /// <summary>The two sites (the owner, 2026-10-01): before July 2023 the first, from then the second.</summary>
    static (double Lat, double Lon, double Elev) SiteFor(string night) => string.CompareOrdinal(night, "2023-07") < 0
        ? (-37.884639, 145.166601, 120)
        : (-37.8769444, 145.1775, 77);

    static readonly HashSet<FrameType> LightTypes = [FrameType.Light];
    static readonly HashSet<FrameType> FlatTypes = [FrameType.Flat, FrameType.DarkFlat];

    public static async Task<int> RunAsync(string organized, bool apply, string logPath)
    {
        var jobs = new List<(string Kind, string Slug, string Night, string Folder)>();
        foreach (var cam in Directory.EnumerateDirectories(Path.Combine(organized, "lights")))
        foreach (var slug in Directory.EnumerateDirectories(cam))
        foreach (var target in Directory.EnumerateDirectories(slug))
        foreach (var night in Directory.EnumerateDirectories(target))
        {
            jobs.Add(("light", Path.GetFileName(slug), Path.GetFileName(night), night));
        }
        foreach (var cam in Directory.EnumerateDirectories(Path.Combine(organized, "flats")))
        foreach (var slug in Directory.EnumerateDirectories(cam))
        foreach (var night in Directory.EnumerateDirectories(slug))
        foreach (var sub in Directory.EnumerateDirectories(night))
        {
            jobs.Add((Path.GetFileName(sub).ToLowerInvariant(), Path.GetFileName(slug), Path.GetFileName(night), sub));
        }

        var unknownSlugs = jobs.Select(j => j.Slug).Where(s => !FilterBySlug.ContainsKey(s)).Distinct().ToList();
        if (unknownSlugs.Count > 0)
        {
            Console.Error.WriteLine($"no FILTER name for: {string.Join(", ", unknownSlugs)}; refusing to run");
            return 3;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? ".");
        await using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false));
        await log.WriteLineAsync("kind\tslug\tnight\tfile\toutcome\tdetail\tother_links");
        var outcomes = new Dictionary<(string Kind, string Outcome), int>();
        var otherNames = 0;
        var conflicts = new List<string>();
        var files = 0;
        var started = DateTime.Now;

        foreach (var (kind, slug, night, folder) in jobs)
        {
            var filter = FilterBySlug[slug];
            var edits = EditsFor(kind, slug, night);
            var allowed = kind == "light" ? LightTypes : FlatTypes;

            foreach (var path in FileEnumeration.EnumerateFiles(folder, ".fits", recursive: true).Order(StringComparer.OrdinalIgnoreCase))
            {
                files++;
                var result = await FitsHeaderEditor.SetCardsAsync(path, edits, allowed, FitsHeaderEditor.HardLinkPolicy.Relink, apply);
                var key = (kind, result.Outcome.ToString());
                outcomes[key] = outcomes.GetValueOrDefault(key) + 1;
                otherNames += result.OtherLinks.Length;
                // A FILTER kept because it states a specific value that is NOT the folder's: a conflict.
                if (result.Detail.Split(", ").FirstOrDefault(d => d.StartsWith("FILTER=", StringComparison.Ordinal)) is { } kept
                    && Normalise(kept["FILTER=".Length..]) != Normalise(filter))
                {
                    conflicts.Add($"{path}: {kept} in a {slug} folder");
                }
                await log.WriteLineAsync(string.Join('\t', kind, slug, night, path, result.Outcome, result.Detail.Replace('\t', ' '), result.OtherLinks.Length));
                if (files % 1000 == 0)
                {
                    Console.WriteLine($"[header-merge] {files} frames, {(DateTime.Now - started).TotalMinutes:F1} min");
                }
            }
        }

        Console.WriteLine($"[header-merge] {(apply ? "APPLIED" : "DRY RUN")}: {files} frames in {jobs.Count} folders, {(DateTime.Now - started).TotalMinutes:F1} min; log {logPath}");
        foreach (var ((kind, outcome), n) in outcomes.OrderBy(kv => kv.Key.Kind).ThenBy(kv => kv.Key.Outcome))
        {
            Console.WriteLine($"  {kind,-9} {outcome,-20} {n,7}");
        }
        Console.WriteLine($"  other names amended with them (Pics / Unsorted twins): {otherNames}");
        Console.WriteLine($"  conflicts (a specific FILTER kept that is not the folder's): {conflicts.Count}");
        foreach (var c in conflicts.Take(40))
        {
            Console.WriteLine($"    {c}");
        }
        return conflicts.Count == 0 ? 0 : 1;
    }

    /// <summary>The cards a frame of this kind, curated folder and night should carry: the folder's FILTER,
    /// and for a light (or a master integrated from lights) the site it was taken from.</summary>
    public static List<FitsHeaderEditor.CardEdit> EditsFor(string kind, string slug, string night)
    {
        List<FitsHeaderEditor.CardEdit> edits = [FitsHeaderEditor.StringCard("FILTER", FilterBySlug[slug], "Filter name", Placeholders)];
        if (kind == "light")
        {
            var (lat, lon, elev) = SiteFor(night);
            edits.Add(FitsHeaderEditor.NumericCard("SITELAT", lat, "degrees"));
            edits.Add(FitsHeaderEditor.NumericCard("SITELONG", lon, "degrees"));
            edits.Add(FitsHeaderEditor.NumericCard("SITEELEV", elev, "metres above mean sea level"));
        }
        return edits;
    }

    static string Normalise(string v) => new string(v.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
