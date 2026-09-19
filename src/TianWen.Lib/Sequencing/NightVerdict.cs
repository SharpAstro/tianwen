using System;
using TianWen.Lib.Devices.Weather;

namespace TianWen.Lib.Sequencing;

/// <summary>What a night is worth, in one word. Ordered worst to best within each kind.</summary>
public enum NightOutlook : byte
{
    /// <summary>No forecast covers the night, and the Moon is up for most of it.</summary>
    Moonlit,

    /// <summary>No forecast covers the night, and most of it is moon-free.</summary>
    Dark,

    /// <summary>The forecast leaves too little clear, moon-free dark time to be worth setting up.</summary>
    NoGo,

    /// <summary>The forecast leaves some clear dark time, or a good night under bad seeing.</summary>
    Marginal,

    /// <summary>The forecast leaves most of the night clear and dark.</summary>
    Go,
}

/// <summary>
/// The ONE verdict on a night, used by the calendar cell and the status bar alike so the two can never disagree
/// (docs/plans/night-calendar.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>Inside the forecast horizon</b> it is Go, Marginal or No-go, from the night's USEFUL dark time: clear dark
/// time, with a moonlit hour counted at the unlit fraction of the Moon (a thin crescent costs almost nothing) but
/// never below <see cref="MoonlitFloor"/>, so a clear night under a full Moon is Marginal rather than No-go:
/// narrowband and the Moon's own targets still work under it. A Go under a Bad seeing class becomes Marginal.
/// <b>Beyond the horizon</b>, or where the forecast covers too little of the night, it says only what is
/// knowable: Dark or Moonlit, from the same moon-weighted dark time. A calendar that shows a confident Go twelve
/// days out is lying.
/// </para>
/// <para>
/// Every threshold is a round number, not a fit, the way <see cref="SeeingForecast"/> started. A night needs
/// either three useful hours or most of its dark window, so a short summer night at high latitude can still be a
/// Go; calibrating these against nights actually imaged is later work.
/// </para>
/// </remarks>
/// <param name="Outlook">The verdict.</param>
/// <param name="UsefulDark">The moon-weighted dark time the verdict was read from: clear time for a forecast
/// verdict, all of it for a moon-only one.</param>
public readonly record struct NightVerdict(NightOutlook Outlook, TimeSpan UsefulDark)
{
    /// <summary>A weather verdict needs a forecast for at least this fraction of the dark window.</summary>
    public const double MinForecastCoverage = 0.75;

    /// <summary>Go needs this much useful dark time, or <see cref="GoFraction"/> of the night.</summary>
    public static readonly TimeSpan GoUseful = TimeSpan.FromHours(3);

    /// <summary>Go needs this fraction of the dark window as useful time, or <see cref="GoUseful"/>.</summary>
    public const double GoFraction = 0.6;

    /// <summary>Marginal needs this much useful dark time, or <see cref="MarginalFraction"/> of the night.</summary>
    public static readonly TimeSpan MarginalUseful = TimeSpan.FromHours(1);

    /// <summary>Marginal needs this fraction of the dark window as useful time, or <see cref="MarginalUseful"/>.</summary>
    public const double MarginalFraction = 0.25;

    /// <summary>A moon-only night is Dark when at least this fraction of it is moon-weighted dark.</summary>
    public const double DarkFraction = 0.6;

    /// <summary>The least a moonlit dark hour counts for, whatever the Moon's phase.</summary>
    public const double MoonlitFloor = 0.25;

    /// <summary>Whether this verdict comes from a weather forecast, rather than the Moon alone.</summary>
    public bool IsForecast => Outlook is NightOutlook.Go or NightOutlook.Marginal or NightOutlook.NoGo;

    /// <summary>The verdict as a word, for a label or a tooltip.</summary>
    public string Label => Outlook switch
    {
        NightOutlook.Go => "Go",
        NightOutlook.Marginal => "Marginal",
        NightOutlook.NoGo => "No-go",
        NightOutlook.Dark => "Dark",
        _ => "Moonlit",
    };

    /// <summary>The verdict on <paramref name="night"/>.</summary>
    public static NightVerdict For(in NightSummary night)
    {
        var dark = night.Dark;
        var moonlitWeight = Math.Max(1.0 - Math.Clamp(night.MoonIllumination, 0.0, 1.0), MoonlitFloor);

        if (night.Forecast is { } forecast && dark > TimeSpan.Zero
            && forecast.Covered.TotalHours >= MinForecastCoverage * dark.TotalHours)
        {
            var useful = forecast.ClearMoonFreeDark + ((forecast.ClearDark - forecast.ClearMoonFreeDark) * moonlitWeight);
            var outlook = Meets(useful, dark, GoUseful, GoFraction) ? NightOutlook.Go
                : Meets(useful, dark, MarginalUseful, MarginalFraction) ? NightOutlook.Marginal
                : NightOutlook.NoGo;

            if (outlook is NightOutlook.Go && forecast.Seeing is SeeingClass.Bad)
            {
                outlook = NightOutlook.Marginal;
            }

            return new NightVerdict(outlook, useful);
        }

        var moonWeighted = night.MoonFreeDark + ((dark - night.MoonFreeDark) * moonlitWeight);
        return new NightVerdict(
            dark > TimeSpan.Zero && moonWeighted.TotalHours >= DarkFraction * dark.TotalHours
                ? NightOutlook.Dark
                : NightOutlook.Moonlit,
            moonWeighted);
    }

    private static bool Meets(TimeSpan useful, TimeSpan dark, TimeSpan absolute, double fraction)
        => useful >= absolute || useful.TotalHours >= fraction * dark.TotalHours;
}
