using System;

namespace TianWen.Lib.Devices.Weather;

/// <summary>A seeing ESTIMATE's class, worst to best. <see cref="Unknown"/> when there is nothing to estimate from.</summary>
public enum SeeingClass : byte
{
    Unknown = 0,
    Bad = 1,
    Poor = 2,
    Average = 3,
    Good = 4,
    Excellent = 5,
}

/// <summary>
/// A seeing ESTIMATE for one forecast hour, from the wind aloft: fast wind at the jet-stream level (250 hPa,
/// about 10 km) means turbulent upper air and soft stars however clear the night. It is a forecast proxy and
/// nothing more: <see cref="IWeatherDriver.StarFWHM"/> stays reserved for MEASURED seeing, and the session never
/// makes a decision from this (docs/plans/seeing-forecast.md).
/// </summary>
/// <remarks>
/// <para>The classes start from astrophoto.app's heuristic, so the two agree on day one: the worse of the
/// 250 hPa wind and twice the surface wind, against 30 / 60 / 90 / 130 km/h. Its thresholds are round numbers,
/// not a fit; calibrating them against our own archived sub FWHM and guide RMS is the plan's P2.</para>
/// <para>One deliberate departure: with no 250 hPa wind the class is <see cref="SeeingClass.Unknown"/>, where
/// astrophoto.app substitutes three times the surface wind. The class claims to come from the air aloft, and
/// a source with no upper-air field (OpenWeatherMap) would otherwise show a confident estimate built from the
/// surface alone.</para>
/// </remarks>
/// <param name="Class">The estimated class.</param>
/// <param name="WindKmh">The wind the class was read from (the worse of the jet and twice the surface wind),
/// km/h, or NaN when <see cref="Class"/> is <see cref="SeeingClass.Unknown"/>.</param>
public readonly record struct SeeingForecast(SeeingClass Class, double WindKmh)
{
    /// <summary>The estimate when there is nothing to estimate from.</summary>
    public static readonly SeeingForecast Unknown = new SeeingForecast(SeeingClass.Unknown, double.NaN);

    /// <summary>The estimate for one forecast hour.</summary>
    public static SeeingForecast For(in HourlyWeatherForecast hour)
    {
        if (double.IsNaN(hour.WindSpeed250hPa))
        {
            return Unknown;
        }

        // The record carries m/s; the thresholds are km/h, as the heuristic was written.
        var jetKmh = hour.WindSpeed250hPa * 3.6;
        var worstKmh = double.IsNaN(hour.WindSpeed) ? jetKmh : Math.Max(jetKmh, 2 * hour.WindSpeed * 3.6);
        var seeingClass = worstKmh switch
        {
            < 30 => SeeingClass.Excellent,
            < 60 => SeeingClass.Good,
            < 90 => SeeingClass.Average,
            < 130 => SeeingClass.Poor,
            _ => SeeingClass.Bad,
        };
        return new SeeingForecast(seeingClass, worstKmh);
    }
}
