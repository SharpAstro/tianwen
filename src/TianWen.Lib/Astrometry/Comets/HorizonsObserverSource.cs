using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>One topocentric astrometric position of a moving target, as seen from a specific site.</summary>
/// <param name="TimeUtc">The instant the position is stated for.</param>
/// <param name="RaDeg">Astrometric right ascension, ICRF/J2000, degrees.</param>
/// <param name="DecDeg">Astrometric declination, ICRF/J2000, degrees.</param>
public readonly record struct EphemerisSample(DateTimeOffset TimeUtc, double RaDeg, double DecDeg);

/// <summary>Fetches a TOPOCENTRIC position track for one small body, from JPL Horizons.</summary>
internal interface IHorizonsObserverSource
{
    Task<ImmutableArray<EphemerisSample>> TryFetchTrackAsync(
        string designation,
        double siteLatDeg,
        double siteLonDeg,
        double siteElevMetres,
        DateTimeOffset start,
        DateTimeOffset stop,
        TimeSpan step,
        CancellationToken cancellationToken);
}

/// <summary>
/// The OBSERVER counterpart to <see cref="HorizonsCometSource"/>, which fetches ELEMENTS. Elements
/// describe an orbit; this describes where a body appeared FROM A PARTICULAR PLACE ON EARTH, which is
/// the only thing that can drive comet-aligned registration.
///
/// <para><b>Why the geocentric path cannot serve.</b> <c>CometEphemeris.TryGetEquatorialJ2000</c> is
/// geocentric, and on the 10P/Tempel 2 set topocentric minus geocentric moves by 2.74 px across the
/// run -- 25x the registration residual. Diurnal parallax is a change in the OBSERVER's position, not
/// a rotation of the field, so a well polar-aligned mount does not reduce it by one pixel; only asking
/// from the right place does. Hence <c>SITE_COORD</c> from the frames' own
/// <c>SITELAT</c>/<c>SITELONG</c>/<c>SITEELEV</c>.</para>
///
/// <para><b>Astrometric, not apparent</b> (<c>QUANTITIES='1'</c>). The WCS a plate solve produces is
/// tied to catalogue star positions, so the comet's position has to be expressed in that same frame.
/// Apparent place (<c>QUANTITIES='2'</c>) additionally applies refraction and aberration of the
/// observer's motion, which the star field carries too and the WCS has therefore already absorbed;
/// mixing the two would inject the difference straight into the rate.</para>
///
/// <para>Parsing is pure and pinned against a frozen real response
/// (<c>Data/horizons-10p-observer-2026-08-16.txt</c>), the same discipline as the elements source.</para>
/// </summary>
internal sealed class HorizonsObserverSource : IHorizonsObserverSource
{
    private static readonly HttpClient s_httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly HttpClient _httpClient;
    private readonly Uri _apiUri;
    private readonly ILogger _logger;

    public HorizonsObserverSource(ILogger<HorizonsObserverSource> logger)
        : this(s_httpClient, apiUri: null, logger)
    {
    }

    /// <summary>For callers that hold a plain <see cref="ILogger"/> rather than a typed one -- the
    /// stacking pipeline is constructed with one, and a non-generic logger parameter cannot be
    /// resolved from DI (see the CatalogPlateSolver note in CLAUDE.md), so this ctor is reached by
    /// direct construction only.</summary>
    internal HorizonsObserverSource(ILogger logger)
        : this(s_httpClient, apiUri: null, logger)
    {
    }

    internal HorizonsObserverSource(HttpClient httpClient, Uri? apiUri, ILogger logger)
    {
        _httpClient = httpClient;
        _apiUri = apiUri ?? new Uri(HorizonsCometSource.DefaultApiUrl);
        _logger = logger;
    }

    /// <summary>
    /// Builds the Horizons OBSERVER query for one body over one window, seen from one site.
    ///
    /// <para><c>SITE_COORD</c> is <b>E-longitude, latitude, altitude in KILOMETRES</b>, in that order.
    /// East-positive longitude is Horizons' convention and matches the FITS <c>SITELONG</c> card;
    /// the kilometre unit is the trap, since every other altitude in this codebase is metres, and
    /// getting it wrong by 1000x is a parallax error far too small to notice on inspection and far
    /// too large to accept in a registration rate.</para>
    /// </summary>
    internal static string BuildQuery(
        Uri apiUri, string designation,
        double siteLatDeg, double siteLonDeg, double siteElevMetres,
        DateTimeOffset start, DateTimeOffset stop, TimeSpan step)
    {
        var inv = CultureInfo.InvariantCulture;
        var site = string.Create(inv, $"{siteLonDeg:0.######},{siteLatDeg:0.######},{siteElevMetres / 1000.0:0.######}");
        var startStr = start.UtcDateTime.ToString("yyyy-MM-dd HH:mm", inv);
        var stopStr = stop.UtcDateTime.ToString("yyyy-MM-dd HH:mm", inv);
        // Horizons takes the step as a count plus a unit; minutes keep it expressible for any
        // sub-hour cadence a session needs, and a session is hours, so the count stays small.
        var stepStr = string.Create(inv, $"{Math.Max(1, (int)Math.Round(step.TotalMinutes))} m");

        return $"{apiUri}?format=text"
            + $"&COMMAND={Uri.EscapeDataString($"'DES={designation};CAP;'")}"
            + "&MAKE_EPHEM=YES&EPHEM_TYPE=OBSERVER"
            + $"&CENTER={Uri.EscapeDataString("'coord@399'")}"
            + $"&COORD_TYPE={Uri.EscapeDataString("'GEODETIC'")}"
            + $"&SITE_COORD={Uri.EscapeDataString($"'{site}'")}"
            + $"&QUANTITIES={Uri.EscapeDataString("'1'")}"
            + $"&ANG_FORMAT={Uri.EscapeDataString("'DEG'")}"
            + "&CSV_FORMAT=YES"
            + $"&START_TIME={Uri.EscapeDataString($"'{startStr}'")}"
            + $"&STOP_TIME={Uri.EscapeDataString($"'{stopStr}'")}"
            + $"&STEP_SIZE={Uri.EscapeDataString($"'{stepStr}'")}";
    }

    /// <summary>
    /// Reads the <c>$$SOE</c>/<c>$$EOE</c> block of a CSV OBSERVER response.
    ///
    /// <para>The RA and Dec columns are located BY HEADER NAME rather than by position. Horizons puts
    /// two unnamed flag columns (solar and lunar presence) between the date and the coordinates, and
    /// they are populated only sometimes -- the frozen fixture has <c>m</c> in one of them for the
    /// first two rows and blanks thereafter -- so a positional reader looks correct against a sample
    /// where the flags happen to be blank and silently reads a flag as a coordinate where they are
    /// not. Naming the columns also survives anybody adding a quantity to the query.</para>
    /// </summary>
    internal static bool TryParse(string response, out ImmutableArray<EphemerisSample> samples)
    {
        samples = [];
        var soe = response.IndexOf("$$SOE", StringComparison.Ordinal);
        var eoe = response.IndexOf("$$EOE", StringComparison.Ordinal);
        if (soe < 0 || eoe < 0 || eoe <= soe)
        {
            return false;
        }

        // The column header is the last line before $$SOE that NAMES a column, not simply the last
        // non-blank one: Horizons draws a rule of asterisks immediately under the header and again
        // above $$SOE, so "last non-blank line" lands on "****...****" every time. Anchoring on the
        // RA column instead is unambiguous and cannot be fooled by decoration.
        var headerLine = "";
        foreach (var line in response[..soe].Split('\n'))
        {
            if (line.Contains("R.A.", StringComparison.OrdinalIgnoreCase))
            {
                headerLine = line;
            }
        }

        var headers = headerLine.Split(',');
        int raCol = -1, decCol = -1;
        for (var i = 0; i < headers.Length; i++)
        {
            var h = headers[i].Trim();
            if (raCol < 0 && h.StartsWith("R.A.", StringComparison.OrdinalIgnoreCase)) raCol = i;
            else if (decCol < 0 && h.StartsWith("DEC", StringComparison.OrdinalIgnoreCase)) decCol = i;
        }
        if (raCol < 0 || decCol < 0)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<EphemerisSample>();
        var body = response[(soe + "$$SOE".Length)..eoe];
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var cells = line.Split(',');
            if (cells.Length <= Math.Max(raCol, decCol))
            {
                continue;
            }
            if (!TryParseTime(cells[0].Trim(), out var when)
                || !double.TryParse(cells[raCol].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ra)
                || !double.TryParse(cells[decCol].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dec))
            {
                continue;
            }
            builder.Add(new EphemerisSample(when, ra, dec));
        }

        samples = builder.ToImmutable();
        return samples.Length > 0;
    }

    /// <summary>Horizons stamps "2026-Aug-16 10:53", adding seconds only for a sub-minute step.</summary>
    private static bool TryParseTime(string cell, out DateTimeOffset when)
    {
        ReadOnlySpan<string> formats = ["yyyy-MMM-dd HH:mm", "yyyy-MMM-dd HH:mm:ss", "yyyy-MMM-dd HH:mm:ss.fff"];
        foreach (var f in formats)
        {
            if (DateTimeOffset.TryParseExact(cell, f, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when))
            {
                return true;
            }
        }
        when = default;
        return false;
    }

    public async Task<ImmutableArray<EphemerisSample>> TryFetchTrackAsync(
        string designation,
        double siteLatDeg, double siteLonDeg, double siteElevMetres,
        DateTimeOffset start, DateTimeOffset stop, TimeSpan step,
        CancellationToken cancellationToken)
    {
        var url = BuildQuery(_apiUri, designation, siteLatDeg, siteLonDeg, siteElevMetres, start, stop, step);
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!TryParse(text, out var samples))
            {
                _logger.LogWarning("Horizons returned no usable OBSERVER record for {Designation}", designation);
                return [];
            }
            _logger.LogDebug("Horizons OBSERVER track for {Designation}: {Count} samples over {Span}",
                designation, samples.Length, stop - start);
            return samples;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Unreachable Horizons is an EXPECTED state, not an error: the offline answer is the
            // explicit rate the caller can pass instead, so this reports empty and lets the caller
            // decide rather than failing a stack that had a perfectly good fallback available.
            _logger.LogWarning(ex, "Horizons OBSERVER fetch failed for {Designation}; an explicit comet rate can be supplied instead", designation);
            return [];
        }
    }
}
