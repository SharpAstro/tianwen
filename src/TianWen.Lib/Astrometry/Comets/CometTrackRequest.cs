using System;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>
/// Everything needed to ask JPL Horizons for the track behind a comet-aligned stack, derived from
/// the frames themselves.
/// </summary>
/// <param name="Designation">Compact form, e.g. <c>10P</c>, which is what Horizons' <c>DES=</c> takes.</param>
/// <param name="SiteLatDeg">Observatory latitude, degrees north.</param>
/// <param name="SiteLonDeg">Observatory longitude, degrees EAST (Horizons' convention, and the FITS
/// <c>SITELONG</c> convention, so no sign flip belongs anywhere on this path).</param>
/// <param name="SiteElevMetres">Observatory elevation, metres. Converted to km at the query.</param>
/// <param name="Start">First instant to sample, already padded to bracket the first exposure.</param>
/// <param name="Stop">Last instant to sample, padded past the final exposure's END.</param>
/// <param name="Step">Sampling cadence.</param>
public readonly record struct CometTrackRequest(
    string Designation,
    double SiteLatDeg,
    double SiteLonDeg,
    double SiteElevMetres,
    DateTimeOffset Start,
    DateTimeOffset Stop,
    TimeSpan Step)
{
    /// <summary>
    /// How many samples the window aims for. The fit only needs two; more exist so the residual is
    /// a meaningful statement about how straight the track was rather than an arithmetic zero, and
    /// so a curved track has somewhere to show it. A dozen keeps the response small enough to be
    /// cheap while making the residual worth reading.
    /// </summary>
    public const int TargetSampleCount = 12;

    /// <summary>
    /// Builds the request, or returns <c>null</c> when the frames cannot support one.
    ///
    /// <para><b>An unknown site is a refusal, not a fallback to geocentric.</b> Horizons will happily
    /// answer from the geocentre, and the answer looks entirely reasonable -- on the 10P set it is
    /// wrong by 3.4 degrees of heading while fitting a straighter line than the correct track. Since
    /// no downstream check can catch that (see <see cref="CometRateSolver"/>), the only safe response
    /// to missing <c>SITELAT</c>/<c>SITELONG</c> is to decline and let the caller fall back to an
    /// explicit rate.</para>
    /// </summary>
    /// <param name="requestedDesignation">What the caller asked for. Empty or whitespace means
    /// "read it off the frames", which is the unattended case.</param>
    /// <param name="objectName">The frames' <c>OBJECT</c> card.</param>
    /// <param name="siteLatDeg">From <c>SITELAT</c>; NaN when the frames did not say.</param>
    /// <param name="siteLonDeg">From <c>SITELONG</c>; NaN when the frames did not say.</param>
    /// <param name="siteElevMetres">From <c>SITEELEV</c>; NaN is treated as sea level, which costs
    /// well under a thousandth of a pixel and is not worth refusing over.</param>
    /// <param name="firstExposureStart">Earliest <c>DATE-OBS</c> in the group.</param>
    /// <param name="lastExposureEnd">Latest <c>DATE-OBS</c> plus that frame's exposure.</param>
    public static CometTrackRequest? TryBuild(
        string? requestedDesignation,
        string? objectName,
        double siteLatDeg,
        double siteLonDeg,
        double siteElevMetres,
        DateTimeOffset firstExposureStart,
        DateTimeOffset lastExposureEnd)
    {
        var wanted = string.IsNullOrWhiteSpace(requestedDesignation) ? objectName : requestedDesignation;
        if (string.IsNullOrWhiteSpace(wanted) || !CometDesignation.TryParse(wanted, out var designation))
        {
            return null;
        }

        if (double.IsNaN(siteLatDeg) || double.IsNaN(siteLonDeg))
        {
            return null;
        }

        if (lastExposureEnd <= firstExposureStart)
        {
            return null;
        }

        var span = lastExposureEnd - firstExposureStart;
        // Round the step to whole minutes because that is the resolution Horizons' STEP_SIZE is
        // expressed in; asking for a cadence it cannot honour yields a different window than the
        // one computed here, and the fit would then be told a span it did not receive.
        var stepMinutes = Math.Max(1.0, Math.Round(span.TotalMinutes / (TargetSampleCount - 1)));
        var step = TimeSpan.FromMinutes(stepMinutes);

        // Pad by one step each side so every frame's epoch falls INSIDE the sampled window. The fit
        // is a straight line and would extrapolate happily, but an extrapolated endpoint is exactly
        // where a track's curvature is least constrained, and the first and last frames are the two
        // that pay the most for a wrong slope.
        return new CometTrackRequest(
            designation.ToCompact(),
            siteLatDeg,
            siteLonDeg,
            double.IsNaN(siteElevMetres) ? 0.0 : siteElevMetres,
            firstExposureStart - step,
            lastExposureEnd + step,
            step);
    }
}
