using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Astrometry.MeteorShowers;

/// <summary>
/// One shower of the IMO working list (docs/plans/yearly-planner.md, section E). A shower is an EVENT, not a catalogue
/// object: it is absent outside its activity window and is never in <c>ICelestialObjectDB</c>.
/// </summary>
/// <param name="Code">The IAU three-letter code (<c>PER</c>).</param>
/// <param name="IauNumber">The IAU Meteor Data Center's shower number (7 for the Perseids).</param>
/// <param name="Name">The name in ASCII, Greek letters spelt out (<c>Southern delta Aquariids</c>), so search finds it.</param>
/// <param name="ActivityStartLambda">The Sun's longitude (equinox J2000) at the start of the activity window, degrees.</param>
/// <param name="ActivityEndLambda">The Sun's longitude (equinox J2000) at the end of the activity window, degrees.</param>
/// <param name="PeakLambda">The Sun's longitude (equinox J2000) at the peak, degrees: what every year's peak is solved from.</param>
/// <param name="RadiantRaDeg">The radiant's right ascension at the peak, degrees.</param>
/// <param name="RadiantDecDeg">The radiant's declination at the peak, degrees.</param>
/// <param name="DriftRaDegPerDay">The radiant's daily drift in right ascension, degrees per day.</param>
/// <param name="DriftDecDegPerDay">The radiant's daily drift in declination, degrees per day.</param>
/// <param name="SpeedKmPerSecond">The atmospheric entry speed, km/s.</param>
/// <param name="PopulationIndex">The population index r.</param>
/// <param name="Zhr">The zenithal hourly rate at the peak, or <see cref="double.NaN"/> where the working list says "Var".</param>
public sealed record MeteorShower(
    string Code,
    int IauNumber,
    string Name,
    double ActivityStartLambda,
    double ActivityEndLambda,
    double PeakLambda,
    double RadiantRaDeg,
    double RadiantDecDeg,
    double DriftRaDegPerDay,
    double DriftDecDegPerDay,
    double SpeedKmPerSecond,
    double PopulationIndex,
    double Zhr)
{
    /// <summary>
    /// Whether the Sun at longitude <paramref name="sunLambda"/> (equinox J2000, degrees) is inside the activity window.
    /// A window may straddle longitude 0, so the test never assumes the start is the smaller number.
    /// </summary>
    public bool IsActiveAt(double sunLambda)
        => MeteorShowerCalendar.Wrap360(sunLambda - ActivityStartLambda)
            <= MeteorShowerCalendar.Wrap360(ActivityEndLambda - ActivityStartLambda);
}

/// <summary>
/// The shower table: the IMO Meteor Shower Calendar's Working List of Visual Meteor Showers (Table 5, "details correct
/// according to the best information available in June 2025", the 2026 calendar), with each radiant's daily drift read
/// off the calendar's Table 6 (radiant positions every five days), and the activity window written as the Sun's longitude.
/// </summary>
/// <remarks>
/// <para>Source: International Meteor Organization, "2026 Meteor Shower Calendar" (J. Rendtel, ed.), IMO INFO(3-25),
/// https://www.imo.net/files/meteor-shower/cal2026.pdf. The peak longitude, radiant, speed, r and ZHR are Table 5's;
/// the drift is the slope of Table 6's positions across the shower's activity; the activity dates of Table 5 were turned
/// into the Sun's longitude for 2026, which holds for every year to a fraction of a day.</para>
/// <para>Left out because the calendar's own tables disagree on the radiant, or the source has no point radiant: the
/// Antihelion Source (a diffuse ecliptic source), the kappa Cygnids, the eta Eridanids and the September Lyncids. A table
/// with a wrong radiant is worse than one without the shower.</para>
/// <para>The longitudes are the IMO's, referred to the equinox of J2000 (checked against the calendar's own stated peaks:
/// the Quadrantids' 283.15 degrees on 2026 January 3 at 21h UT).</para>
/// </remarks>
public static class MeteorShowerTable
{
    /// <summary>Every shower in the table, in the order of the working list.</summary>
    public static ImmutableArray<MeteorShower> All { get; } =
    [
        new("QUA", 10, "Quadrantids", 276.1, 292.4, 283.15, 230, 49, 0.6, -0.2, 41, 2.1, 80),
        new("GUM", 404, "gamma Ursae Minorids", 289.4, 302.6, 298, 228, 67, 0.8, -0.4, 31, 3.0, 3),
        new("ACE", 102, "alpha Centaurids", 310.7, 332.0, 319.4, 211, -58, 1.2, -0.3, 58, 2.0, 6),
        new("LYR", 6, "April Lyrids", 23.7, 40.3, 32.32, 271, 34, 1.1, 0.0, 49, 2.1, 18),
        new("PPU", 137, "pi Puppids", 24.7, 38.3, 33.5, 110, -45, 0.5, -0.1, 18, 2.0, double.NaN),
        new("ETA", 31, "eta Aquariids", 28.6, 67.3, 45.5, 338, -1, 0.9, 0.4, 66, 2.4, 50),
        new("ELY", 145, "eta Lyrids", 42.2, 53.8, 50.0, 291, 43, 1.0, 0.1, 43, 3.0, 3),
        new("ARI", 171, "Daytime Arietids", 52.9, 93.1, 76.7, 43, 24, 1.0, 0.0, 38, 2.8, 30),
        new("JBO", 170, "June Bootids", 90.3, 100.7, 90.3, 221, 48, 0.2, -0.2, 18, 2.2, double.NaN),
        new("JPE", 175, "July Pegasids", 98.8, 117.9, 108.0, 347, 11, 0.9, 0.2, 63, 3.0, 3),
        new("GDR", 184, "July gamma Draconids", 121.7, 128.4, 125.13, 280, 51, 1.0, 0.0, 27, 3.0, 5),
        new("SDA", 5, "Southern delta Aquariids", 109.3, 150.5, 128, 340, -16, 0.8, 0.2, 41, 2.5, 25),
        new("CAP", 1, "alpha Capricornids", 100.7, 142.8, 128, 307, -10, 0.9, 0.3, 23, 2.5, 5),
        new("PER", 7, "Perseids", 114.1, 151.5, 140.0, 48, 58, 1.4, 0.2, 59, 2.2, 100),
        new("AUR", 206, "Aurigids", 154.4, 163.1, 158.6, 91, 39, 1.2, 0.0, 66, 2.5, 6),
        new("SPE", 208, "September epsilon Perseids", 162.1, 178.6, 166.7, 48, 40, 1.1, 0.1, 64, 2.5, 8),
        new("DSX", 221, "Daytime Sextantids", 176.7, 193.4, 188, 156, -2, 0.8, 0.0, 32, 2.5, 5),
        new("OCT", 281, "October Camelopardalids", 191.4, 193.4, 192.58, 164, 79, 0.0, 0.0, 47, 2.5, 5),
        new("DRA", 9, "Draconids", 192.4, 197.3, 195.4, 262, 54, 0.0, 0.0, 20, 2.6, 5),
        new("EGE", 23, "epsilon Geminids", 200.3, 214.2, 205, 102, 27, 1.0, 0.0, 70, 3.0, 3),
        new("ORI", 8, "Orionids", 188.4, 225.2, 208, 95, 16, 0.7, 0.1, 66, 2.5, 20),
        new("LMI", 22, "Leonis Minorids", 205.2, 214.2, 211, 162, 37, 1.0, -0.4, 62, 3.0, 2),
        new("STA", 2, "Southern Taurids", 176.7, 238.3, 223, 52, 15, 0.8, 0.2, 27, 2.3, 7),
        new("NTA", 17, "Northern Taurids", 206.2, 258.6, 230, 58, 22, 0.9, 0.2, 29, 2.3, 5),
        new("LEO", 13, "Leonids", 223.2, 248.4, 235.27, 152, 22, 0.6, -0.25, 71, 2.5, 15),
        new("AMO", 246, "alpha Monocerotids", 232.3, 243.4, 239.32, 117, 1, 0.8, -0.2, 65, 2.4, double.NaN),
        new("NOO", 250, "November Orionids", 230.2, 254.5, 246, 91, 16, 0.7, 0.0, 44, 3.0, 3),
        new("PHO", 254, "Phoenicids", 248.4, 253.5, 249.5, 8, -27, 0.6, -0.1, 15, 2.8, double.NaN),
        new("PUP", 301, "Puppid-Velids", 248.4, 263.6, 255, 123, -45, 0.6, 0.0, 44, 2.9, 10),
        new("MON", 19, "Monocerotids", 248.4, 267.7, 257, 100, 8, 0.7, -0.1, 41, 3.0, 3),
        new("HYD", 16, "sigma Hydrids", 250.4, 268.7, 257, 125, 2, 0.8, -0.2, 58, 3.0, 7),
        new("GEM", 4, "Geminids", 251.5, 268.7, 262.2, 112, 33, 1.0, -0.07, 35, 2.6, 150),
        new("COM", 20, "Comae Berenicids", 251.7, 310.7, 271, 164, 29, 0.9, -0.35, 65, 3.0, 3),
        new("URS", 15, "Ursids", 264.7, 274.8, 270.7, 217, 76, 0.0, -0.4, 33, 2.8, 10),
    ];

    /// <summary>The shower with the IAU code <paramref name="code"/>, or null.</summary>
    public static MeteorShower? ByCode(string code)
    {
        foreach (var shower in All)
        {
            if (string.Equals(shower.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                return shower;
            }
        }

        return null;
    }
}
