using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using DIR.Lib;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The night calendar: the popover off the status-bar date, the month it shows, and the per-night summaries
/// behind every cell and the status-bar verdict (docs/plans/night-calendar.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>One model for both readers.</b> The status bar's verdict and the calendar cell for the same night read the
/// same <see cref="NightSummary"/> through the same <see cref="NightVerdict.For"/>, so the two cannot disagree.
/// </para>
/// <para>
/// <b>Written from the background, read by the render thread.</b> Every result lands as ONE immutable
/// <see cref="NightCalendarData"/> swapped in whole: a published snapshot never changes under a reader, and a
/// month built for a forecast that has since been replaced is dropped rather than merged into the new one
/// (<see cref="TryAddNights"/> compares the generation inside a compare-and-swap).
/// </para>
/// </remarks>
public sealed class NightCalendarState
{
    private NightCalendarData _data = NightCalendarData.Empty;
    private long _generation;

    /// <summary>Whether the calendar is open. Its trigger is the status-bar date.</summary>
    public PopoverState Popover { get; } = new PopoverState();

    /// <summary>The first day of the month on screen. Set to the planning night's month each time the calendar
    /// opens, then moved by the paging buttons.</summary>
    public DateOnly Month { get; set; }

    /// <summary>The latest published forecast and summaries. Read it once per frame into a local.</summary>
    public NightCalendarData Data => Volatile.Read(ref _data);

    /// <summary>Replaces everything: a new forecast, or a new site, makes every earlier night stale.</summary>
    internal void Publish(NightCalendarData data) => Volatile.Write(ref _data, data);

    /// <summary>A generation no earlier publish has used, so two refreshes that overlap can never share one.</summary>
    internal long NextGeneration() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Adds <paramref name="nights"/> to the published snapshot if it is still <paramref name="generation"/>, and
    /// drops them otherwise: a month built from a forecast that has since been replaced is not this forecast's.
    /// </summary>
    /// <returns>Whether they were added.</returns>
    internal bool TryAddNights(long generation, IEnumerable<KeyValuePair<DateOnly, NightSummary>> nights)
    {
        while (true)
        {
            var current = Data;
            if (current.Generation != generation)
            {
                return false;
            }

            var next = current with { Nights = current.Nights.SetItems(nights) };
            if (ReferenceEquals(Interlocked.CompareExchange(ref _data, next, current), current))
            {
                return true;
            }
        }
    }
}

/// <summary>
/// One published state of the night calendar.
/// </summary>
/// <param name="Generation">Bumped on every <see cref="NightCalendarState.Publish"/>; a background month build
/// carries the generation it started from.</param>
/// <param name="Key">The site and weather device this was built for, or null before the first build.</param>
/// <param name="FetchedAt">When <paramref name="Forecast"/> was fetched, so a refresh within the drivers' own
/// cache lifetime reuses it without asking anything.</param>
/// <param name="RangeStart">The first instant the forecast was ASKED for (<see cref="ExtendedForecast.RangeFor"/>),
/// which moves with the UTC date, so a forecast fetched yesterday is refetched even inside the hour.</param>
/// <param name="Forecast">The multi-day forecast, or null with no weather device or after a failed fetch.</param>
/// <param name="Nights">The nights summarised so far, by evening date.</param>
public sealed record NightCalendarData(
    long Generation,
    NightCalendarKey? Key,
    DateTimeOffset FetchedAt,
    DateTimeOffset RangeStart,
    ExtendedForecast? Forecast,
    ImmutableDictionary<DateOnly, NightSummary> Nights)
{
    /// <summary>Nothing built yet.</summary>
    public static readonly NightCalendarData Empty = new NightCalendarData(0, null, default, default, null,
        ImmutableDictionary<DateOnly, NightSummary>.Empty);
}

/// <summary>What a calendar's contents depend on besides the date: the site, and the weather device asked.</summary>
public readonly record struct NightCalendarKey(double Latitude, double Longitude, double Elevation, Uri? Weather);
