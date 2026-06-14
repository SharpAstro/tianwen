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
    // Fields with a digit boundary ("_2m"/"_10m") need an explicit name: the context's
    // SnakeCaseLower policy converts "Temperature2m" -> "temperature2m" (no underscore before
    // the digit), which does NOT match Open-Meteo's "temperature_2m" -> the value silently
    // binds to nothing and reads back NaN. (Bit us: temp/humidity/wind were all NaN.)
    [JsonPropertyName("temperature_2m")] public double Temperature2m { get; set; }
    [JsonPropertyName("relative_humidity_2m")] public double RelativeHumidity2m { get; set; }
    public double CloudCover { get; set; }
    public double SurfacePressure { get; set; }
    [JsonPropertyName("wind_direction_10m")] public double WindDirection10m { get; set; }
    [JsonPropertyName("wind_speed_10m")] public double WindSpeed10m { get; set; }
    [JsonPropertyName("wind_gusts_10m")] public double WindGusts10m { get; set; }
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
    public List<double>? PrecipitationProbability { get; set; }
    // "_2m"/"_10m" fields need explicit names -- SnakeCaseLower yields "temperature2m" (no
    // underscore before the digit), which doesn't match Open-Meteo's "temperature_2m", so the
    // arrays bound to nothing and every value read back NaN. (See OpenMeteoCurrentData.)
    [JsonPropertyName("temperature_2m")] public List<double>? Temperature2m { get; set; }
    [JsonPropertyName("relative_humidity_2m")] public List<double>? RelativeHumidity2m { get; set; }
    [JsonPropertyName("dew_point_2m")] public List<double>? DewPoint2m { get; set; }
    [JsonPropertyName("wind_speed_10m")] public List<double>? WindSpeed10m { get; set; }
    [JsonPropertyName("wind_gusts_10m")] public List<double>? WindGusts10m { get; set; }
    [JsonPropertyName("wind_direction_10m")] public List<double>? WindDirection10m { get; set; }
    public List<double>? Visibility { get; set; }
    public List<int>? WeatherCode { get; set; }
}
