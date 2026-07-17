using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>Fetches the full comet set from JPL's Small-Body Database, mapped to <see cref="CometElements"/>.</summary>
internal interface ISbdbCometSource
{
    Task<IReadOnlyList<CometElements>> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Raw JPL SBDB query response: a column-name array plus rows of string-or-null cells
/// (<c>{ "fields": [...], "data": [[...]], "count": N }</c>). All cells arrive as JSON strings even for
/// numbers, so the parser converts with the invariant culture.
/// </summary>
internal sealed class SbdbQueryResponse
{
    public string[]? Fields { get; set; }
    public string?[][]? Data { get; set; }
    public int? Count { get; set; }
}

/// <summary>
/// The comet-discovery data source: one keyless HTTPS GET to the SBDB query API returns every comet's
/// designation, common name, IAU total-magnitude parameters (M1/K1) and osculating elements. That single
/// bulk fetch IS the "database" -- with the elements cached locally, positions and magnitudes at any time
/// are pure local computation (<see cref="CometEphemeris"/>), so the sky map, planner and search all work
/// offline without a per-object round-trip. SBDB is continuously updated from MPC observations, so it is
/// the authoritative discovery source (no separate MPC file needed). Follows the no-key weather-driver
/// pattern: a shared static <see cref="HttpClient"/>; the pure <see cref="Parse"/> step is unit-tested
/// directly and the caching is layered on top in <see cref="CometRepository"/>.
/// </summary>
internal sealed class SbdbCometSource : ISbdbCometSource
{
    // sb-kind=c: comets only. `prefix` carries the orbit-type letter (C/P/D/X/A/I) that `pdes` omits for
    // provisional comets, so the two together reconstruct the canonical designation. Fields are requested
    // in a fixed order but the parser maps by name defensively.
    internal const string DefaultQueryUrl =
        "https://ssd-api.jpl.nasa.gov/sbdb_query.api?sb-kind=c&fields=prefix,pdes,name,M1,K1,e,q,i,om,w,tp,epoch";

    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _queryUri;
    private readonly ILogger _logger;

    public SbdbCometSource(ILogger<SbdbCometSource> logger)
        : this(s_httpClient, queryUri: null, logger)
    {
    }

    // Test seam: inject an HttpClient wrapping a canned-response handler.
    internal SbdbCometSource(HttpClient httpClient, ILogger logger)
        : this(httpClient, queryUri: null, logger)
    {
    }

    // Endpoint-override seam over the shared client (see queryUri remarks on the primary ctor).
    internal SbdbCometSource(Uri? queryUri, ILogger logger)
        : this(s_httpClient, queryUri, logger)
    {
    }

    // queryUri overrides the live SBDB endpoint with a snapshot of the SAME query response - e.g. the
    // browser host points it at a CI-baked same-origin static file, because JPL sends no CORS headers
    // and a cross-origin fetch can never succeed from a browser. Null = the live JPL API.
    internal SbdbCometSource(HttpClient httpClient, Uri? queryUri, ILogger logger)
    {
        _httpClient = httpClient;
        _queryUri = queryUri ?? new Uri(DefaultQueryUrl);
        _logger = logger;
    }

    public async Task<IReadOnlyList<CometElements>> FetchAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching comet elements from SBDB: {Url}", _queryUri);

        using var response = await _httpClient.GetAsync(_queryUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var parsed = await JsonSerializer.DeserializeAsync(stream, SbdbJsonContext.Default.SbdbQueryResponse, cancellationToken);

        return Parse(parsed, _logger);
    }

    /// <summary>
    /// Pure mapping from the SBDB column/row response to <see cref="CometElements"/>. Rows whose
    /// designation cannot be parsed/packed, or that lack a core orbital element, are skipped and counted
    /// (never a silent truncation). Missing M1/K1 map to NaN (no magnitude model).
    /// </summary>
    internal static IReadOnlyList<CometElements> Parse(SbdbQueryResponse? response, ILogger? logger)
    {
        if (response?.Fields is not { } fields || response.Data is not { } rows)
        {
            logger?.LogWarning("SBDB response had no fields/data");
            return [];
        }

        int Col(string name) => Array.IndexOf(fields, name);
        var iPrefix = Col("prefix");
        var iPdes = Col("pdes");
        var iName = Col("name");
        var iM1 = Col("M1");
        var iK1 = Col("K1");
        var iE = Col("e");
        var iQ = Col("q");
        var iInc = Col("i");
        var iOm = Col("om");
        var iW = Col("w");
        var iTp = Col("tp");
        var iEpoch = Col("epoch");

        if (iPdes < 0 || iE < 0 || iQ < 0 || iInc < 0 || iOm < 0 || iW < 0 || iTp < 0)
        {
            logger?.LogWarning("SBDB response missing required element columns: {Fields}", string.Join(",", fields));
            return [];
        }

        var result = new List<CometElements>(rows.Length);
        var skipped = 0;
        foreach (var row in rows)
        {
            if (row is null
                || Cell(row, iPdes) is not { } pdes
                || !CometDesignation.TryParse(ReconstructDesignation(iPrefix >= 0 ? Cell(row, iPrefix) : null, pdes), out var designation)
                || !designation.TryToCatalogIndex(out _)
                || !TryNum(row, iE, out var e)
                || !TryNum(row, iQ, out var q)
                || !TryNum(row, iInc, out var inc)
                || !TryNum(row, iOm, out var om)
                || !TryNum(row, iW, out var w)
                || !TryNum(row, iTp, out var tp))
            {
                skipped++;
                continue;
            }

            result.Add(new CometElements(
                Designation: designation,
                CommonName: iName >= 0 ? Cell(row, iName) : null,
                PerihelionDistanceAu: q,
                Eccentricity: e,
                InclinationDeg: inc,
                AscendingNodeDeg: om,
                ArgumentOfPerihelionDeg: w,
                PerihelionJdTt: tp,
                EpochJdTt: iEpoch >= 0 && TryNum(row, iEpoch, out var epoch) ? epoch : double.NaN,
                AbsoluteMagnitudeM1: iM1 >= 0 && TryNum(row, iM1, out var m1) ? m1 : double.NaN,
                SlopeK1: iK1 >= 0 && TryNum(row, iK1, out var k1) ? k1 : double.NaN));
        }

        if (skipped > 0)
        {
            logger?.LogInformation("SBDB: mapped {Mapped} comets, skipped {Skipped} (unparseable designation or missing elements)", result.Count, skipped);
        }

        return result;
    }

    // SBDB stores a provisional comet's designation without its orbit-type prefix ("2023 A3") and keeps
    // the prefix ("C") in a separate column, so rejoin them ("C/2023 A3"). A numbered comet's pdes ("12P",
    // "73P-C") is already the full designation (it has no embedded space) and needs no prefix.
    private static string ReconstructDesignation(string? prefix, string pdes)
        => prefix is { Length: > 0 } && pdes.Contains(' ') ? $"{prefix}/{pdes}" : pdes;

    private static string? Cell(string?[] row, int col)
        => col >= 0 && col < row.Length && row[col] is { Length: > 0 } s ? s : null;

    private static bool TryNum(string?[] row, int col, out double value)
    {
        if (Cell(row, col) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = double.NaN;
        return false;
    }
}
