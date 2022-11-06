using System;
using System.Collections;
using System.Collections.Generic;

namespace Astap.Lib.Astrometry.Ascom;

public class AstroUtils : DynamicComObject
{
    public AstroUtils() : base("ASCOM.Astrometry.AstroUtils.AstroUtils") { }

    public (bool aboveHorizon, IReadOnlyList<DateTimeOffset> riseEvents, IReadOnlyList<DateTimeOffset> setEvents)? EventTimes(EventType eventType, DateTimeOffset dto, double SiteLatitude, double SiteLongitude)
    {
        var result = _comObject?.EventTimes(eventType, dto.Day, dto.Month, dto.Year, SiteLatitude, SiteLongitude, dto.Offset.TotalHours);
        if (result?.Count >= 2)
        {
            int resIdx = 0;
            var aboveHorizon = result[resIdx++] is bool idx0bool && idx0bool;
            var numberOfRiseEvents = result[resIdx++] is int idx1int ? idx1int : 0;
            var numberOfSetEvents = result[resIdx++] is int idx2int ? idx2int : 0;

            var riseEvents = new DateTimeOffset[numberOfRiseEvents];
            var setEvents = new DateTimeOffset[numberOfSetEvents];

            for (var i = 0; i < numberOfRiseEvents; i++)
            {
                if (result[resIdx++] is double hours)
                {
                    var ts = TimeSpan.FromHours(hours);
                    riseEvents[i] = new DateTimeOffset(dto.Year, dto.Month, dto.Day, ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds, dto.Offset);
                }
                else
                {
                    throw new InvalidCastException($"Cannot cast rise event time {result[resIdx - 1]} to double");
                }
            }

            for (var i = 0; i < numberOfSetEvents; i++)
            {
                if (result[resIdx++] is double hours)
                {
                    var ts = TimeSpan.FromHours(hours);
                    setEvents[i] = new DateTimeOffset(dto.Year, dto.Month, dto.Day, ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds, dto.Offset);
                }
                else
                {
                    throw new InvalidCastException($"Cannot cast set event time {result[resIdx - 1]} to double");
                }
            }

            return (aboveHorizon, riseEvents, setEvents);
        }

        return null;
    }
}
