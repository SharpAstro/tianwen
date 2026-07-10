using System.Text.Json.Serialization;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>
/// Osculating two-body orbital elements plus the IAU total-magnitude parameters for a single comet,
/// as published by JPL's Small-Body Database (SBDB). This is the domain type the ephemeris
/// (<see cref="CometEphemeris"/>) and the magnitude law consume; the on-disk cache DTO in the data
/// source maps onto it. Angles are in degrees and distances in AU exactly as SBDB reports them (the
/// propagator converts to radians internally), so a value inspected here matches the source verbatim.
///
/// <para>Two-body propagation needs only <see cref="PerihelionDistanceAu"/>, <see cref="Eccentricity"/>,
/// <see cref="InclinationDeg"/>, <see cref="AscendingNodeDeg"/>, <see cref="ArgumentOfPerihelionDeg"/> and
/// <see cref="PerihelionJdTt"/>; <see cref="EpochJdTt"/> is carried only to reason about how stale the
/// osculating set is. <see cref="AbsoluteMagnitudeM1"/>/<see cref="SlopeK1"/> drive the total-magnitude
/// law <c>m = M1 + 5*log10(delta) + K1*log10(r)</c>; either being NaN means SBDB has no photometric model
/// and the predicted magnitude is undefined.</para>
/// </summary>
public readonly record struct CometElements(
    CometDesignation Designation,
    string? CommonName,
    double PerihelionDistanceAu,
    double Eccentricity,
    double InclinationDeg,
    double AscendingNodeDeg,
    double ArgumentOfPerihelionDeg,
    double PerihelionJdTt,
    double EpochJdTt,
    double AbsoluteMagnitudeM1,
    double SlopeK1)
{
    /// <summary>The comet's identity as a <see cref="Catalog.Comet"/> <see cref="CatalogIndex"/>, or null
    /// when the designation cannot be packed (SOHO-style high-order fragments; never observable targets).</summary>
    [JsonIgnore]
    public CatalogIndex? CatalogIndex => Designation.TryToCatalogIndex(out var idx) ? idx : null;

    /// <summary>True when SBDB supplies both total-magnitude parameters, so a magnitude can be predicted.</summary>
    [JsonIgnore]
    public bool HasMagnitudeModel => !double.IsNaN(AbsoluteMagnitudeM1) && !double.IsNaN(SlopeK1);

    /// <summary>
    /// The comet's human display label, following the IAU/Wikipedia convention and the single source of
    /// truth for how a comet is named across the app (search results, sky-map info panel, planner, MCP):
    /// a numbered periodic comet uses the slash style <c>"10P/Tempel"</c>, while a provisional comet (whose
    /// canonical designation already carries a <c>C/</c>-style slash) uses the parenthetical style
    /// <c>"C/2026 A1 (PANSTARRS)"</c>. Falls back to the bare canonical when SBDB has no common name.
    /// </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var canonical = Designation.ToCanonical();
            if (CommonName is not { Length: > 0 } commonName)
            {
                return canonical;
            }
            // A provisional designation ("C/2026 A1") already contains '/', so append the name in parens;
            // a numbered one ("10P") has no slash, so join with '/' for the "10P/Tempel" style.
            return canonical.Contains('/') ? $"{canonical} ({commonName})" : $"{canonical}/{commonName}";
        }
    }
}
