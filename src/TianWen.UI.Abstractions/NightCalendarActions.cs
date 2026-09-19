using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Everything the night calendar DOES: which night is planned, the month grid, choosing a night, and the one
/// multi-day forecast behind both the calendar and the planner's own weather band (docs/plans/night-calendar.md).
/// </summary>
/// <remarks>
/// <b>The network is asked as little as possible.</b> One request per provider covers yesterday to the last day
/// Open-Meteo answers (<see cref="ExtendedForecast.RangeFor"/>), and it serves every night in that range: the
/// planner's band for the planned night is a SLICE of it, so stepping the date through the next two weeks asks
/// nothing. The forecast is kept in memory and reused for <see cref="ForecastReuse"/>, the drivers' own file-cache
/// lifetime, so a recompute does not even read the cache file. A night past the horizon asks nothing at all (the
/// provider could only refuse it), and only a night BEFORE the range, which someone stepped back to, gets the
/// single-night request the planner has always made.
/// </remarks>
public static class NightCalendarActions
{
    /// <summary>How long a fetched forecast is reused before asking the drivers again: their own cache lifetime.</summary>
    public static readonly TimeSpan ForecastReuse = TimeSpan.FromHours(1);

    /// <summary>A month grid is six weeks, so every month fits whatever weekday it starts on.</summary>
    public const int GridDays = 42;

    /// <summary>
    /// The night being planned, by its evening date: the pinned date's own date (the planner's recompute and its
    /// persistence both read it that way), or tonight's evening in the site's time zone.
    /// </summary>
    public static DateOnly PlanningEveningDate(PlannerState state, ITimeProvider timeProvider)
        => DateOnly.FromDateTime(state.PlanningDate?.Date
            ?? CoordinateUtils.AstronomicalEveningDate(timeProvider.GetUtcNow().ToOffset(state.SiteTimeZone)));

    /// <summary>Tonight's evening date in the site's time zone.</summary>
    public static DateOnly TonightEveningDate(PlannerState state, ITimeProvider timeProvider)
        => DateOnly.FromDateTime(CoordinateUtils.AstronomicalEveningDate(timeProvider.GetUtcNow().ToOffset(state.SiteTimeZone)));

    /// <summary>
    /// The colour a verdict's WORD is written in, wherever a host writes it beside the date: the palette's success,
    /// warning and error for a forecast, body text for a dark night and dimmed for a moonlit one.
    /// </summary>
    public static RGBAColor32 VerdictColour(NightOutlook outlook) => outlook switch
    {
        NightOutlook.Go => GuiTheme.Palette.Success,
        NightOutlook.Marginal => GuiTheme.Palette.Warn,
        NightOutlook.NoGo => GuiTheme.Palette.Error,
        NightOutlook.Dark => GuiTheme.Palette.BodyText,
        _ => GuiTheme.Palette.DimText,
    };

    /// <summary>The first day of <paramref name="date"/>'s month.</summary>
    public static DateOnly FirstOfMonth(DateOnly date) => new DateOnly(date.Year, date.Month, 1);

    /// <summary>
    /// The <see cref="GridDays"/> nights a month's grid shows: from the <paramref name="firstDayOfWeek"/> on or
    /// before the 1st, so the grid's leading and trailing cells are the neighbouring months' nights.
    /// </summary>
    public static ImmutableArray<DateOnly> MonthGrid(DateOnly month, DayOfWeek firstDayOfWeek)
    {
        var first = FirstOfMonth(month);
        var lead = ((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        var start = first.AddDays(-lead);
        var days = ImmutableArray.CreateBuilder<DateOnly>(GridDays);
        for (var i = 0; i < GridDays; i++)
        {
            days.Add(start.AddDays(i));
        }
        return days.MoveToImmutable();
    }

    /// <summary>The week's first day where the user is, which is where a calendar should start its rows.</summary>
    public static DayOfWeek FirstDayOfWeek => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    /// <summary>
    /// Plans the night beginning on <paramref name="evening"/>, keeping the time of day the planner was looking at
    /// (the sky map shows that instant), and closes the calendar. Tonight is un-pinned rather than pinned to
    /// today, so the planner follows the clock again exactly as the old reset-to-tonight did.
    /// </summary>
    public static void PickNight(PlannerState state, DateOnly evening, ITimeProvider timeProvider, SkyMapState? skyMap = null)
    {
        state.Calendar.Popover.Close();

        if (evening == TonightEveningDate(state, timeProvider))
        {
            PlannerActions.ResetPlanningDate(state);
        }
        else
        {
            var current = state.PlanningDate ?? timeProvider.GetUtcNow().ToOffset(state.SiteTimeZone);
            state.PlanningDate = new DateTimeOffset(evening.ToDateTime(TimeOnly.FromTimeSpan(current.TimeOfDay)), current.Offset);
            state.NeedsRecompute = true;
            state.NeedsRedraw = true;
        }

        // A scrubbed sky map would otherwise sit on a different night from the one just chosen.
        if (skyMap is not null)
        {
            skyMap.TimeOffset = TimeSpan.Zero;
        }
    }

    /// <summary>Moves the calendar by <paramref name="months"/>.</summary>
    public static void ShiftMonth(NightCalendarState calendar, int months)
        => calendar.Month = FirstOfMonth(calendar.Month).AddMonths(months);

    /// <summary>
    /// Readies the calendar to open on the planned night: its month is chosen at the first paint and the keyboard
    /// cursor starts there. Every trigger calls this before opening it, so a reopened calendar never shows where
    /// the last one was left.
    /// </summary>
    public static void PrepareToOpen(NightCalendarState calendar)
    {
        calendar.Month = default;
        calendar.Cursor = null;
    }

    /// <summary>
    /// The calendar's keys while it is open (<c>PopoverState.ContentKeys</c>; Escape stays the popover's):
    /// <list type="bullet">
    /// <item><description>Left and Right move the cursor a night, Up and Down a week.</description></item>
    /// <item><description>PageUp and PageDown move it a month.</description></item>
    /// <item><description>Enter or Space plans the night under it.</description></item>
    /// <item><description>T plans tonight.</description></item>
    /// </list>
    /// A cursor that leaves the month on screen takes the month with it and asks for that month's nights.
    /// </summary>
    /// <returns>Whether the key was the calendar's.</returns>
    public static bool HandleKey(PlannerState state, InputKey key, ITimeProvider timeProvider, SignalBus? bus = null,
        SkyMapState? skyMap = null)
    {
        var calendar = state.Calendar;
        var cursor = calendar.Cursor ?? PlanningEveningDate(state, timeProvider);
        DateOnly? moved = key switch
        {
            InputKey.Left => cursor.AddDays(-1),
            InputKey.Right => cursor.AddDays(1),
            InputKey.Up => cursor.AddDays(-7),
            InputKey.Down => cursor.AddDays(7),
            InputKey.PageUp => cursor.AddMonths(-1),
            InputKey.PageDown => cursor.AddMonths(1),
            _ => null,
        };

        if (moved is { } night)
        {
            calendar.Cursor = night;
            var shown = FirstOfMonth(calendar.Month == default ? cursor : calendar.Month);
            if (!MonthGrid(shown, FirstDayOfWeek).Contains(night) || key is InputKey.PageUp or InputKey.PageDown)
            {
                calendar.Month = FirstOfMonth(night);
                bus?.Post(new NightCalendarMonthSignal(calendar.Month));
            }
            state.NeedsRedraw = true;
            return true;
        }

        switch (key)
        {
            case InputKey.Enter or InputKey.Space:
                PickNight(state, cursor, timeProvider, skyMap);
                return true;

            case InputKey.T:
                PickNight(state, TonightEveningDate(state, timeProvider), timeProvider, skyMap);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Refreshes the forecast behind the planner and the calendar, sets the planned night's weather band from it,
    /// and summarises the planned night's month (and the month on screen, when that differs).
    /// </summary>
    /// <remarks>
    /// Called wherever the planner used to fetch its one night: after the night window is known. With no weather
    /// device the calendar still fills in, from the Moon alone. Non-fatal throughout: a failed fetch leaves the
    /// band empty and the calendar moon-only, and says so in the log.
    /// </remarks>
    public static Task RefreshAsync(PlannerState state, Profile? profile, IServiceProvider sp,
        ITimeProvider timeProvider, ILogger logger, CancellationToken cancellationToken)
        => RefreshAsync(state, SiteOf(profile, timeProvider), WeatherOf(profile), sp, timeProvider, logger,
            cancellationToken);

    /// <summary>
    /// <see cref="RefreshAsync(PlannerState, Profile?, IServiceProvider, ITimeProvider, ILogger, CancellationToken)"/>
    /// for a host with no profile (the browser): the site as a transform, and the weather device to ask.
    /// </summary>
    /// <param name="site">The site, or null when there is none to plan for. Moved by the night-window calls, so
    /// hand in one nothing else is using.</param>
    /// <param name="weatherUri">The weather device, or null to summarise from the Moon alone.</param>
    public static async Task RefreshAsync(PlannerState state, Transform? site, Uri? weatherUri, IServiceProvider sp,
        ITimeProvider timeProvider, ILogger logger, CancellationToken cancellationToken)
    {
        if (site is not { } transform)
        {
            state.WeatherForecast = null;
            state.WeatherForecastOrigin = null;
            return;
        }

        var now = timeProvider.GetUtcNow();
        var key = new NightCalendarKey(transform.SiteLatitude, transform.SiteLongitude, transform.SiteElevation, weatherUri);
        var (rangeStart, rangeEnd) = ExtendedForecast.RangeFor(now);

        var previous = state.Calendar.Data;
        var reuse = previous.Key == key && previous.RangeStart == rangeStart && now - previous.FetchedAt < ForecastReuse;
        var forecast = reuse ? previous.Forecast : null;
        var fetchedAt = reuse ? previous.FetchedAt : now;

        IWeatherDriver? driver = null;
        try
        {
            if (!reuse && weatherUri is not null && TryCreateDriver(weatherUri, sp, out driver))
            {
                forecast = await driver.GetExtendedHourlyForecastAsync(sp, key.Latitude, key.Longitude,
                    rangeStart, rangeEnd, cancellationToken);
            }

            await SetPlannedNightWeatherAsync(state, forecast, weatherUri, rangeStart, rangeEnd, sp, driver,
                cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Weather forecast fetch failed");
            state.WeatherForecast = null;
            state.WeatherForecastOrigin = null;
        }
        finally
        {
            driver?.Dispose();
        }

        // A reused forecast keeps its nights and adds the planned month only if it is missing; a new one starts
        // over, since every night inside its horizon may have changed.
        var generation = reuse ? previous.Generation : state.Calendar.NextGeneration();
        if (!reuse)
        {
            state.Calendar.Publish(new NightCalendarData(generation, key, fetchedAt, rangeStart, forecast,
                ImmutableDictionary<DateOnly, NightSummary>.Empty));
        }

        var months = new List<DateOnly> { FirstOfMonth(PlanningEveningDate(state, timeProvider)) };
        if (state.Calendar.Month != default && !months.Contains(FirstOfMonth(state.Calendar.Month)))
        {
            months.Add(FirstOfMonth(state.Calendar.Month));
        }

        foreach (var month in months)
        {
            SummariseMonth(state.Calendar, transform, month, generation);
        }
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Summarises <paramref name="month"/>'s grid against the published forecast, and the pinned pointings on each of
    /// its nights, whichever is not done already. The open calendar asks for this; cells fill in when it lands.
    /// </summary>
    public static void EnsureMonth(PlannerState state, Profile? profile, ITimeProvider timeProvider, DateOnly month)
        => EnsureMonth(state, SiteOf(profile, timeProvider), month);

    /// <summary>
    /// <see cref="EnsureMonth(PlannerState, Profile?, ITimeProvider, DateOnly)"/> for a host with no profile: the
    /// site as a transform nothing else is using.
    /// </summary>
    public static void EnsureMonth(PlannerState state, Transform? site, DateOnly month)
    {
        if (site is not { } transform)
        {
            return;
        }

        var generation = state.Calendar.Data.Generation;
        SummariseMonth(state.Calendar, transform, month, generation);
        SummarisePins(state, transform, month, PinsKey(state), generation);
        state.NeedsRedraw = true;
    }

    /// <summary>The profile's site, or null without one (or without a mount, as the planner itself requires).</summary>
    private static Transform? SiteOf(Profile? profile, ITimeProvider timeProvider)
        => profile is null ? null : TransformFactory.FromProfile(profile, timeProvider, out _);

    /// <summary>The profile's weather device, or null for none.</summary>
    private static Uri? WeatherOf(Profile? profile)
        => profile?.Data is { Weather: { } weather } && weather != NoneDevice.Instance.DeviceUri ? weather : null;

    /// <summary>The pin set the pins half is computed for, read off the planner as it is now.</summary>
    public static NightPinsKey PinsKey(PlannerState state)
        => new NightPinsKey(state.Proposals, state.FramingGroups, state.MinHeightAboveHorizon);

    /// <summary>
    /// Whether <paramref name="month"/>'s grid has its pins for <paramref name="key"/>'s pin set. Nothing pinned has
    /// nothing to compute, so it needs no request at all.
    /// </summary>
    public static bool HasPins(NightCalendarData data, NightPinsKey key, DateOnly month)
    {
        if (key.Proposals.IsDefaultOrEmpty)
        {
            return true;
        }

        if (data.PinsKey != key)
        {
            return false;
        }

        foreach (var night in MonthGrid(month, FirstDayOfWeek))
        {
            if (data.Nights.ContainsKey(night) && !data.Pins.ContainsKey(night))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The pinned pointings on every summarised night of <paramref name="month"/>'s grid, collapsed as the scheduler
    /// collapses them. With nothing pinned it records the empty set, so the calendar stops asking.
    /// </summary>
    internal static void SummarisePins(PlannerState state, Transform transform, DateOnly month, NightPinsKey key,
        long generation)
    {
        var calendar = state.Calendar;
        var data = calendar.Data;
        var built = new List<KeyValuePair<DateOnly, NightPins>>(GridDays);
        if (!key.Proposals.IsDefaultOrEmpty)
        {
            var collapsed = FramingPlanner.CollapseForSchedule(key.Proposals.AsSpan(), key.Groups);
            var pointings = new Target[collapsed.Length];
            for (var i = 0; i < collapsed.Length; i++)
            {
                pointings[i] = collapsed[i].Target;
            }

            var hours = data.Forecast?.Hours;
            var fresh = data.PinsKey != key;
            foreach (var night in MonthGrid(month, FirstDayOfWeek))
            {
                if (data.Nights.TryGetValue(night, out var summary) && (fresh || !data.Pins.ContainsKey(night)))
                {
                    built.Add(new KeyValuePair<DateOnly, NightPins>(night, NightPins.Compute(transform.SiteLatitude,
                        transform.SiteLongitude, transform.SiteElevation, summary, pointings, key.MinAltitude, hours,
                        state.Comets)));
                }
            }
        }

        calendar.TryAddPins(generation, key, built);
    }

    /// <summary>Summarises every night of <paramref name="month"/>'s grid not already published.</summary>
    internal static void SummariseMonth(NightCalendarState calendar, Transform transform, DateOnly month, long generation)
    {
        var data = calendar.Data;
        var hours = data.Forecast?.Hours;
        var built = new List<KeyValuePair<DateOnly, NightSummary>>(GridDays);
        foreach (var night in MonthGrid(month, FirstDayOfWeek))
        {
            if (!data.Nights.ContainsKey(night))
            {
                built.Add(new KeyValuePair<DateOnly, NightSummary>(night, NightSummary.Compute(transform, night, hours)));
            }
        }

        if (built.Count > 0)
        {
            calendar.TryAddNights(generation, built);
        }
    }

    /// <summary>
    /// The planned night's band: a slice of <paramref name="forecast"/> where the night lies in its range, the
    /// planner's old single-night request where it lies BEFORE it, and nothing past it.
    /// </summary>
    private static async Task SetPlannedNightWeatherAsync(PlannerState state, ExtendedForecast? forecast,
        Uri? weatherUri, DateTimeOffset rangeStart, DateTimeOffset rangeEnd, IServiceProvider sp,
        IWeatherDriver? driver, CancellationToken cancellationToken)
    {
        var start = state.CivilSet ?? state.AstroDark - TimeSpan.FromHours(1);
        var end = state.CivilRise ?? state.AstroTwilight + TimeSpan.FromHours(1);

        if (weatherUri is null || double.IsNaN(state.SiteLatitude) || double.IsNaN(state.SiteLongitude))
        {
            state.WeatherForecast = null;
            state.WeatherForecastOrigin = null;
        }
        else if (end >= rangeStart && start <= rangeEnd)
        {
            state.WeatherForecast = forecast is null ? null : Slice(forecast.Hours, start, end);
            state.WeatherForecastOrigin = forecast;
        }
        else if (end < rangeStart)
        {
            // A night someone stepped back to: the only case that still asks for one night on its own. The
            // refresh's driver exists only when it fetched; a reused forecast brings none, so make one here.
            IWeatherDriver? own = null;
            try
            {
                if ((driver ?? (TryCreateDriver(weatherUri, sp, out own) ? own : null)) is { } past)
                {
                    var hours = await past.GetHourlyForecastWithUpperAirAsync(sp, state.SiteLatitude,
                        state.SiteLongitude, start, end, cancellationToken);
                    state.WeatherForecast = hours;
                    state.WeatherForecastOrigin = new ExtendedForecast(hours, past.Name, null, null);
                }
                else
                {
                    state.WeatherForecast = null;
                    state.WeatherForecastOrigin = null;
                }
            }
            finally
            {
                own?.Dispose();
            }
        }
        else
        {
            // Past the horizon: the provider would refuse the request, so it is not made.
            state.WeatherForecast = null;
            state.WeatherForecastOrigin = null;
        }

        state.NeedsRedraw = true;
    }

    /// <summary>The entries from <paramref name="start"/> to <paramref name="end"/>, inclusive, as the drivers trim.</summary>
    internal static List<HourlyWeatherForecast> Slice(IReadOnlyList<HourlyWeatherForecast> hours,
        DateTimeOffset start, DateTimeOffset end)
    {
        var slice = new List<HourlyWeatherForecast>();
        foreach (var hour in hours)
        {
            if (hour.Time >= start && hour.Time <= end)
            {
                slice.Add(hour);
            }
        }
        return slice;
    }

    private static bool TryCreateDriver(Uri weatherUri, IServiceProvider sp,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IWeatherDriver? driver)
    {
        driver = null;
        return EquipmentActions.TryDeviceFromUri(weatherUri) is { } device
            && device.TryInstantiateDriver(sp, out driver);
    }
}
