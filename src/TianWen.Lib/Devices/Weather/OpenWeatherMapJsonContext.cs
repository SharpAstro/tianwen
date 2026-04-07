using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// AOT-safe JSON source generator context for OpenWeatherMap API response types.
/// </summary>
[JsonSerializable(typeof(OwmCurrentResponse))]
[JsonSerializable(typeof(OwmForecastResponse))]
[JsonSerializable(typeof(OwmOneCallResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
internal partial class OpenWeatherMapJsonContext : JsonSerializerContext
{
}

/// <summary>
/// OpenWeatherMap current weather response (/data/2.5/weather).
/// </summary>
internal sealed class OwmCurrentResponse
{
    public OwmMainData? Main { get; set; }
    public OwmWindData? Wind { get; set; }
    public OwmCloudsData? Clouds { get; set; }
    public OwmRainData? Rain { get; set; }
    public int Visibility { get; set; }
    public List<OwmWeatherEntry>? Weather { get; set; }
}

/// <summary>
/// OpenWeatherMap 5-day/3-hour forecast response (/data/2.5/forecast).
/// </summary>
internal sealed class OwmForecastResponse
{
    public List<OwmForecastEntry>? List { get; set; }
}

internal sealed class OwmForecastEntry
{
    /// <summary>Unix timestamp (seconds).</summary>
    public long Dt { get; set; }
    public OwmMainData? Main { get; set; }
    public OwmWindData? Wind { get; set; }
    public OwmCloudsData? Clouds { get; set; }
    public OwmRainData? Rain { get; set; }
    public int Visibility { get; set; }
    public List<OwmWeatherEntry>? Weather { get; set; }
    /// <summary>Probability of precipitation (0–1).</summary>
    public double Pop { get; set; }
}

internal sealed class OwmMainData
{
    public double Temp { get; set; }
    public double Humidity { get; set; }
    public double Pressure { get; set; }
}

internal sealed class OwmWindData
{
    public double Speed { get; set; }
    public double Gust { get; set; }
    public double Deg { get; set; }
}

internal sealed class OwmCloudsData
{
    public double All { get; set; }
}

/// <summary>
/// Rain volume. The <c>3h</c> field holds mm in the last 3 hours (forecast)
/// or <c>1h</c> for current weather.
/// </summary>
internal sealed class OwmRainData
{
    [JsonPropertyName("1h")]
    public double OneHour { get; set; }

    [JsonPropertyName("3h")]
    public double ThreeHour { get; set; }
}

internal sealed class OwmWeatherEntry
{
    public int Id { get; set; }
    public string? Main { get; set; }
    public string? Description { get; set; }
}

// --- One Call 3.0 response types ---

/// <summary>
/// OpenWeatherMap One Call 3.0 response (/data/3.0/onecall).
/// Contains current conditions and native hourly forecasts (48 hours).
/// </summary>
internal sealed class OwmOneCallResponse
{
    public OwmOneCallCurrentData? Current { get; set; }
    public List<OwmOneCallHourlyEntry>? Hourly { get; set; }
}

/// <summary>
/// Current conditions block in the One Call 3.0 response.
/// Fields are flat (no nested Main/Wind/Clouds objects like 2.5).
/// </summary>
internal sealed class OwmOneCallCurrentData
{
    public long Dt { get; set; }
    public double Temp { get; set; }
    public double Humidity { get; set; }
    public double DewPoint { get; set; }
    public double Pressure { get; set; }
    public double Clouds { get; set; }
    public double WindSpeed { get; set; }
    public double WindGust { get; set; }
    public double WindDeg { get; set; }
    public double Uvi { get; set; }
    public int Visibility { get; set; }
    public OwmRainData? Rain { get; set; }
    public List<OwmWeatherEntry>? Weather { get; set; }
}

/// <summary>
/// Hourly forecast entry in the One Call 3.0 response.
/// Same flat structure as current — native hourly, no interpolation needed.
/// </summary>
internal sealed class OwmOneCallHourlyEntry
{
    public long Dt { get; set; }
    public double Temp { get; set; }
    public double Humidity { get; set; }
    public double DewPoint { get; set; }
    public double Pressure { get; set; }
    public double Clouds { get; set; }
    public double WindSpeed { get; set; }
    public double WindGust { get; set; }
    public double WindDeg { get; set; }
    public double Uvi { get; set; }
    public int Visibility { get; set; }
    public double Pop { get; set; }
    public OwmRainData? Rain { get; set; }
    public List<OwmWeatherEntry>? Weather { get; set; }
}
