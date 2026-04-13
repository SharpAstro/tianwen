using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// Built-in weather driver that fetches data from the Open-Meteo free API (no API key required).
/// Implements <see cref="IWeatherDriver"/> with file-based caching (1-hour TTL).
/// </summary>
internal sealed class OpenMeteoDriver : IWeatherDriver
{
    private const string BaseUrl = "https://api.open-meteo.com/v1/forecast";
    private const string HourlyParams = "cloud_cover,precipitation,temperature_2m,relative_humidity_2m,dew_point_2m,wind_speed_10m,wind_gusts_10m,wind_direction_10m,visibility,weather_code";
    private const string CurrentParams = "temperature_2m,relative_humidity_2m,cloud_cover,surface_pressure,wind_direction_10m,wind_speed_10m,wind_gusts_10m,precipitation";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private static readonly HttpClient s_httpClient = new HttpClient()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly OpenMeteoDevice _device;
    private readonly IExternal _external;
    private bool _connected;

    // Current conditions (populated on connect or first forecast fetch)
    private double _cloudCover = double.NaN;
    private double _temperature = double.NaN;
    private double _humidity = double.NaN;
    private double _dewPoint = double.NaN;
    private double _pressure = double.NaN;
    private double _windSpeed = double.NaN;
    private double _windGust = double.NaN;
    private double _windDirection = double.NaN;
    private double _rainRate = double.NaN;

    public OpenMeteoDriver(OpenMeteoDevice device, IServiceProvider serviceProvider)
    {
        _device = device;
        _external = serviceProvider.GetRequiredService<IExternal>();
        Logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(OpenMeteoDriver));
        TimeProvider = serviceProvider.GetRequiredService<TimeProvider>();
    }

    // --- IDeviceDriver ---
    public string Name => _device.DisplayName;
    public string? Description => "Open-Meteo free weather forecast service";
    public string? DriverInfo => Description;
    public string? DriverVersion => "1.0";
    public DeviceType DriverType => DeviceType.Weather;
    public IExternal External => _external;
    public ILogger Logger { get; }
    public TimeProvider TimeProvider { get; }
    public bool Connected => Volatile.Read(ref _connected);
    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Connected)
        {
            return;
        }

        // Validate API is reachable with a minimal current-weather request
        try
        {
            // Use 0,0 as a quick connectivity check — actual lat/lon comes from profile
            var testUrl = $"{BaseUrl}?latitude=0&longitude=0&current={CurrentParams}";
            using var response = await s_httpClient.GetAsync(testUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Open-Meteo API connectivity check failed");
            throw;
        }

        Volatile.Write(ref _connected, true);
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(true));
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _connected, false);
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(false));
        return ValueTask.CompletedTask;
    }

    // --- IWeatherDriver current conditions ---
    public double CloudCover => _cloudCover;
    public double Temperature => _temperature;
    public double Humidity => _humidity;
    public double DewPoint => _dewPoint;
    public double Pressure => _pressure;
    public double WindSpeed => _windSpeed;
    public double WindGust => _windGust;
    public double WindDirection => _windDirection;
    public double RainRate => _rainRate;
    public double SkyQuality => double.NaN;
    public double SkyTemperature => double.NaN;
    public double StarFWHM => double.NaN;

    // --- IWeatherDriver forecast ---
    public async Task<IReadOnlyList<HourlyWeatherForecast>> GetHourlyForecastAsync(
        double latitude, double longitude,
        DateTimeOffset start, DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        var startDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Check file cache
        var cacheDir = _external.CreateSubDirectoryInAppDataFolder("Weather");
        var cacheFile = Path.Combine(cacheDir.FullName,
            $"{latitude:F2}_{longitude:F2}_{startDate}_{endDate}.json");

        // Return fresh cache immediately if within TTL
        var cached = await TryLoadCacheAsync(cacheFile, cancellationToken);
        if (cached is { fresh: true })
        {
            return cached.Value.data;
        }

        // TTL expired or no cache — try the API
        var url = $"{BaseUrl}?latitude={latitude.ToString(CultureInfo.InvariantCulture)}"
            + $"&longitude={longitude.ToString(CultureInfo.InvariantCulture)}"
            + $"&hourly={HourlyParams}"
            + $"&current={CurrentParams}"
            + $"&start_date={startDate}&end_date={endDate}";

        Logger.LogDebug("Fetching Open-Meteo forecast: {Url}", url);

        OpenMeteoResponse? apiResponse;
        try
        {
            using var httpResponse = await s_httpClient.GetAsync(url, cancellationToken);
            httpResponse.EnsureSuccessStatusCode();

            using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
            apiResponse = await JsonSerializer.DeserializeAsync(stream, OpenMeteoJsonContext.Default.OpenMeteoResponse, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Open-Meteo API request failed");

            // Fall back to stale cache when offline
            if (cached is { data: { } staleData })
            {
                Logger.LogInformation("Using stale weather cache (offline fallback)");
                return staleData;
            }
            return [];
        }

        if (apiResponse is null)
        {
            return cached?.data ?? [];
        }

        // Update current conditions
        UpdateCurrentConditions(apiResponse.Current);

        // Parse hourly data
        var forecasts = ParseHourlyData(apiResponse.Hourly, start, end);

        // Cache result (non-fatal — don't lose forecast data if caching fails)
        await Logger.CatchAsync(
            ct => _external.AtomicWriteJsonAsync(cacheFile, forecasts, OpenMeteoJsonContext.Default.ListHourlyWeatherForecast, ct),
            cancellationToken);

        return forecasts;
    }

    /// <summary>
    /// Tries to load cached forecast data. Returns null if no cache file exists.
    /// When a cache file exists, returns the data and whether it's within the TTL (fresh).
    /// Stale cache data is still returned so callers can use it as an offline fallback.
    /// </summary>
    private async Task<(List<HourlyWeatherForecast> data, bool fresh)?> TryLoadCacheAsync(string cacheFile, CancellationToken ct)
    {
        if (!File.Exists(cacheFile))
        {
            return null;
        }

        var data = await _external.TryReadJsonAsync(cacheFile, OpenMeteoJsonContext.Default.ListHourlyWeatherForecast, Logger, ct);
        if (data is null)
        {
            return null;
        }

        var fileInfo = new FileInfo(cacheFile);
        var age = TimeProvider.GetUtcNow() - new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);
        var fresh = age <= CacheTtl;

        if (fresh)
        {
            Logger.LogDebug("Using cached weather forecast: {CacheFile}", cacheFile);
        }

        return (data, fresh);
    }

    private void UpdateCurrentConditions(OpenMeteoCurrentData? current)
    {
        if (current is null)
        {
            return;
        }

        _cloudCover = current.CloudCover;
        _temperature = current.Temperature2m;
        _humidity = current.RelativeHumidity2m;
        _pressure = current.SurfacePressure;
        _windDirection = current.WindDirection10m;
        _windSpeed = current.WindSpeed10m / 3.6; // km/h → m/s
        _windGust = current.WindGusts10m / 3.6;
        _rainRate = current.Precipitation;

        // Approximate dew point using the Magnus formula
        _dewPoint = ApproximateDewPoint(_temperature, _humidity);
    }

    private static List<HourlyWeatherForecast> ParseHourlyData(OpenMeteoHourlyData? hourly, DateTimeOffset start, DateTimeOffset end)
    {
        if (hourly?.Time is not { Count: > 0 } times)
        {
            return [];
        }

        var count = times.Count;
        var result = new List<HourlyWeatherForecast>(count);

        for (var i = 0; i < count; i++)
        {
            if (!DateTimeOffset.TryParse(times[i], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            {
                continue;
            }

            if (time < start || time > end)
            {
                continue;
            }

            result.Add(new HourlyWeatherForecast(
                Time: time,
                CloudCover: GetValue(hourly.CloudCover, i),
                Precipitation: GetValue(hourly.Precipitation, i),
                Temperature: GetValue(hourly.Temperature2m, i),
                Humidity: GetValue(hourly.RelativeHumidity2m, i),
                DewPoint: GetValue(hourly.DewPoint2m, i),
                WindSpeed: GetValue(hourly.WindSpeed10m, i) / 3.6, // km/h → m/s
                WindGust: GetValue(hourly.WindGusts10m, i) / 3.6,
                WindDirection: GetValue(hourly.WindDirection10m, i),
                Visibility: GetValue(hourly.Visibility, i),
                WeatherCode: GetIntValue(hourly.WeatherCode, i)
            ));
        }

        return result;
    }

    private static double GetValue(List<double>? list, int index)
        => list is not null && index < list.Count ? list[index] : double.NaN;

    private static int GetIntValue(List<int>? list, int index)
        => list is not null && index < list.Count ? list[index] : 0;

    /// <summary>
    /// Approximates dew point using the Magnus formula.
    /// </summary>
    private static double ApproximateDewPoint(double temperatureC, double humidityPercent)
    {
        if (double.IsNaN(temperatureC) || double.IsNaN(humidityPercent) || humidityPercent <= 0)
        {
            return double.NaN;
        }

        const double a = 17.27;
        const double b = 237.7;
        var alpha = a * temperatureC / (b + temperatureC) + Math.Log(humidityPercent / 100.0);
        return b * alpha / (a - alpha);
    }

    // --- IDisposable ---
    public void Dispose() { }
    public ValueTask DisposeAsync() => DisconnectAsync();
}
