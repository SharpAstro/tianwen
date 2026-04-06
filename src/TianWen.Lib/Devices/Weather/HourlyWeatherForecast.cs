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
    int WeatherCode);
