using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;

namespace BakeComets;

/// <summary>
/// Bakes the two comet assets the browser build serves same-origin. See the csproj header for what and
/// the pages.yml step for when; this file carries the WHY of the incremental scheme.
/// </summary>
internal static class Program
{
    /// <summary>
    /// How long a baked entry stays good, by how bright the comet can ever get -- its peak-ish total
    /// magnitude <c>M1 + K1*log10(q)</c>, which is the same static candidacy gate the planner already
    /// uses, rather than a threshold invented here.
    ///
    /// <para><b>This is a relevance budget, not physics.</b> Osculating elements decay with TIME, so a
    /// magnitude-19 comet's set goes stale at exactly the same rate as 45P's. What brightness changes is
    /// how much along-track error is worth a request: nobody is pointing a telescope at the faint end,
    /// and a comet that is never drawn and never proposed does not need arcsecond placement. Do not read
    /// the tiers as a claim about accuracy.</para>
    ///
    /// <para>Measured against the live SBDB set (4,071 comets, 1,764 periodic): 518 are stale enough to
    /// warrant an upgrade at all, of which 16 fall in the daily tier, 63 in the weekly, 209 in the
    /// monthly and 230 in fetch-once. Steady state is about 30 requests on a day that deploys, against
    /// 518 for a bake that re-resolved everything.</para>
    /// </summary>
    private static TimeSpan RefreshIntervalFor(double peakMagnitude) => peakMagnitude switch
    {
        <= 12.0 => TimeSpan.FromDays(1),
        <= 15.0 => TimeSpan.FromDays(7),
        <= 18.0 => TimeSpan.FromDays(30),
        // Fainter than 18, or no photometric model at all (NaN fails every comparison above and lands
        // here): resolve once if it is missing and then leave it alone forever.
        _ => TimeSpan.MaxValue,
    };

    private static async Task<int> Main(string[] args)
    {
        string? outDir = null, seed = null;
        var maxFetches = 200;
        var delay = TimeSpan.FromMilliseconds(250);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--seed" when i + 1 < args.Length: seed = args[++i]; break;
                case "--max-fetches" when i + 1 < args.Length: maxFetches = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--delay-ms" when i + 1 < args.Length: delay = TimeSpan.FromMilliseconds(int.Parse(args[++i], CultureInfo.InvariantCulture)); break;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}'");
                    return Usage();
            }
        }

        if (outDir is null)
        {
            return Usage();
        }

        Directory.CreateDirectory(outDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var now = DateTimeOffset.UtcNow;

        var comets = await BakeBulkAsync(http, outDir, CancellationToken.None);
        if (comets.Count == 0)
        {
            Console.Error.WriteLine("SBDB returned no usable comets; refusing to write a corrupt asset");
            return 1;
        }

        await BakeApparitionsAsync(http, outDir, seed, comets, now, maxFetches, delay, CancellationToken.None);
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: BakeComets --out <wwwroot> [--seed <url|path>] [--max-fetches N] [--delay-ms N]");
        return 2;
    }

    /// <summary>
    /// Mirrors the SBDB query response to <c>comets-sbdb.json</c> VERBATIM, then parses the same bytes
    /// with the app's own parser so the staleness pass below sees exactly what the browser will.
    /// Writing the raw response rather than a mapped form is what lets the browser keep using the
    /// unmodified <see cref="SbdbCometSource"/>, and taking the URL from that class rather than
    /// restating it here is what stops the two drifting (the shell step this replaced restated the
    /// query string in YAML under a "keep them in sync" comment).
    /// </summary>
    private static async Task<IReadOnlyList<CometElements>> BakeBulkAsync(HttpClient http, string outDir, CancellationToken ct)
    {
        var json = await http.GetStringAsync(SbdbCometSource.DefaultQueryUrl, ct);
        var response = JsonSerializer.Deserialize(json, SbdbJsonContext.Default.SbdbQueryResponse);

        // The shape check the shell step did with jq: a JPL error page must fail the bake rather than
        // deploy as an asset the app then chokes on.
        if (response?.Fields is not { Length: > 0 } || response.Data is not { Length: > 0 })
        {
            throw new InvalidOperationException("SBDB response has no fields/data; refusing to write it");
        }

        var path = Path.Combine(outDir, "comets-sbdb.json");
        await File.WriteAllTextAsync(path, json, ct);

        var comets = SbdbCometSource.Parse(response, NullLogger.Instance);
        Console.WriteLine($"[bake-comets] comets-sbdb.json: {comets.Count} comets ({json.Length / 1024} KiB)");
        return comets;
    }

    private static async Task BakeApparitionsAsync(
        HttpClient http,
        string outDir,
        string? seed,
        IReadOnlyList<CometElements> comets,
        DateTimeOffset now,
        int maxFetches,
        TimeSpan delay,
        CancellationToken ct)
    {
        var existing = await TryLoadSeedAsync(http, seed, ct);
        Console.WriteLine($"[bake-comets] seed: {existing.Count} entries from {seed ?? "(none)"}");

        now.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
        var jdTt = tt1 + tt2;

        // Only a bulk record a revolution or more old is worth refining -- the same test the repository
        // applies before it would ask at runtime.
        var candidates = new List<(CometElements Comet, CatalogIndex Index, TimeSpan Interval)>();
        foreach (var comet in comets)
        {
            if (comet.CatalogIndex is not { } index || !comet.IsElementSetStale(jdTt))
            {
                continue;
            }

            var peak = comet.HasMagnitudeModel && comet.PerihelionDistanceAu > 0.0
                ? comet.AbsoluteMagnitudeM1 + comet.SlopeK1 * Math.Log10(comet.PerihelionDistanceAu)
                : double.NaN;
            candidates.Add((comet, index, RefreshIntervalFor(peak)));
        }

        // Anything the bulk set no longer lists, or whose published solution has caught up so its record
        // is no longer stale, is dead weight -- drop it rather than carry it forever.
        var live = candidates.Select(c => c.Index).ToHashSet();
        var kept = existing.Where(kv => live.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var pruned = existing.Count - kept.Count;

        // Brightest first, so a run that hits its cap has spent it on the comets anyone might look at.
        var due = candidates
            .Where(c => !kept.TryGetValue(c.Index, out var e) || now - e.FetchedUtc > c.Interval)
            .OrderBy(c => c.Interval)
            .ToList();

        var horizons = new HorizonsCometSource(http, apiUri: null, NullLogger.Instance);
        int fetched = 0, failed = 0;
        foreach (var candidate in due)
        {
            if (fetched + failed >= maxFetches)
            {
                break;
            }

            try
            {
                if (await horizons.TryFetchCurrentApparitionAsync(candidate.Comet, now, ct) is { } refined)
                {
                    kept[candidate.Index] = new ApparitionEntry(now, refined);
                    fetched++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One comet Horizons will not answer for must not fail the deploy: the asset is written
                // without it, that comet keeps its bulk record, and the next run tries again.
                Console.WriteLine($"[bake-comets]   {candidate.Comet.DisplayName}: {ex.Message}");
                failed++;
            }

            await Task.Delay(delay, ct);
        }

        // A cap that silently truncated would read as "everything is covered"; say what was left.
        var deferred = due.Count - Math.Min(due.Count, maxFetches);
        Console.WriteLine(
            $"[bake-comets] apparitions: {candidates.Count} stale, {due.Count} due, {fetched} fetched, "
            + $"{failed} failed, {pruned} pruned, {deferred} deferred to the next run");

        var file = new ApparitionCacheFile([.. kept.Values]) { NoRemoteRefresh = true };
        var path = Path.Combine(outDir, "comets-apparitions.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(file, SbdbJsonContext.Default.ApparitionCacheFile), ct);
        Console.WriteLine($"[bake-comets] comets-apparitions.json: {kept.Count} entries (sealed)");
    }

    /// <summary>
    /// Reads the previous overlay so this run only refreshes what has expired. The seed is normally the
    /// CURRENTLY DEPLOYED asset, which makes the published site its own incremental state: nothing to
    /// keep in sync with what is live, and no cache to be evicted out from under a deploy that happens
    /// less often than a CI cache is retained. Any failure -- the first ever run, a 404, a truncated
    /// file -- is a cold start, which is a slow bake and never a wrong one.
    /// </summary>
    private static async Task<Dictionary<CatalogIndex, ApparitionEntry>> TryLoadSeedAsync(HttpClient http, string? seed, CancellationToken ct)
    {
        var result = new Dictionary<CatalogIndex, ApparitionEntry>();
        if (string.IsNullOrWhiteSpace(seed))
        {
            return result;
        }

        try
        {
            var json = Uri.TryCreate(seed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? await http.GetStringAsync(uri, ct)
                : await File.ReadAllTextAsync(seed, ct);

            foreach (var entry in JsonSerializer.Deserialize(json, SbdbJsonContext.Default.ApparitionCacheFile)?.Entries ?? [])
            {
                if (entry.Elements.CatalogIndex is { } index)
                {
                    result[index] = entry;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[bake-comets] no usable seed ({ex.Message}); this run is a cold bake");
            result.Clear();
        }

        return result;
    }
}
