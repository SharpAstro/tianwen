using System;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Lunar;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.VSOP87;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Where a light frame pointed and what the sky around it was doing, read from its header (#1974): the covariates an
/// expected-sky model is fitted on (<see cref="ExpectedSkyModel"/>) and the dataset gradient report's geometry. Every
/// member is NaN where the header lacks the time, the site or the pointing.
/// </summary>
/// <param name="RaHours">The pointing used: the plate solution's centre when given, else the header's target.</param>
/// <param name="DecDeg">Declination of that pointing.</param>
/// <param name="HourAngleHours">Hour angle in (-12, 12].</param>
/// <param name="AltitudeDeg">Altitude of the pointing.</param>
/// <param name="AzimuthDeg">Azimuth of the pointing, from north through east.</param>
/// <param name="Airmass">Plane-parallel airmass (<see cref="SiteContext.AirmassFromAltitude"/>).</param>
/// <param name="West">True when the pointing is west of the meridian (positive hour angle); false when east or unknown.</param>
/// <param name="ParallacticDeg">Parallactic angle.</param>
/// <param name="MoonAltitudeDeg">Moon altitude, negative below the horizon.</param>
/// <param name="MoonIllumination">Illuminated fraction of the Moon, 0 to 1.</param>
/// <param name="MoonSeparationDeg">Angular distance from the pointing to the Moon.</param>
/// <param name="MoonRaHours">Moon right ascension.</param>
/// <param name="MoonDecDeg">Moon declination.</param>
/// <param name="HoursFromLocalMidnight">Hours from local mean solar midnight at the site's longitude, in [-12, 12).</param>
public readonly record struct SkyCovariates(
    double RaHours,
    double DecDeg,
    double HourAngleHours,
    double AltitudeDeg,
    double AzimuthDeg,
    double Airmass,
    bool West,
    double ParallacticDeg,
    double MoonAltitudeDeg,
    double MoonIllumination,
    double MoonSeparationDeg,
    double MoonRaHours,
    double MoonDecDeg,
    double HoursFromLocalMidnight)
{
    /// <summary>Whether the Moon was above the horizon.</summary>
    public bool MoonUp => MoonAltitudeDeg > 0;

    /// <summary>Reads the covariates off a frame's metadata; <paramref name="solution"/>, when given, centres the pointing.</summary>
    public static SkyCovariates From(in ImageMeta meta, WCS? solution = null)
    {
        var raHours = solution?.CenterRA ?? meta.TargetRA;
        var decDeg = solution?.CenterDec ?? meta.TargetDec;
        var epoch = meta.ExposureStartTime;
        double lat = meta.Latitude;
        double lon = meta.Longitude;
        var hasEpoch = epoch.Year >= 1900;
        var lst = hasEpoch ? SiteContext.ComputeLST(epoch, lon) : double.NaN;
        var ha = CoordinateUtils.ConditionHA(lst - raHours);
        var alt = SiteContext.AltitudeDegrees(lat, ha, decDeg);
        var az = SiteContext.AzimuthDegrees(lat, ha, decDeg);
        var airmass = double.IsNaN(alt) ? double.NaN : SiteContext.AirmassFromAltitude(alt);
        var parallactic = double.IsNaN(ha) || double.IsNaN(decDeg) || double.IsNaN(lat) ? double.NaN : CoordinateUtils.ParallacticAngleDeg(ha, decDeg, lat);

        var moonAlt = double.NaN;
        var moonIllumination = double.NaN;
        var moonSeparation = double.NaN;
        var moonRaOut = double.NaN;
        var moonDecOut = double.NaN;
        if (hasEpoch && !double.IsNaN(lat) && !double.IsNaN(lon)
            && VSOP87a.Reduce(CatalogIndex.Moon, epoch, lat, lon, out var moonRa, out var moonDec, out _, out var moonAltitude, out _))
        {
            moonAlt = moonAltitude;
            moonRaOut = moonRa;
            moonDecOut = moonDec;
            moonIllumination = MeeusMoon.GetPhase(epoch.ToJulian()).Illumination;
            if (double.IsFinite(raHours) && double.IsFinite(decDeg))
            {
                moonSeparation = CoordinateUtils.AngularSeparationDeg(raHours, decDeg, moonRa, moonDec);
            }
        }

        // Mean solar time at the longitude is UTC plus longitude / 15 hours; local midnight is 0 h of it.
        var fromMidnight = double.NaN;
        if (hasEpoch && double.IsFinite(lon))
        {
            var local = ((epoch.UtcDateTime.TimeOfDay.TotalHours + lon / 15.0) % 24.0 + 24.0) % 24.0;
            fromMidnight = local >= 12.0 ? local - 24.0 : local;
        }

        return new SkyCovariates(raHours, decDeg, ha, alt, az, airmass, ha > 0, parallactic,
            moonAlt, moonIllumination, moonSeparation, moonRaOut, moonDecOut, fromMidnight);
    }
}
