using System;
using System.Globalization;
using TianWen.Lib.Astrometry.MeteorShowers;
using TianWen.Lib.Astrometry.SOFA;

namespace TianWen.UI.Abstractions;

/// <summary>
/// What a shower's rate and peak are SAID as, in one place: the night calendar's tooltip and the atlas's info panel read the same
/// words, so the two cannot drift.
/// </summary>
internal static class MeteorShowerText
{
    /// <summary>The rate expected at the site on a night, in words.</summary>
    internal static string Rate(MeteorShowerNight night)
        => double.IsNaN(night.ExpectedRate) ? "rate varies"
            : night.MeanSinAltitude <= 0.0 ? "radiant not up in the dark"
            : $"about {Math.Max(1, (int)Math.Round(night.ExpectedRate))} an hour here";

    /// <summary>The shower's own ZHR, or "ZHR varies" where the working list says so.</summary>
    internal static string Zhr(MeteorShower shower)
        => double.IsNaN(shower.Zhr) ? "ZHR varies" : $"ZHR {shower.Zhr:0}";

    /// <summary>The peak nearest <paramref name="viewingUtc"/> in the site's time, or empty when it cannot be solved.</summary>
    internal static string Peak(MeteorShower shower, DateTimeOffset viewingUtc, double siteLat, double siteLon)
    {
        if (MeteorShowerCalendar.PeakNear(shower, viewingUtc) is not { } peak
            || !Transform.TryGetUtcOffset(siteLat, siteLon, peak.UtcDateTime, out var offset))
        {
            return "";
        }

        return "peak " + peak.ToOffset(offset).ToString("d MMM HH:mm", CultureInfo.InvariantCulture) + " site time";
    }
}
