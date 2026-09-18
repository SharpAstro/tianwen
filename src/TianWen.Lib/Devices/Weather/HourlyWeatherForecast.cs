using System;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// A single hourly weather data point, used for forecast overlays in the observation planner.
/// </summary>
/// <param name="Time">The hour this forecast applies to.</param>
/// <param name="CloudCover">Cloud cover percentage (0–100).</param>
/// <param name="Precipitation">Precipitation in mm for this hour.</param>
/// <param name="Temperature">Temperature in °C.</param>
/// <param name="Humidity">Relative humidity percentage (0–100).</param>
/// <param name="DewPoint">Dew point in °C.</param>
/// <param name="WindSpeed">Wind speed in m/s.</param>
/// <param name="WindGust">Wind gust speed in m/s.</param>
/// <param name="WindDirection">Wind direction in degrees (0=N, 90=E, 180=S, 270=W).</param>
/// <param name="Visibility">Visibility in meters.</param>
/// <param name="WeatherCode">WMO 4677 weather code. Key values: 0=clear, 1–3=partly cloudy/overcast,
/// 45/48=fog/rime fog, 51–67=drizzle/rain, 71–77=snow, 80–82=showers, 95–99=thunderstorm.</param>
/// <param name="PrecipitationProbability">Chance of precipitation as a percentage (0–100), or
/// <see cref="double.NaN"/> when the source does not provide it.</param>
/// <param name="WindSpeed250hPa">Wind speed at the 250 hPa pressure level (about 10 km, the jet stream) in
/// m/s, or <see cref="double.NaN"/> when the source does not provide it. The input to a SEEING FORECAST
/// (docs/plans/seeing-forecast.md), never a seeing measurement.</param>
/// <param name="WindSpeed500hPa">Wind speed at 500 hPa (about 5.5 km) in m/s, or NaN.</param>
/// <param name="WindSpeed850hPa">Wind speed at 850 hPa (about 1.5 km) in m/s, or NaN.</param>
// The forecast CACHE is read through THIS constructor, so a field an older cache lacks takes the parameter's
// default (NaN, "not known") instead of 0. Without the attribute the JSON source generator builds the struct
// through its implicit parameterless constructor and passes every init-only member as an argument with no
// default, so a missing one arrived as default(double): a cache written before the pressure-level winds
// existed read them back as a dead-calm jet stream, which the seeing forecast would call a perfect night
// (OpenMeteoPressureLevelWindTests).
[method: System.Text.Json.Serialization.JsonConstructor]
public readonly record struct HourlyWeatherForecast(
    DateTimeOffset Time,
    double CloudCover,
    double Precipitation,
    double Temperature,
    double Humidity,
    double DewPoint,
    double WindSpeed,
    double WindGust,
    double WindDirection,
    double Visibility,
    int WeatherCode,
    double PrecipitationProbability = double.NaN,
    double WindSpeed250hPa = double.NaN,
    double WindSpeed500hPa = double.NaN,
    double WindSpeed850hPa = double.NaN);
