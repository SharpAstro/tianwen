using System;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Astrometry.MeteorShowers;

/// <summary>
/// One shower on one night at a site: where its radiant is that night and what rate to expect (docs/plans/yearly-planner.md, E).
/// </summary>
/// <param name="Shower">The shower.</param>
/// <param name="RadiantRaDeg">The radiant's right ascension that night: the table's, moved by the drift, degrees.</param>
/// <param name="RadiantDecDeg">The radiant's declination that night, degrees.</param>
/// <param name="MeanSinAltitude">The mean of the sine of the radiant's altitude over the dark window, with a radiant below the
/// horizon counting zero. 0 when the radiant never clears the horizon while it is dark.</param>
/// <param name="LimitingMagnitude">The limiting magnitude the rate was scaled to, 6.5 under a sky without the Moon.</param>
/// <param name="ExpectedRate">The expected hourly rate for a visual observer: the ZHR, times <see cref="MeanSinAltitude"/>, times
/// r to the power (limiting magnitude minus 6.5). NaN where the ZHR is variable.</param>
/// <param name="Peak">The peak of this return, UTC (the one nearest the night).</param>
/// <param name="PeakOffset">The night's middle minus the peak: negative before it, positive after.</param>
public readonly record struct MeteorShowerNight(
    MeteorShower Shower,
    double RadiantRaDeg,
    double RadiantDecDeg,
    double MeanSinAltitude,
    double LimitingMagnitude,
    double ExpectedRate,
    DateTimeOffset? Peak,
    TimeSpan PeakOffset)
{
    /// <summary>
    /// Whether this is the night of the peak, or the nearest one to it: the peak is within half a day of the night's middle. A
    /// peak that falls in daylight at the site is not hidden, it is said as the nearest night's rate.
    /// </summary>
    public bool IsPeakNight => Peak is not null && Math.Abs(PeakOffset.TotalHours) <= 12.0;
}

/// <summary>
/// The showers active on a night at a site: the ONE routine every surface asks (the planner, the night calendar, the atlas).
/// It reuses <see cref="NightSummary"/>, so the dark window and the Moon are the night calendar's and nothing is computed twice.
/// </summary>
public static class MeteorShowerNights
{
    /// <summary>The limiting magnitude of a dark sky with no Moon, the reference of the ZHR.</summary>
    public const double ReferenceLimitingMagnitude = 6.5;

    /// <summary>
    /// How much a Moon that is full and up all night lowers the limiting magnitude. A working figure (a full Moon costs a
    /// visual observer one to two magnitudes), scaled by the illuminated fraction and by the share of the dark window with the
    /// Moon up; not a fit to observations.
    /// </summary>
    public const double FullMoonMagnitudeLoss = 1.8;

    /// <summary>
    /// The limiting magnitude under the Moon of <paramref name="night"/>: <see cref="ReferenceLimitingMagnitude"/> lowered by
    /// <see cref="FullMoonMagnitudeLoss"/> times the illuminated fraction times the share of the dark window the Moon is up.
    /// </summary>
    public static double LimitingMagnitude(in NightSummary night)
    {
        var dark = night.Dark;
        var moonUp = dark > TimeSpan.Zero
            ? Math.Clamp(1.0 - (night.MoonFreeDark / dark), 0.0, 1.0)
            : 0.0;
        return ReferenceLimitingMagnitude - (FullMoonMagnitudeLoss * Math.Clamp(night.MoonIllumination, 0.0, 1.0) * moonUp);
    }

    /// <summary>
    /// The showers active on <paramref name="night"/> at the site (<paramref name="latitude"/>, <paramref name="longitude"/>,
    /// degrees, east positive), each with its radiant moved by the drift to that night and its expected hourly rate. A night
    /// outside every activity window gives an empty array. Activity is judged at the middle of the dark window.
    /// </summary>
    public static ImmutableArray<MeteorShowerNight> For(double latitude, double longitude, in NightSummary night)
    {
        var mid = night.DarkStart + (night.Dark / 2);
        var sun = MeteorShowerCalendar.SunLongitude(mid);
        if (double.IsNaN(sun))
        {
            return [];
        }

        var limiting = LimitingMagnitude(night);
        var builder = ImmutableArray.CreateBuilder<MeteorShowerNight>();
        foreach (var shower in MeteorShowerTable.All)
        {
            if (!shower.IsActiveAt(sun))
            {
                continue;
            }

            // Days from the peak, by the Sun's longitude (a day is about a degree), so the drift needs no calendar date.
            var days = Wrap180(sun - shower.PeakLambda) * 365.2422 / 360.0;
            var ra = MeteorShowerCalendar.Wrap360(shower.RadiantRaDeg + (shower.DriftRaDegPerDay * days));
            var dec = Math.Clamp(shower.RadiantDecDeg + (shower.DriftDecDegPerDay * days), -90.0, 90.0);

            var meanSin = MeanSinAltitude(latitude, longitude, ra, dec, night);
            var rate = shower.Zhr * meanSin * Math.Pow(shower.PopulationIndex, limiting - ReferenceLimitingMagnitude);
            var peak = MeteorShowerCalendar.PeakNear(shower, mid);
            builder.Add(new MeteorShowerNight(shower, ra, dec, meanSin, limiting, rate, peak,
                peak is { } p ? mid - p : TimeSpan.Zero));
        }

        return builder.ToImmutable();
    }

    /// <summary>The mean of max(0, sin altitude) of a fixed (RA, Dec) over the night's dark window, sampled as the night calendar samples it.</summary>
    private static double MeanSinAltitude(double latitude, double longitude, double raDeg, double decDeg, in NightSummary night)
    {
        var end = night.DarkEnd;
        var sum = 0.0;
        var count = 0;
        for (var t = night.DarkStart; t < end; t += NightSummary.Step)
        {
            var slice = end - t < NightSummary.Step ? end - t : NightSummary.Step;
            var sample = t + (slice / 2);
            var hourAngle = SiteContext.ComputeLST(sample.ToUniversalTime(), longitude) - (raDeg / 15.0);
            var altitude = SiteContext.AltitudeDegrees(latitude, hourAngle, decDeg);
            sum += Math.Max(0.0, Math.Sin(altitude * Math.PI / 180.0));
            count++;
        }

        return count > 0 ? sum / count : 0.0;
    }

    private static double Wrap180(double degrees) => MeteorShowerCalendar.Wrap360(degrees + 180.0) - 180.0;
}
