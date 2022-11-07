using System;
using System.Collections.Generic;

namespace Astap.Lib.Astrometry.NOVA;

public interface IAstroUtils : IDisposable
{
    /// <summary>
    ///
    /// </summary>
    /// <param name="eventType"></param>
    /// <param name="dto"></param>
    /// <param name="siteLatitude"></param>
    /// <param name="siteLongitude"></param>
    /// <returns></returns>
    /// <exception cref="InvalidCastException"></exception>
    (bool aboveHorizon, IReadOnlyList<DateTimeOffset> riseEvents, IReadOnlyList<DateTimeOffset> setEvents)? EventTimes(EventType eventType, DateTimeOffset dto, double siteLatitude, double siteLongitude);
}
