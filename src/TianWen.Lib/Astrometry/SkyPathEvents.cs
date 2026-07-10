using System;
using System.Collections.Generic;

namespace TianWen.Lib.Astrometry;

/// <summary>The kind of notable event along a solar-system object's apparent sky path.</summary>
public enum SkyPathEventKind
{
    /// <summary>Apparent motion turns from direct (eastward, RA increasing) to retrograde: a station.</summary>
    StationRetrograde,
    /// <summary>Apparent motion turns from retrograde back to direct: a station.</summary>
    StationDirect,
    /// <summary>Greatest (max) angular separation from the Sun -- for an inferior planet (Mercury/Venus).</summary>
    GreatestElongation,
    /// <summary>Elongation extremum near 180 deg -- an outer planet opposite the Sun.</summary>
    Opposition,
    /// <summary>Closest approach to the Sun -- for a comet, from its element perihelion time.</summary>
    Perihelion,
}

/// <summary>A detected event at a specific sampled point on the path.</summary>
public readonly record struct SkyPathEvent(
    SkyPathEventKind Kind, double RaJ2000Hours, double DecJ2000Deg, DateTimeOffset TimeUtc, string Label);

/// <summary>Classifies the selected body so the detector knows which events apply.</summary>
public enum SkyPathBody
{
    /// <summary>Sun / Moon / anything without a meaningful elongation or perihelion event.</summary>
    Other,
    /// <summary>Mercury or Venus -- greatest-elongation events.</summary>
    InferiorPlanet,
    /// <summary>Mars..Neptune -- opposition events.</summary>
    OuterPlanet,
    /// <summary>A comet -- perihelion event.</summary>
    Comet,
}

/// <summary>
/// Detects notable events along an already-sampled apparent sky path (stations / retrograde, greatest
/// elongation, opposition, perihelion), purely from the sampled positions -- no ephemeris service call.
/// Stations come from sign reversals of the apparent RA rate; elongation extrema from the angular
/// separation to a supplied Sun track; perihelion from the comet's element perihelion time. The sky map
/// annotates a selected object's path with these; the detector is pure so it is unit-testable.
/// </summary>
public static class SkyPathEventDetector
{
    /// <summary>
    /// Appends the events found along <paramref name="path"/> (evenly spaced from <paramref name="start"/>
    /// by <paramref name="step"/>) to <paramref name="results"/>. <paramref name="sun"/> is the Sun's track
    /// at the same sample times (empty to skip elongation/opposition). <paramref name="perihelion"/> is the
    /// comet perihelion instant (null to skip). The list is cleared first.
    /// </summary>
    public static void Detect(
        ReadOnlySpan<(double RA, double Dec)> path,
        ReadOnlySpan<(double RA, double Dec)> sun,
        DateTimeOffset start,
        TimeSpan step,
        SkyPathBody body,
        DateTimeOffset? perihelion,
        List<SkyPathEvent> results)
    {
        results.Clear();
        var n = path.Length;
        if (n < 3)
        {
            return;
        }

        // Stations: the apparent RA rate (signed, wrapped to +/-12 h) reverses sign. Direct motion is RA
        // increasing (eastward); retrograde is RA decreasing. A +/- reversal is a station then retrograde;
        // a -/+ reversal is a station then direct.
        for (var i = 1; i < n - 1; i++)
        {
            var prevRate = CoordinateUtils.ConditionHA(path[i].RA - path[i - 1].RA);
            var nextRate = CoordinateUtils.ConditionHA(path[i + 1].RA - path[i].RA);
            if (prevRate == 0.0 || nextRate == 0.0 || (prevRate > 0.0) == (nextRate > 0.0))
            {
                continue;
            }
            var retro = prevRate > 0.0; // was direct, turning retrograde
            results.Add(new SkyPathEvent(
                retro ? SkyPathEventKind.StationRetrograde : SkyPathEventKind.StationDirect,
                path[i].RA, path[i].Dec, start + step * i, retro ? "R" : "D"));
        }

        // Elongation extremum (needs the Sun track): interior local maximum of the Sun-object separation.
        // For an inferior planet that is greatest elongation; for an outer planet the max (~180 deg) is
        // opposition.
        if (sun.Length == n && body is SkyPathBody.InferiorPlanet or SkyPathBody.OuterPlanet)
        {
            var inferior = body == SkyPathBody.InferiorPlanet;
            for (var i = 1; i < n - 1; i++)
            {
                var e0 = CoordinateUtils.AngularSeparationDeg(sun[i - 1].RA, sun[i - 1].Dec, path[i - 1].RA, path[i - 1].Dec);
                var e1 = CoordinateUtils.AngularSeparationDeg(sun[i].RA, sun[i].Dec, path[i].RA, path[i].Dec);
                var e2 = CoordinateUtils.AngularSeparationDeg(sun[i + 1].RA, sun[i + 1].Dec, path[i + 1].RA, path[i + 1].Dec);
                if (e1 >= e0 && e1 >= e2 && (e1 > e0 || e1 > e2))
                {
                    results.Add(new SkyPathEvent(
                        inferior ? SkyPathEventKind.GreatestElongation : SkyPathEventKind.Opposition,
                        path[i].RA, path[i].Dec, start + step * i, inferior ? "GE" : "Opp"));
                }
            }
        }

        // Perihelion (comet): the sample nearest the element perihelion instant, if it falls in the window.
        if (body == SkyPathBody.Comet && perihelion is { } peri)
        {
            var end = start + step * (n - 1);
            if (peri >= start && peri <= end && step > TimeSpan.Zero)
            {
                var idx = (int)Math.Round((peri - start) / step);
                idx = Math.Clamp(idx, 0, n - 1);
                results.Add(new SkyPathEvent(
                    SkyPathEventKind.Perihelion, path[idx].RA, path[idx].Dec, start + step * idx, "q"));
            }
        }
    }
}
