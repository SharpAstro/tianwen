using System;
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
    /// Orbital period in years for a closed orbit (Kepler's third law, <c>P = a^1.5</c> with a in AU),
    /// or <see cref="double.NaN"/> for a parabolic or hyperbolic one, which never returns.
    /// </summary>
    [JsonIgnore]
    public double OrbitalPeriodYears
        => Eccentricity < 1.0 && PerihelionDistanceAu > 0.0
            ? Math.Pow(PerihelionDistanceAu / (1.0 - Eccentricity), 1.5)
            : double.NaN;

    /// <summary>
    /// How many revolutions have elapsed between the element set's epoch and <paramref name="jdTt"/>.
    /// <see cref="double.NaN"/> for a non-periodic comet, where the question does not apply.
    /// </summary>
    public double RevolutionsSinceEpoch(double jdTt)
        => OrbitalPeriodYears is var years and > 0.0 && !double.IsNaN(years)
            ? (jdTt - EpochJdTt) / (years * 365.25)
            : double.NaN;

    /// <summary>
    /// True when this element set is at least one full revolution old, at which point a POSITION
    /// propagated from it carries an along-track error large enough to matter on a sky map.
    ///
    /// <para><b>The measured case.</b> 10P's published record is stated at an osculating epoch of
    /// 2016-Sep-19, so by 2026 it is ~1.8 revolutions old, and our two-body propagation lands
    /// <b>9.3 degrees</b> from JPL Horizons (pinned in <c>CometEphemerisTests</c>). The heliocentric
    /// and geocentric distances are right to a few parts in ten thousand, so the comet is at the
    /// correct place in its orbit and the wrong place ALONG it: JPL fits non-gravitational terms
    /// (outgassing, A1/A2) that perturb the period, and a period error integrates straight into phase.
    /// The fix is fresher elements, which is the deferred per-object Horizons fetch; until then the
    /// marker is drawn and labelled as approximate.</para>
    ///
    /// <para><b>This says nothing about the magnitude, and that correction is the point.</b> The
    /// obvious reading of a faint comet near perihelion is that its photometric model is out of date,
    /// and for 10P that reading is WRONG: Horizons predicts 12.776 for the same instant from the same
    /// M1 = 13.7 / K1 = 6.5, against our 12.75, and its solution is nine days old and fitted to 6,347
    /// observations through 2026. A comet simply can be that faint near perihelion. Do not attach a
    /// brightness caveat to this flag, and do not "fix" the magnitude law.</para>
    /// </summary>
    public bool IsElementSetStale(double jdTt)
        => RevolutionsSinceEpoch(jdTt) is var revolutions && revolutions >= 1.0;

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
