using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    internal async ValueTask<DateTime> GetMountUtcNowAsync(CancellationToken cancellationToken)
        => await Setup.Mount.Driver.TryGetUTCDateFromMountAsync(cancellationToken) ?? External.TimeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Converts a UTC <see cref="DateTime"/> to a local <see cref="DateTimeOffset"/> at the site's timezone,
    /// then returns the start of that local day as a <see cref="DateTimeOffset"/>.
    /// </summary>
    private static DateTimeOffset LocalStartOfDay(DateTime utc, TimeSpan siteTimeZone)
    {
        var localDto = new DateTimeOffset(utc, TimeSpan.Zero).ToOffset(siteTimeZone);
        var localMidnight = localDto.Date; // DateTimeKind.Unspecified
        return new DateTimeOffset(localMidnight, siteTimeZone);
    }

    internal async ValueTask WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(CancellationToken cancellationToken)
    {
        if (await Setup.Mount.Driver.TryGetTransformAsync(cancellationToken) is not { } transform)
        {
            throw new InvalidOperationException("Failed to retrieve time transformation from mount");
        }

        var (_, _, set) = transform.EventTimes(Astrometry.SOFA.EventType.AmateurAstronomicalTwilight);
        if (set is { Count: 1 })
        {
            var now = External.TimeProvider.GetUtcNow().UtcDateTime;
            var localNow = new DateTimeOffset(now, TimeSpan.Zero).ToOffset(transform.SiteTimeZone);
            var localDayStart = LocalStartOfDay(now, transform.SiteTimeZone);
            var localAstroTwilightSet = localDayStart + set[0];
            var local10MinBeforeAstroTwilightSet = localAstroTwilightSet - TimeSpan.FromMinutes(10);
            var diff = local10MinBeforeAstroTwilightSet - now;

            External.AppLogger.LogDebug("WaitForDark: now={Now}, localDayStart={LocalDayStart}, set[0]={SetOffset}, twilightSet={TwilightSet}, 10minBefore={Before10}, diff={Diff}",
                now, localDayStart, set[0], localAstroTwilightSet, local10MinBeforeAstroTwilightSet, diff);

            if (diff > TimeSpan.Zero)
            {
                External.AppLogger.LogInformation("Current time {CurrentTimeLocal}, twilight ends {AmateurTwilightEndsLocal}, which is in {Diff}",
                    localNow, localAstroTwilightSet, diff);
                await External.SleepAsync(diff, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                External.AppLogger.LogWarning("Current time {CurrentTimeLocal}, twilight ends {AmateurTwilightEndsLocal}, ended {Diff} ago",
                    localNow, localAstroTwilightSet, -diff);
            }
        }
        else
        {
            throw new InvalidOperationException($"Failed to retrieve astro event time for {transform.DateTime}");
        }
    }

    internal async ValueTask<DateTime> SessionEndTimeAsync(DateTime startTime, CancellationToken cancellationToken)
    {
        if (await Setup.Mount.Driver.TryGetTransformAsync(cancellationToken) is not { } transform)
        {
            throw new InvalidOperationException("Failed to retrieve time transformation from mount");
        }

        // advance one day
        var nowPlusOneDay = transform.DateTime = startTime.AddDays(1);
        var (_, rise, _) = transform.EventTimes(Astrometry.SOFA.EventType.AstronomicalTwilight);

        if (rise is { Count: 1 })
        {
            var tomorrowLocalStart = LocalStartOfDay(nowPlusOneDay, transform.SiteTimeZone);
            return (tomorrowLocalStart + rise[0]).UtcDateTime;
        }
        else
        {
            throw new InvalidOperationException($"Failed to retrieve astro event time for {transform.DateTime}");
        }
    }


}
