using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    internal async ValueTask<DateTime> GetMountUtcNowAsync(CancellationToken cancellationToken)
        => await Setup.Mount.Driver.TryGetUTCDateFromMountAsync(cancellationToken) ?? _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Resolves refraction inputs (site pressure + temperature) for transforms built on the
    /// session hot path: a connected weather device supplies the live values, else the standard
    /// atmosphere (pressure auto-derived from the site's static elevation). These are varying
    /// environmental values, so they are read live here rather than stored. Passed to
    /// <see cref="Devices.IMountDriver.TryGetTransformAsync(SiteConditions, CancellationToken)"/>.
    /// </summary>
    private SiteConditions ResolveSiteConditions()
        => SiteConditions.Resolve(Setup.Weather?.Driver is { Connected: true } weather ? weather : null);

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
        var utcNow = _timeProvider.GetUtcNow();
        var diff = waitUntil - utcNow;

        _logger.LogInformation("WaitForDark: utcNow={UtcNow}, firstObservationStart={FirstStart}, waitUntil={WaitUntil}, diff={Diff}",
            utcNow, firstStart, waitUntil, diff);

        if (diff > TimeSpan.Zero)
        {
            _logger.LogInformation("Waiting {Diff} until 10 minutes before first observation at {FirstStart}",
                diff, firstStart);
            await _timeProvider.SleepAsync(diff, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation("First observation at {FirstStart} already started or starting soon (diff={Diff})",
                firstStart, diff);
        }
    }

    /// <summary>
    /// Outcome of <see cref="WaitForScheduledStartAsync"/>.
    /// </summary>
    internal enum ScheduledStartOutcome
    {
        /// <summary>The scheduled start (minus lead) was reached normally; proceed with the slew.</summary>
        Proceed,
        /// <summary>The observation is already behind schedule (start in the past); proceeding immediately.</summary>
        StartedLate,
        /// <summary>The lead-adjusted start lies beyond the session end; the observation should be skipped.</summary>
        SessionEnded
    }

    /// <summary>
    /// Waits until <c>observation.Start - lead</c> before returning, so the scheduler's
    /// altitude-optimised slot allocation actually begins slewing at the allocated time
    /// instead of the loop advancing linearly. The lead (<see cref="SessionConfiguration.ScheduledStartLeadTime"/>,
    /// default 3 min) covers slew + centering + guider settle so the first light frame lands near
    /// <see cref="ScheduledObservation.Start"/>.
    /// </summary>
    /// <remarks>
    /// Same-Start / past-Start schedules (hosted API stamping <c>Start = now</c>, legacy callers,
    /// and every existing test) short-circuit at the first branch, so current behaviour is
    /// preserved exactly for those paths. Uses <see cref="GetMountUtcNowAsync"/> -- the same clock
    /// the observation-loop condition uses -- not <c>_timeProvider.GetUtcNow()</c>, so the two never
    /// disagree. Sleeps in &lt;= 1 min chunks so cancellation stays responsive and the fake-time
    /// pump in tests advances it naturally; cancellation unwinds via <see cref="OperationCanceledException"/>
    /// like every other <c>SleepAsync</c> call site.
    /// </remarks>
    internal async ValueTask<ScheduledStartOutcome> WaitForScheduledStartAsync(
        ScheduledObservation observation, DateTime sessionEndTime, CancellationToken cancellationToken)
    {
        var lead = Configuration.ScheduledStartLeadTime ?? SessionConfiguration.DefaultScheduledStartLeadTime;
        var startUtc = observation.Start.UtcDateTime;
        var waitUntil = startUtc - lead;
        var now = await GetMountUtcNowAsync(cancellationToken);

        // Start already reached (or in the past): preserves the linear-advance behaviour exactly
        // for same-Start / hosted-API / legacy schedules, which all hit this branch.
        if (now >= waitUntil)
        {
            if (now > startUtc)
            {
                _logger.LogInformation(
                    "Observation {Target} scheduled start {Start:o} is {Late} in the past; proceeding immediately (running behind schedule).",
                    observation.Target, observation.Start, now - startUtc);
                return ScheduledStartOutcome.StartedLate;
            }

            return ScheduledStartOutcome.Proceed;
        }

        // The lead-adjusted start lies beyond the session end -> nothing to image for this slot.
        if (waitUntil >= sessionEndTime)
        {
            _logger.LogWarning(
                "Observation {Target} scheduled start {Start:o} (lead {Lead}) is at or beyond session end {SessionEnd:o}; skipping.",
                observation.Target, observation.Start, lead, sessionEndTime);
            return ScheduledStartOutcome.SessionEnded;
        }

        _logger.LogInformation(
            "Waiting {Wait} until scheduled start {Start:o} (lead {Lead}) of {Target}.",
            waitUntil - now, observation.Start, lead, observation.Target);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            now = await GetMountUtcNowAsync(cancellationToken);
            var remaining = waitUntil - now;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var chunk = remaining < TimeSpan.FromMinutes(1) ? remaining : TimeSpan.FromMinutes(1);
            await _timeProvider.SleepAsync(chunk, cancellationToken).ConfigureAwait(false);
        }

        // Pathological overshoot: only reachable with a clock that jumps a whole slot in one chunk.
        var startOfImaging = await GetMountUtcNowAsync(cancellationToken);
        if (startOfImaging > startUtc + observation.Duration)
        {
            _logger.LogWarning(
                "Overslept the scheduled slot of {Target}: woke at {Now:o}, slot ended {SlotEnd:o}; proceeding late.",
                observation.Target, startOfImaging, observation.Start + observation.Duration);
            return ScheduledStartOutcome.StartedLate;
        }

        return ScheduledStartOutcome.Proceed;
    }

    internal async ValueTask<DateTime> SessionEndTimeAsync(DateTime startTime, CancellationToken cancellationToken)
    {
        if (await Setup.Mount.Driver.TryGetTransformAsync(ResolveSiteConditions(), cancellationToken) is not { } transform)
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
