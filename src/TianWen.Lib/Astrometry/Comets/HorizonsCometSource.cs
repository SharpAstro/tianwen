using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>Fetches ONE comet's osculating elements for the apparition in progress, from JPL Horizons.</summary>
internal interface IHorizonsCometSource
{
    Task<CometElements?> TryFetchCurrentApparitionAsync(CometElements baseElements, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>
/// Per-object refinement over <see cref="SbdbCometSource"/>: the osculating element set for the
/// apparition IN PROGRESS, which is the difference between drawing a comet in the right place and
/// drawing it degrees away.
///
/// <para><b>Why the bulk set is not enough.</b> SBDB publishes one default record per comet, stated at
/// whatever osculating epoch that solution used, and for a periodic comet that is routinely an earlier
/// apparition: 10P's is epoch 2016. Propagating it two-body carries a FIXED period, while JPL's own
/// integration includes the non-gravitational (outgassing) terms it fits, which move the period by
/// about a day per revolution for an active comet. A period error integrates straight into phase, so
/// after two revolutions 10P landed 3.76 days late, and 3.76 days at 31 km/s seen from 0.41 AU is
/// <b>9.3 degrees</b> of sky (measured in <c>CometEphemerisTests</c>).</para>
///
/// <para><b>Why this is cheap.</b> Horizons will state the osculating elements at any instant, and
/// osculating elements at time T already contain the perturbation state at T. So the six numbers
/// <see cref="CometEphemeris"/> already consumes, asked for at today's date, make the same two-body
/// propagator accurate again without modelling non-gravitational forces at all. No new maths, no
/// ephemeris stream to follow: one small text response per comet, cached.</para>
///
/// <para>Deliberately per-object and on demand. The bulk SBDB fetch stays the base layer because it is
/// what makes 4,000 comets available offline in one keyless request; this refines only the handful a
/// user is actually looking at.</para>
/// </summary>
internal sealed partial class HorizonsCometSource : IHorizonsCometSource
{
    internal const string DefaultApiUrl = "https://ssd.jpl.nasa.gov/api/horizons.api";

    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _apiUri;
    private readonly ILogger _logger;

    public HorizonsCometSource(ILogger<HorizonsCometSource> logger)
        : this(s_httpClient, apiUri: null, logger)
    {
    }

    // Test seam / endpoint override, mirroring SbdbCometSource: a browser host cannot reach JPL
    // directly (no CORS headers), so it points at a same-origin proxy or simply never calls this.
    internal HorizonsCometSource(HttpClient httpClient, Uri? apiUri, ILogger logger)
    {
        _httpClient = httpClient;
        _apiUri = apiUri ?? new Uri(DefaultApiUrl);
        _logger = logger;
    }

    /// <summary>
    /// Builds the Horizons ELEMENTS query for one comet at one instant.
    ///
    /// <para>Every parameter here is load-bearing. <c>CAP</c> selects the apparition in progress rather
    /// than making the request ambiguous for a comet with several solutions; <c>OUT_UNITS=AU-D</c> is
    /// what makes QR and A come back in AU instead of km (the default), which would otherwise be read
    /// as an orbit 150 million times too large; <c>CENTER=500@10</c> asks for SUN-centred elements,
    /// which is the frame the propagator assumes; and the one-day window with a one-day step is the
    /// smallest request that returns exactly one record.</para>
    /// </summary>
    internal static string BuildQuery(Uri apiUri, string designation, DateTimeOffset at)
    {
        var start = at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var stop = at.UtcDateTime.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"{apiUri}?format=text"
            + $"&COMMAND={Uri.EscapeDataString($"'DES={designation};CAP;'")}"
            + "&MAKE_EPHEM=YES&EPHEM_TYPE=ELEMENTS"
            + $"&CENTER={Uri.EscapeDataString("'500@10'")}"
            + $"&OUT_UNITS={Uri.EscapeDataString("'AU-D'")}"
            + $"&REF_PLANE={Uri.EscapeDataString("'ECLIPTIC'")}"
            + $"&REF_SYSTEM={Uri.EscapeDataString("'J2000'")}"
            + $"&START_TIME={Uri.EscapeDataString($"'{start}'")}"
            + $"&STOP_TIME={Uri.EscapeDataString($"'{stop}'")}"
            + $"&STEP_SIZE={Uri.EscapeDataString("'1 d'")}";
    }

    public async Task<CometElements?> TryFetchCurrentApparitionAsync(
        CometElements baseElements, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var designation = baseElements.Designation.ToCanonical();
        var url = BuildQuery(_apiUri, designation, at);

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!TryParse(text, baseElements, out var refined))
        {
            _logger.LogWarning("Horizons returned no usable element record for {Designation}", designation);
            return null;
        }

        _logger.LogDebug(
            "Horizons current-apparition elements for {Designation}: epoch {Epoch} (was {OldEpoch}), Tp {Tp} (was {OldTp})",
            designation, refined.EpochJdTt, baseElements.EpochJdTt, refined.PerihelionJdTt, baseElements.PerihelionJdTt);
        return refined;
    }

    /// <summary>
    /// Reads the first record of a Horizons ELEMENTS response, keeping everything from
    /// <paramref name="baseElements"/> that Horizons is not being asked for: the designation, the
    /// common name, and the photometric model. The magnitude parameters deliberately come from SBDB,
    /// because they are the SAME ones Horizons uses (verified for 10P: M1 = 13.7, K1 = 6.5 on both) and
    /// re-parsing them from the header block would add a failure mode for no gain.
    ///
    /// <para>Pure, so it is unit-tested against a frozen response rather than the network.</para>
    /// </summary>
    internal static bool TryParse(string response, CometElements baseElements, out CometElements elements)
    {
        elements = baseElements;

        var soe = response.IndexOf("$$SOE", StringComparison.Ordinal);
        var eoe = response.IndexOf("$$EOE", StringComparison.Ordinal);
        if (soe < 0 || eoe <= soe)
        {
            return false;
        }

        var block = response[(soe + "$$SOE".Length)..eoe];

        // The record opens with its own epoch: "2461258.500000000 = A.D. 2026-Aug-06 ...". That JD IS
        // the osculating epoch of the elements below it, and carrying it is what later lets
        // IsElementSetStale see that this set is current.
        var epochMatch = EpochPattern().Match(block);
        if (!epochMatch.Success
            || !double.TryParse(epochMatch.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var epochJdTt))
        {
            return false;
        }

        // Keys are written with an optional space before '=' ("W =", "A ="), and several longer keys end
        // in a letter this could otherwise match (MA=, TA=, AD=). A word boundary plus the explicit
        // alternation handles both: "\bA\s*=" cannot match inside "MA=" (no boundary between M and A)
        // nor "AD=" (a 'D' follows the 'A', not '=').
        if (!TryReadField(block, "EC", out var eccentricity)
            || !TryReadField(block, "QR", out var perihelionDistanceAu)
            || !TryReadField(block, "IN", out var inclinationDeg)
            || !TryReadField(block, "OM", out var ascendingNodeDeg)
            || !TryReadField(block, "W", out var argumentOfPerihelionDeg)
            || !TryReadField(block, "Tp", out var perihelionJdTt))
        {
            return false;
        }

        // A sanity gate, because a units mistake here is silent and catastrophic: QR in km (the Horizons
        // default, if OUT_UNITS ever stopped being sent) is ~1.5e8 for a 1 AU perihelion, which would
        // sail through as a valid double and put the comet in another galaxy.
        if (!(perihelionDistanceAu > 0.0 && perihelionDistanceAu < 1000.0) || !(eccentricity >= 0.0))
        {
            return false;
        }

        elements = baseElements with
        {
            PerihelionDistanceAu = perihelionDistanceAu,
            Eccentricity = eccentricity,
            InclinationDeg = inclinationDeg,
            AscendingNodeDeg = ascendingNodeDeg,
            ArgumentOfPerihelionDeg = argumentOfPerihelionDeg,
            PerihelionJdTt = perihelionJdTt,
            EpochJdTt = epochJdTt,
        };
        return true;
    }

    private static bool TryReadField(string block, string key, out double value)
    {
        var match = Regex.Match(block, $@"\b{Regex.Escape(key)}\s*=\s*([-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)");
        if (match.Success
            && double.TryParse(match.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }
        value = double.NaN;
        return false;
    }

    [GeneratedRegex(@"^\s*([0-9]+\.[0-9]+)\s*=\s*A\.D\.", RegexOptions.Multiline)]
    private static partial Regex EpochPattern();
}
