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
    public static CatalogIndex CelestialBody(this EventType eventType)
        => eventType switch
        {
            EventType.SunRiseSunset or EventType.CivilTwilight or EventType.NauticalTwilight or EventType.AmateurAstronomicalTwilight or EventType.AstronomicalTwilight => CatalogIndex.Sol,
            EventType.MoonRiseMoonSet => CatalogIndex.Moon,
            EventType.MercuryRiseSet => CatalogIndex.Mercury,
            EventType.VenusRiseSet => CatalogIndex.Venus,
            EventType.MarsRiseSet => CatalogIndex.Mars,
            EventType.JupiterRiseSet => CatalogIndex.Jupiter,
            EventType.SaturnRiseSet => CatalogIndex.Saturn,
            EventType.UranusRiseSet => CatalogIndex.Uranus,
            EventType.NeptuneRiseSet => CatalogIndex.Neptune,
            _ => throw new ArgumentException($"No celestial body defined for {eventType}", nameof(eventType))
        };
}