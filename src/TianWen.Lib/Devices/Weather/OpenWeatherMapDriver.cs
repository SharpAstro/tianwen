using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// Weather driver that fetches data from the OpenWeatherMap API.
/// Auto-detects the API version on connect: tries One Call 3.0 first (native hourly data),
/// falls back to the free 2.5 tier (3-hour forecasts interpolated to hourly).
/// Implements file-based caching (1-hour TTL) with stale-cache offline fallback.
/// </summary>
internal sealed class OpenWeatherMapDriver : IWeatherDriver
{
    private const string BaseUrl25 = "https://api.openweathermap.org/data/2.5";
    private const string BaseUrl30 = "https://api.openweathermap.org/data/3.0";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private static readonly HttpClient s_httpClient = new HttpClient()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly OpenWeatherMapDevice _device;
    private readonly IExternal _external;
    private readonly string _apiKey;
    private bool _connected;
    private bool _useOneCall30;

    // Current conditions
    private double _cloudCover = double.NaN;
    private double _temperature = double.NaN;
    private double _humidity = double.NaN;
    private double _dewPoint = double.NaN;
    private double _pressure = double.NaN;
    private double _windSpeed = double.NaN;
    private double _windGust = double.NaN;
    private double _windDirection = double.NaN;
    private double _rainRate = double.NaN;

    public OpenWeatherMapDriver(OpenWeatherMapDevice device, IServiceProvider serviceProvider)
    {
        _device = device;
        _external = serviceProvider.GetRequiredService<IExternal>();
        Logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(OpenWeatherMapDriver));
        TimeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        _apiKey = device.ApiKey ?? throw new ArgumentException("OpenWeatherMap API key is required");
    }

    // --- IDeviceDriver ---
    public string Name => _device.DisplayName;
    public string? Description => _useOneCall30 ? "OpenWeatherMap One Call 3.0" : "OpenWeatherMap 2.5";
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

        // Probe One Call 3.0 first — if the key supports it, we get native hourly data
        try
        {
            var testUrl30 = $"{BaseUrl30}/onecall?lat=0&lon=0&appid={_apiKey}&units=metric&exclude=minutely,daily,alerts";
            using var response30 = await s_httpClient.GetAsync(testUrl30, cancellationToken);
            if (response30.IsSuccessStatusCode)
            {
                _useOneCall30 = true;
                Logger.LogInformation("OpenWeatherMap: using One Call 3.0 API");
            }
            else if (response30.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Key doesn't have 3.0 access — fall back to 2.5
                _useOneCall30 = false;
                Logger.LogInformation("OpenWeatherMap: One Call 3.0 not available, using 2.5 API");

                // Validate 2.5 works
                var testUrl25 = $"{BaseUrl25}/weather?lat=0&lon=0&appid={_apiKey}&units=metric";
                using var response25 = await s_httpClient.GetAsync(testUrl25, cancellationToken);
                response25.EnsureSuccessStatusCode();
            }
            else
            {
                // Unexpected status — let it throw
                response30.EnsureSuccessStatusCode();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "OpenWeatherMap API connectivity check failed");
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
            $"owm_{latitude:F2}_{longitude:F2}_{startDate}_{endDate}.json");

        var cached = await TryLoadCacheAsync(cacheFile, cancellationToken);
        if (cached is { fresh: true })
        {
            return cached.Value.data;
        }

        var lat = latitude.ToString(CultureInfo.InvariantCulture);
        var lon = longitude.ToString(CultureInfo.InvariantCulture);

        Logger.LogDebug("Fetching OpenWeatherMap forecast for {Lat},{Lon} (v{Version})", lat, lon, _useOneCall30 ? "3.0" : "2.5");

        List<HourlyWeatherForecast> forecasts;
        try
        {
            forecasts = _useOneCall30
                ? await FetchOneCall30Async(lat, lon, start, end, cancellationToken)
                : await Fetch25Async(lat, lon, start, end, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "OpenWeatherMap API request failed");

            if (cached is { data: { } staleData })
            {
                Logger.LogInformation("Using stale weather cache (offline fallback)");
                return staleData;
            }
            return [];
        }

        if (forecasts.Count == 0)
        {
            return cached?.data ?? [];
        }

        // Cache result (non-fatal)
        await Logger.CatchAsync(
            ct => _external.AtomicWriteJsonAsync(cacheFile, forecasts, OpenMeteoJsonContext.Default.ListHourlyWeatherForecast, ct),
            cancellationToken);

        return forecasts;
    }

    /// <summary>
    /// Fetches from the One Call 3.0 API which provides native hourly data (48 hours) + current conditions.
    /// </summary>
    private async Task<List<HourlyWeatherForecast>> FetchOneCall30Async(
        string lat, string lon, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl30}/onecall?lat={lat}&lon={lon}&appid={_apiKey}&units=metric&exclude=minutely,daily,alerts";
        using var httpResponse = await s_httpClient.GetAsync(url, cancellationToken);
        httpResponse.EnsureSuccessStatusCode();

        using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        var response = await JsonSerializer.DeserializeAsync(stream, OpenWeatherMapJsonContext.Default.OwmOneCallResponse, cancellationToken);

        if (response is null)
        {
            return [];
        }

        // Update current conditions from the "current" block
        UpdateCurrentFromOneCall(response.Current);

        // Parse hourly entries (already hourly — no interpolation needed)
        if (response.Hourly is not { Count: > 0 })
        {
            return [];
        }

        var result = new List<HourlyWeatherForecast>(response.Hourly.Count);
        foreach (var entry in response.Hourly)
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(entry.Dt);
            if (time < start)
            {
                continue;
            }
            if (time > end)
            {
                break;
            }

            result.Add(OneCallEntryToForecast(entry, time));
        }

        return result;
    }

    /// <summary>
    /// Fetches from the 2.5 API: /weather for current + /forecast for 5-day/3-hour, interpolated to hourly.
    /// </summary>
    private async Task<List<HourlyWeatherForecast>> Fetch25Async(
        string lat, string lon, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        // Fetch current weather
        var currentUrl = $"{BaseUrl25}/weather?lat={lat}&lon={lon}&appid={_apiKey}&units=metric";
        using (var currentHttpResponse = await s_httpClient.GetAsync(currentUrl, cancellationToken))
        {
            currentHttpResponse.EnsureSuccessStatusCode();
            using var currentStream = await currentHttpResponse.Content.ReadAsStreamAsync(cancellationToken);
            var currentResponse = await JsonSerializer.DeserializeAsync(currentStream, OpenWeatherMapJsonContext.Default.OwmCurrentResponse, cancellationToken);
            UpdateCurrentFrom25(currentResponse);
        }

        // Fetch 5-day/3-hour forecast
        var forecastUrl = $"{BaseUrl25}/forecast?lat={lat}&lon={lon}&appid={_apiKey}&units=metric";
        using var forecastHttpResponse = await s_httpClient.GetAsync(forecastUrl, cancellationToken);
        forecastHttpResponse.EnsureSuccessStatusCode();
        using var forecastStream = await forecastHttpResponse.Content.ReadAsStreamAsync(cancellationToken);
        var forecastResponse = await JsonSerializer.DeserializeAsync(forecastStream, OpenWeatherMapJsonContext.Default.OwmForecastResponse, cancellationToken);

        if (forecastResponse?.List is not { Count: > 0 })
        {
            return [];
        }

        return InterpolateToHourly(forecastResponse.List, start, end);
    }

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
            Logger.LogDebug("Using cached OWM weather forecast: {CacheFile}", cacheFile);
        }

        return (data, fresh);
    }

    private void UpdateCurrentFrom25(OwmCurrentResponse? current)
    {
        if (current is null)
        {
            return;
        }

        _cloudCover = current.Clouds?.All ?? double.NaN;
        _temperature = current.Main?.Temp ?? double.NaN;
        _humidity = current.Main?.Humidity ?? double.NaN;
        _pressure = current.Main?.Pressure ?? double.NaN;
        _windSpeed = current.Wind?.Speed ?? double.NaN;
        _windGust = current.Wind?.Gust ?? double.NaN;
        _windDirection = current.Wind?.Deg ?? double.NaN;
        _rainRate = current.Rain?.OneHour ?? 0.0;
        _dewPoint = ApproximateDewPoint(_temperature, _humidity);
    }

    private void UpdateCurrentFromOneCall(OwmOneCallCurrentData? current)
    {
        if (current is null)
        {
            return;
        }

        _cloudCover = current.Clouds;
        _temperature = current.Temp;
        _humidity = current.Humidity;
        _pressure = current.Pressure;
        _windSpeed = current.WindSpeed;
        _windGust = current.WindGust;
        _windDirection = current.WindDeg;
        _dewPoint = current.DewPoint;
        _rainRate = current.Rain?.OneHour ?? 0.0;
    }

    private static HourlyWeatherForecast OneCallEntryToForecast(OwmOneCallHourlyEntry entry, DateTimeOffset time)
    {
        return new HourlyWeatherForecast(
            Time: time,
            CloudCover: entry.Clouds,
            Precipitation: entry.Rain?.OneHour ?? 0.0,
            Temperature: entry.Temp,
            Humidity: entry.Humidity,
            DewPoint: entry.DewPoint,
            WindSpeed: entry.WindSpeed,
            WindGust: entry.WindGust,
            WindDirection: entry.WindDeg,
            Visibility: entry.Visibility,
            WeatherCode: OwmIdToWmoCode(entry.Weather is { Count: > 0 } ? entry.Weather[0].Id : 0)
        );
    }

    /// <summary>
    /// Interpolates 3-hour OWM 2.5 forecast entries to hourly resolution.
    /// For each 3-hour gap, two intermediate hours are linearly interpolated.
    /// </summary>
    private static List<HourlyWeatherForecast> InterpolateToHourly(List<OwmForecastEntry> entries, DateTimeOffset start, DateTimeOffset end)
    {
        var result = new List<HourlyWeatherForecast>(entries.Count * 3);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var time = DateTimeOffset.FromUnixTimeSeconds(entry.Dt);

            if (time > end)
            {
                break;
            }

            var forecast = ForecastEntryToHourly(entry, time);

            if (time >= start)
            {
                result.Add(forecast);
            }

            // Interpolate two intermediate hours if there's a next entry
            if (i + 1 < entries.Count)
            {
                var nextEntry = entries[i + 1];
                var nextTime = DateTimeOffset.FromUnixTimeSeconds(nextEntry.Dt);
                var nextForecast = ForecastEntryToHourly(nextEntry, nextTime);

                for (var h = 1; h <= 2; h++)
                {
                    var interpTime = time.AddHours(h);
                    if (interpTime < start || interpTime > end)
                    {
                        continue;
                    }

                    var t = h / 3.0; // 1/3 or 2/3
                    result.Add(new HourlyWeatherForecast(
                        Time: interpTime,
                        CloudCover: Lerp(forecast.CloudCover, nextForecast.CloudCover, t),
                        Precipitation: Lerp(forecast.Precipitation, nextForecast.Precipitation, t),
                        Temperature: Lerp(forecast.Temperature, nextForecast.Temperature, t),
                        Humidity: Lerp(forecast.Humidity, nextForecast.Humidity, t),
                        DewPoint: Lerp(forecast.DewPoint, nextForecast.DewPoint, t),
                        WindSpeed: Lerp(forecast.WindSpeed, nextForecast.WindSpeed, t),
                        WindGust: Lerp(forecast.WindGust, nextForecast.WindGust, t),
                        WindDirection: LerpAngle(forecast.WindDirection, nextForecast.WindDirection, t),
                        Visibility: Lerp(forecast.Visibility, nextForecast.Visibility, t),
                        WeatherCode: forecast.WeatherCode // discrete — hold until next data point
                    ));
                }
            }
        }

        return result;
    }

    private static HourlyWeatherForecast ForecastEntryToHourly(OwmForecastEntry entry, DateTimeOffset time)
    {
        var temp = entry.Main?.Temp ?? double.NaN;
        var humidity = entry.Main?.Humidity ?? double.NaN;

        return new HourlyWeatherForecast(
            Time: time,
            CloudCover: entry.Clouds?.All ?? double.NaN,
            Precipitation: entry.Rain?.ThreeHour ?? 0.0,
            Temperature: temp,
            Humidity: humidity,
            DewPoint: ApproximateDewPoint(temp, humidity),
            WindSpeed: entry.Wind?.Speed ?? double.NaN,
            WindGust: entry.Wind?.Gust ?? double.NaN,
            WindDirection: entry.Wind?.Deg ?? double.NaN,
            Visibility: entry.Visibility,
            WeatherCode: OwmIdToWmoCode(entry.Weather is { Count: > 0 } ? entry.Weather[0].Id : 0)
        );
    }

    /// <summary>
    /// Maps OWM weather condition IDs to WMO 4677 codes for consistency with <see cref="HourlyWeatherForecast.WeatherCode"/>.
    /// </summary>
    private static int OwmIdToWmoCode(int owmId) => owmId switch
    {
        800 => 0,                                       // Clear
        801 => 1,                                       // Few clouds
        802 => 2,                                       // Scattered clouds
        803 or 804 => 3,                                // Broken/overcast
        701 or 711 or 721 or 731 or 741 => 45,          // Mist/smoke/haze/dust/fog
        762 or 771 or 781 => 48,                        // Volcanic ash/squalls/tornado → rime fog (closest)
        >= 300 and < 400 => 51,                         // Drizzle
        >= 500 and < 502 => 61,                         // Light/moderate rain
        >= 502 and < 600 => 63,                         // Heavy rain
        >= 600 and < 602 => 71,                         // Light/moderate snow
        >= 602 and < 700 => 75,                         // Heavy snow
        >= 200 and < 300 => 95,                         // Thunderstorm
        _ => 0
    };

    private static double Lerp(double a, double b, double t)
        => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : a + (b - a) * t;

    /// <summary>
    /// Linearly interpolates between two angles (in degrees), taking the shortest arc.
    /// </summary>
    private static double LerpAngle(double a, double b, double t)
    {
        if (double.IsNaN(a) || double.IsNaN(b))
        {
            return double.NaN;
        }

        var diff = ((b - a + 540) % 360) - 180;
        return (a + diff * t + 360) % 360;
    }

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
