using System;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>How far a sequence step lets a planet turn (#1340), as the light beside the step shows it.</summary>
public enum PlanetaryTurnLight
{
    /// <summary>No planet, no turn model or no step length known: nothing to say.</summary>
    Unknown,

    /// <summary>Under a pixel at the disk's middle: the file needs no de-rotation at all.</summary>
    Green,

    /// <summary>Past a pixel, under half the limit: the stack's de-rotation carries it easily.</summary>
    Yellow,

    /// <summary>Past half the limit, under it: the de-rotation still carries it, with less in hand.</summary>
    Orange,

    /// <summary>Past the limit: shorten the step.</summary>
    Red,
}

/// <summary>
/// A planet's turn over a span of time, in pixels at the middle of its disk, worked out from the ephemeris before any frame is
/// taken (#1340): the same quantity the stack measures after the fact (<c>FrameDerotator.TurnAtCentrePx</c>), the central
/// meridian's change times the disk's radius and the cosine of the latitude it is seen from, the radius from the planet's
/// angular size and the pixel scale rather than a limb fit.
/// </summary>
public static class PlanetaryTurn
{
    /// <summary>A turn under this needs no de-rotation (<see cref="PlanetaryBestStack.TurnWorthDerotatingPx"/>).</summary>
    public const double GreenBelowPx = PlanetaryBestStack.TurnWorthDerotatingPx;

    /// <summary>
    /// The turn past which a step is too long (#1340). PROVISIONAL: the issue sets it from the twins' de-rotation error, which is
    /// not measured yet, and the owner confirms it; until then a turn of ten pixels at the disk's middle.
    /// </summary>
    public const double RedFromPx = 10;

    /// <summary>The planets whose turn a stack de-rotates, which a sequence's light can speak for.</summary>
    public static bool Speaks(CatalogIndex planet) => PhysicalEphemeris.SupportsPlanetaryStacking(planet);

    /// <summary>
    /// How far <paramref name="planet"/>'s disk middle moves over <paramref name="span"/> from <paramref name="from"/>, in pixels at
    /// <paramref name="pixelScaleArcsec"/>; NaN when it cannot be said (no rotation model, no pixel scale, no span).
    /// </summary>
    public static double TurnPx(CatalogIndex planet, DateTimeOffset from, TimeSpan span, double pixelScaleArcsec)
    {
        if (!Speaks(planet) || !(pixelScaleArcsec > 0) || !(span > TimeSpan.Zero))
        {
            return double.NaN;
        }
        var (start, end) = (PhysicalEphemeris.Compute(planet, from), PhysicalEphemeris.Compute(planet, from + span));
        var turnDeg = Math.Abs(Math.IEEERemainder(end.CentralMeridianIII - start.CentralMeridianIII, 360));
        var radiusPx = start.AngularDiameterArcsec / 2 / pixelScaleArcsec;
        return radiusPx * turnDeg * Math.PI / 180 * Math.Cos(start.SubObserverLatitudeCentric * Math.PI / 180);
    }

    /// <summary>The light for a turn of <paramref name="turnPx"/>.</summary>
    public static PlanetaryTurnLight LightFor(double turnPx) => turnPx switch
    {
        double.NaN => PlanetaryTurnLight.Unknown,
        < GreenBelowPx => PlanetaryTurnLight.Green,
        < RedFromPx / 2 => PlanetaryTurnLight.Yellow,
        < RedFromPx => PlanetaryTurnLight.Orange,
        _ => PlanetaryTurnLight.Red,
    };

    /// <summary>The longest step whose turn stays green, for the editor's suggestion; null when it cannot be said.</summary>
    public static TimeSpan? LongestGreen(CatalogIndex planet, DateTimeOffset from, double pixelScaleArcsec)
    {
        var perMinute = TurnPx(planet, from, TimeSpan.FromMinutes(1), pixelScaleArcsec);
        return double.IsFinite(perMinute) && perMinute > 0 ? TimeSpan.FromMinutes(GreenBelowPx / perMinute) : null;
    }
}
