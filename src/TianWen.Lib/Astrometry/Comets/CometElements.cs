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
    public CatalogIndex? CatalogIndex => Designation.TryToCatalogIndex(out var idx) ? idx : null;

    /// <summary>True when SBDB supplies both total-magnitude parameters, so a magnitude can be predicted.</summary>
    public bool HasMagnitudeModel => !double.IsNaN(AbsoluteMagnitudeM1) && !double.IsNaN(SlopeK1);
}
