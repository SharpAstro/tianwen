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
        // Wait until 10 minutes before the first scheduled observation starts.
        // The schedule already encodes the correct start times computed by the planner
        // using CalculateNightWindow, so we don't need to recompute twilight here.
        if (Observations.Count == 0)
        {
            return;
        }

        var firstStart = Observations[0].Start;
        var waitUntil = firstStart - TimeSpan.FromMinutes(10);
        var utcNow = External.TimeProvider.GetUtcNow();
        var diff = waitUntil - utcNow;

        External.AppLogger.LogInformation("WaitForDark: utcNow={UtcNow}, firstObservationStart={FirstStart}, waitUntil={WaitUntil}, diff={Diff}",
            utcNow, firstStart, waitUntil, diff);

        if (diff > TimeSpan.Zero)
        {
            External.AppLogger.LogInformation("Waiting {Diff} until 10 minutes before first observation at {FirstStart}",
                diff, firstStart);
            await External.SleepAsync(diff, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            External.AppLogger.LogInformation("First observation at {FirstStart} already started or starting soon (diff={Diff})",
                firstStart, diff);
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
