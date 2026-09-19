using System;
using System.Collections.Generic;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Lunar;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Devices.Weather;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// One night at a site, summarised for a person choosing WHICH night to image: its dark window, the Moon, and,
/// inside the forecast horizon, the weather over that window. <see cref="NightVerdict.For"/> turns it into the one
/// verdict the calendar cell and the status bar both show (docs/plans/night-calendar.md).
/// </summary>
/// <remarks>
/// A night is named by its EVENING date (<see cref="CoordinateUtils.AstronomicalEveningDate"/>): it spans
/// midnight, so a summary keyed on the calendar date would show the wrong night's Moon for half of it. Nothing
/// here is read by the session; it is a forecast for a person deciding.
/// </remarks>
/// <param name="EveningDate">The date the night begins on, site-local.</param>
/// <param name="DarkStart">When it gets dark enough to image, site-local.</param>
/// <param name="DarkEnd">When it stops being dark enough, site-local.</param>
/// <param name="DarkBoundary">The twilight the evening edge was found at (the planner's night window and its
/// fallback chain), or null when none was reached: polar night, or a sun that never got low enough.</param>
/// <param name="MoonIllumination">The Moon's illuminated fraction at the middle of the dark window, 0 to 1.</param>
/// <param name="MoonWaxing">Whether the Moon is waxing at the middle of the dark window.</param>
/// <param name="MoonFreeDark">How much of the dark window has the Moon below the horizon.</param>
/// <param name="Forecast">The weather over the dark window, or null when no forecast hour covers any of it.</param>
public readonly record struct NightSummary(
    DateOnly EveningDate,
    DateTimeOffset DarkStart,
    DateTimeOffset DarkEnd,
    EventType? DarkBoundary,
    double MoonIllumination,
    bool MoonWaxing,
    TimeSpan MoonFreeDark,
    NightForecast? Forecast)
{
    /// <summary>
    /// An hour is CLEAR below this cloud cover, in percent, and with no measurable precipitation. A round number,
    /// not a fit, exactly as the seeing classes started; calibrating it against nights actually imaged is later work.
    /// </summary>
    public const double ClearCloudCoverPercent = 30.0;

    /// <summary>Precipitation below this, in mm per hour, counts as none.</summary>
    public const double NoPrecipitationMmPerHour = 0.1;

    /// <summary>
    /// The sampling step over the dark window. Ten minutes resolves a moonrise well inside the hour the forecast
    /// is stated in, and a whole polar night is still only 144 samples.
    /// </summary>
    internal static readonly TimeSpan Step = TimeSpan.FromMinutes(10);

    /// <summary>The length of the dark window.</summary>
    public TimeSpan Dark => DarkEnd > DarkStart ? DarkEnd - DarkStart : TimeSpan.Zero;

    /// <summary>
    /// Summarises the night beginning on the evening of <paramref name="eveningDate"/> at the transform's site.
    /// </summary>
    /// <param name="transform">The site. Its <see cref="Transform.DateTimeOffset"/> is moved, as by every
    /// night-window call, so a caller summarising in parallel owns one per thread.</param>
    /// <param name="eveningDate">The night, by the date it begins on.</param>
    /// <param name="forecast">Hourly forecast entries, each stated on its UTC hour and covering the hour after it;
    /// any order, any range. Null or empty gives a summary with no <see cref="Forecast"/>.</param>
    public static NightSummary Compute(Transform transform, DateOnly eveningDate,
        IReadOnlyList<HourlyWeatherForecast>? forecast = null)
    {
        var window = ObservationScheduler.CalculateNightWindowForEvening(transform, eveningDate);
        var start = window.Dark;
        var end = window.Twilight > start ? window.Twilight : start;

        var mid = start + ((end - start) / 2);
        var (illumination, waxing) = MeeusMoon.GetPhase(mid.ToJulian());

        var byHour = HourlyForecastIndex.For(forecast, start, end);

        var moonFree = TimeSpan.Zero;
        var covered = TimeSpan.Zero;
        var clear = TimeSpan.Zero;
        var clearMoonFree = TimeSpan.Zero;
        var cloudWeighted = 0.0;
        var precipitation = 0.0;
        List<SeeingClass>? seeing = null;

        for (var t = start; t < end; t += Step)
        {
            var slice = end - t < Step ? end - t : Step;
            var sample = t + (slice / 2);

            var moonDown = !VSOP87a.Reduce(CatalogIndex.Moon, sample, transform.SiteLatitude, transform.SiteLongitude,
                    out _, out _, out _, out var moonAlt, out _)
                || moonAlt < 0.0;
            if (moonDown)
            {
                moonFree += slice;
            }

            if (byHour is null || !byHour.TryGetKnown(sample, out var hour))
            {
                continue;
            }

            covered += slice;
            cloudWeighted += hour.CloudCover * slice.TotalHours;
            precipitation += HourlyForecastIndex.Rain(hour) * slice.TotalHours;

            if (HourlyForecastIndex.IsClear(hour))
            {
                clear += slice;
                if (moonDown)
                {
                    clearMoonFree += slice;
                }
            }

            if (SeeingForecast.For(hour).Class is var seeingClass and not SeeingClass.Unknown)
            {
                (seeing ??= []).Add(seeingClass);
            }
        }

        NightForecast? nightForecast = covered > TimeSpan.Zero
            ? new NightForecast(covered, clear, clearMoonFree, cloudWeighted / covered.TotalHours, precipitation,
                MedianClass(seeing))
            : null;

        return new NightSummary(eveningDate, start, end, window.EveningBoundary, illumination, waxing, moonFree,
            nightForecast);
    }

    /// <summary>The median class of the known samples (the lower middle on an even count), or Unknown.</summary>
    private static SeeingClass MedianClass(List<SeeingClass>? classes)
    {
        if (classes is not { Count: > 0 })
        {
            return SeeingClass.Unknown;
        }

        classes.Sort();
        return classes[(classes.Count - 1) / 2];
    }
}

/// <summary>
/// The weather over one night's dark window, from the hourly forecast. Every duration is dark time.
/// </summary>
/// <param name="Covered">How much of the dark window a forecast hour covers. Less than the whole window at the
/// edge of the forecast horizon, which is why <see cref="NightVerdict.For"/> asks for most of it.</param>
/// <param name="ClearDark">Covered dark time that is clear (<see cref="NightSummary.ClearCloudCoverPercent"/>).</param>
/// <param name="ClearMoonFreeDark">Clear dark time with the Moon below the horizon.</param>
/// <param name="MeanCloudCover">Mean cloud cover over the covered time, in percent.</param>
/// <param name="Precipitation">Precipitation over the covered time, in mm.</param>
/// <param name="Seeing">The median seeing class over the covered time, or Unknown with no upper-air wind.</param>
public readonly record struct NightForecast(
    TimeSpan Covered,
    TimeSpan ClearDark,
    TimeSpan ClearMoonFreeDark,
    double MeanCloudCover,
    double Precipitation,
    SeeingClass Seeing);
