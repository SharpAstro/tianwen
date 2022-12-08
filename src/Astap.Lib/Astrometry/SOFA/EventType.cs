using Astap.Lib.Astrometry.Catalogs;
using System;

namespace Astap.Lib.Astrometry.SOFA;

public enum EventType
{
    SunRiseSunset = 0,
    MoonRiseMoonSet = 1,
    CivilTwilight = 2,
    NauticalTwilight = 3,
    AmateurAstronomicalTwilight = 4,
    AstronomicalTwilight = 5,
    MercuryRiseSet = 6,
    VenusRiseSet = 7,
    MarsRiseSet = 8,
    JupiterRiseSet = 9,
    SaturnRiseSet = 10,
    UranusRiseSet = 11,
    NeptuneRiseSet = 12,
}

public static class EventTypeEx
{
    public static (CatalogIndex catIdx, double radius) CelestialBodyAndRadius(this EventType eventType)
        => eventType switch
        {
            EventType.SunRiseSunset or EventType.CivilTwilight or EventType.NauticalTwilight or EventType.AmateurAstronomicalTwilight or EventType.AstronomicalTwilight => (CatalogIndex.Sol, Constants.SUN_RADIUS),
            EventType.MoonRiseMoonSet => (CatalogIndex.Moon, Constants.MOON_RADIUS),
            EventType.MercuryRiseSet => (CatalogIndex.Mercury, Constants.MERCURY_RADIUS),
            EventType.VenusRiseSet => (CatalogIndex.Venus, Constants.VENUS_RADIUS),
            EventType.MarsRiseSet => (CatalogIndex.Mars, Constants.MARS_RADIUS),
            EventType.JupiterRiseSet => (CatalogIndex.Jupiter, Constants.JUPITER_RADIUS),
            EventType.SaturnRiseSet => (CatalogIndex.Saturn, Constants.SATURN_RADIUS),
            EventType.UranusRiseSet => (CatalogIndex.Uranus, Constants.URANUS_RADIUS),
            EventType.NeptuneRiseSet => (CatalogIndex.Neptune, Constants.NEPTUNE_RADIUS),
            _ => throw new ArgumentException($"No celestial body defined for {eventType}", nameof(eventType))
        };
}