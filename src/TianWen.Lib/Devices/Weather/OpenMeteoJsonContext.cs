using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// AOT-safe JSON source generator context for Open-Meteo API response types
/// and cached forecast data.
/// </summary>
[JsonSerializable(typeof(OpenMeteoResponse))]
[JsonSerializable(typeof(List<HourlyWeatherForecast>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
internal partial class OpenMeteoJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Top-level Open-Meteo API response (hourly + current).
/// </summary>
internal sealed class OpenMeteoResponse
{
    public OpenMeteoCurrentData? Current { get; set; }
    public OpenMeteoHourlyData? Hourly { get; set; }
}

/// <summary>
/// Current weather conditions from Open-Meteo.
/// </summary>
internal sealed class OpenMeteoCurrentData
{
    public double Temperature2m { get; set; }
    public double RelativeHumidity2m { get; set; }
    public double CloudCover { get; set; }
    public double SurfacePressure { get; set; }
    public double WindDirection10m { get; set; }
    public double WindSpeed10m { get; set; }
    public double WindGusts10m { get; set; }
    public double Precipitation { get; set; }
}

/// <summary>
/// Hourly forecast arrays from Open-Meteo. All arrays are parallel (same length, same index = same hour).
/// </summary>
internal sealed class OpenMeteoHourlyData
{
    public List<string>? Time { get; set; }
    public List<double>? CloudCover { get; set; }
    public List<double>? Precipitation { get; set; }
    public List<double>? Temperature2m { get; set; }
    public List<double>? RelativeHumidity2m { get; set; }
    public List<double>? DewPoint2m { get; set; }
    public List<double>? WindSpeed10m { get; set; }
    public List<double>? WindGusts10m { get; set; }
    public List<double>? WindDirection10m { get; set; }
    public List<double>? Visibility { get; set; }
    public List<int>? WeatherCode { get; set; }
}
