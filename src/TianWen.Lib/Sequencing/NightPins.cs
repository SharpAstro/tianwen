using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;

namespace TianWen.Lib.Sequencing;

/// <summary>What the pinned pointings can do with one slice of a night.</summary>
public enum PinCoverage : byte
{
    /// <summary>No pinned pointing is above the minimum altitude.</summary>
    None,

    /// <summary>At least one is up, and the forecast says clear.</summary>
    Clear,

    /// <summary>At least one is up, and the forecast says cloud or rain.</summary>
    Cloudy,

    /// <summary>At least one is up, and no forecast reaches this far: usable if it is clear.</summary>
    Unforecast,
}

/// <summary>One pinned pointing on one night.</summary>
/// <param name="Name">The pointing's name (a co-framed group's joined name, as the scheduler has it).</param>
/// <param name="Located">False when a planet or comet could not be placed on this night; nothing else is then
/// meaningful.</param>
/// <param name="From">The first instant it is usable, or null when it never rises above the minimum.</param>
/// <param name="To">The last instant it is usable, or null.</param>
/// <param name="Up">Dark time above the minimum altitude.</param>
/// <param name="Forecast">The part of <paramref name="Up"/> a forecast hour covers.</param>
/// <param name="Clear">The part of <paramref name="Up"/> the forecast says is clear.</param>
/// <param name="MinMoonSeparationDeg">The closest the Moon comes while both are up, in degrees, or NaN when the Moon
/// is never up while the pointing is.</param>
public readonly record struct PinnedPointingNight(
    string Name,
    bool Located,
    DateTimeOffset? From,
    DateTimeOffset? To,
    TimeSpan Up,
    TimeSpan Forecast,
    TimeSpan Clear,
    double MinMoonSeparationDeg);

/// <summary>
/// The pinned pointings on one night: each one's window and hours, and the night's timeline of what they can use
/// together (docs/plans/night-calendar.md, P3). Kept apart from <see cref="NightSummary"/>, which is about the SKY and
/// must not move when a target is pinned.
/// </summary>
/// <param name="EveningDate">The night, by the date it begins on.</param>
/// <param name="Start">The dark window's start, where the timeline begins.</param>
/// <param name="End">The dark window's end.</param>
/// <param name="Timeline">One entry per <see cref="SampleStep"/> from <paramref name="Start"/>; the last covers
/// what is left of the window.</param>
/// <param name="Pointings">Each pinned pointing, in pin order.</param>
public sealed record NightPins(
    DateOnly EveningDate,
    DateTimeOffset Start,
    DateTimeOffset End,
    ImmutableArray<PinCoverage> Timeline,
    ImmutableArray<PinnedPointingNight> Pointings)
{
    /// <summary>The scheduler's own grid step, so a pin's window here and on the planner's chart agree.</summary>
    public static readonly TimeSpan SampleStep = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How much of the night at least one pointing is usable: the UNION of their windows, never the sum, since one
    /// mount images one pointing at a time.
    /// </summary>
    public TimeSpan Usable => Sum(static c => c is not PinCoverage.None);

    /// <summary>The part of <see cref="Usable"/> the forecast says is clear.</summary>
    public TimeSpan UsableClear => Sum(static c => c is PinCoverage.Clear);

    /// <summary>How long timeline entry <paramref name="index"/> lasts: a whole step, or the window's remainder.</summary>
    public TimeSpan SliceAt(int index)
    {
        var from = Start + (SampleStep * index);
        return End - from < SampleStep ? End - from : SampleStep;
    }

    private TimeSpan Sum(Func<PinCoverage, bool> counts)
    {
        var total = TimeSpan.Zero;
        for (var i = 0; i < Timeline.Length; i++)
        {
            if (counts(Timeline[i]))
            {
                total += SliceAt(i);
            }
        }
        return total;
    }

    /// <summary>
    /// The pinned pointings over <paramref name="night"/>'s dark window.
    /// </summary>
    /// <param name="latitude">Site latitude in degrees.</param>
    /// <param name="longitude">Site longitude in degrees.</param>
    /// <param name="elevation">Site elevation in metres.</param>
    /// <param name="night">The night, whose dark window is sampled.</param>
    /// <param name="pointings">The pinned pointings, collapsed as the scheduler collapses them (a co-framed group is
    /// one). A planet or comet is placed by its catalogue index at the night's middle, never by its stored RA/Dec,
    /// which was resolved on a different night.</param>
    /// <param name="minAltitude">The profile's minimum altitude in degrees.</param>
    /// <param name="forecast">The hourly forecast, or null past its horizon; read by the verdict's own rule.</param>
    /// <param name="comets">Places a pinned comet; without it a comet is not located.</param>
    public static NightPins Compute(double latitude, double longitude, double elevation, in NightSummary night,
        IReadOnlyList<Target> pointings, byte minAltitude, IReadOnlyList<HourlyWeatherForecast>? forecast = null,
        ICometRepository? comets = null)
    {
        var start = night.DarkStart;
        var end = night.DarkEnd > start ? night.DarkEnd : start;
        var count = end > start ? (int)Math.Ceiling((end - start) / SampleStep) : 0;

        // One astrometry context and one Moon position per sample, shared by every pointing, at each slice's middle.
        var times = new DateTimeOffset[count];
        var slices = new TimeSpan[count];
        var astroms = new Astrom[count];
        for (var i = 0; i < count; i++)
        {
            var from = start + (SampleStep * i);
            slices[i] = end - from < SampleStep ? end - from : SampleStep;
            times[i] = from + (slices[i] / 2);
            times[i].ToSOFAUtcJd(out var utc1, out var utc2);
            astroms[i] = SOFAHelpers.PrepareAstrom(utc1, utc2, latitude, longitude, elevation);
        }
        var moon = ObservationScheduler.PrecomputeMoonGrid(times, astroms, ObservationScheduler.DefaultMoonAvoidanceRadiusDeg);

        var byHour = HourlyForecastIndex.For(forecast, start, end);
        var weather = new PinCoverage[count];
        for (var i = 0; i < count; i++)
        {
            weather[i] = byHour is null || !byHour.TryGetKnown(times[i], out var hour) ? PinCoverage.Unforecast
                : HourlyForecastIndex.IsClear(hour) ? PinCoverage.Clear
                : PinCoverage.Cloudy;
        }

        var timeline = new PinCoverage[count];
        var middle = start + ((end - start) / 2);
        var results = ImmutableArray.CreateBuilder<PinnedPointingNight>(pointings.Count);
        foreach (var pointing in pointings)
        {
            if (!TryPlace(pointing, middle, comets, out var ra, out var dec))
            {
                results.Add(new PinnedPointingNight(pointing.Name, false, null, null, TimeSpan.Zero, TimeSpan.Zero,
                    TimeSpan.Zero, double.NaN));
                continue;
            }

            DateTimeOffset? first = null, last = null;
            TimeSpan up = TimeSpan.Zero, forecastUp = TimeSpan.Zero, clear = TimeSpan.Zero;
            var minSeparation = double.MaxValue;
            for (var i = 0; i < count; i++)
            {
                if (SOFAHelpers.AltitudeFromAstrom(ra, dec, in astroms[i]) <= minAltitude)
                {
                    continue;
                }

                var from = start + (SampleStep * i);
                first ??= from;
                last = from + slices[i];
                up += slices[i];
                if (weather[i] is not PinCoverage.Unforecast)
                {
                    forecastUp += slices[i];
                }
                if (weather[i] is PinCoverage.Clear)
                {
                    clear += slices[i];
                }
                timeline[i] = weather[i];

                if (moon.AboveHorizon[i] && !double.IsNaN(moon.RaHours[i]))
                {
                    minSeparation = Math.Min(minSeparation,
                        CoordinateUtils.AngularSeparationDeg(moon.RaHours[i], moon.DecDeg[i], ra, dec));
                }
            }

            results.Add(new PinnedPointingNight(pointing.Name, true, first, last, up, forecastUp, clear,
                minSeparation == double.MaxValue ? double.NaN : minSeparation));
        }

        return new NightPins(night.EveningDate, start, end, ImmutableArray.Create(timeline), results.MoveToImmutable());
    }

    /// <summary>
    /// Where <paramref name="pointing"/> is on this night: a planet or comet from its catalogue index at
    /// <paramref name="at"/>, anything else from its own coordinates.
    /// </summary>
    private static bool TryPlace(Target pointing, DateTimeOffset at, ICometRepository? comets, out double ra, out double dec)
    {
        if (pointing.CatalogIndex is { } index && index.IsSolarSystemObject)
        {
            if (index.ToCatalog() is Catalog.Comet)
            {
                ra = dec = double.NaN;
                return comets is not null && comets.TryGetPosition(index, at, out ra, out dec, out _);
            }

            return VSOP87a.ReduceJ2000(index, at, out ra, out dec, out _);
        }

        ra = pointing.RA;
        dec = pointing.Dec;
        return double.IsFinite(ra) && double.IsFinite(dec);
    }
}
