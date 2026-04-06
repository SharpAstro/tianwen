using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Weather;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// Fake weather driver for testing. Returns deterministic synthetic weather data
/// with a clear→cloudy→rainy→foggy→clear cycle over the night.
/// </summary>
internal sealed class FakeWeatherDriver(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), IWeatherDriver
{
    public double CloudCover => 45.0;
    public double Temperature => 12.5;
    public double Humidity => 68.0;
    public double DewPoint => 6.8;
    public double Pressure => 1013.25;
    public double WindSpeed => 3.2;
    public double WindGust => 5.1;
    public double WindDirection => 225.0;
    public double RainRate => 0.0;
    public double SkyQuality => double.NaN;
    public double SkyTemperature => double.NaN;
    public double StarFWHM => double.NaN;

    public Task<IReadOnlyList<HourlyWeatherForecast>> GetHourlyForecastAsync(
        double latitude, double longitude,
        DateTimeOffset start, DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        var forecasts = new List<HourlyWeatherForecast>();

        // Generate hourly entries with a synthetic weather pattern
        var current = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, 0, 0, start.Offset);
        if (current < start)
        {
            current = current.AddHours(1);
        }

        var totalHours = (int)(end - start).TotalHours + 1;
        for (var i = 0; current <= end && i < totalHours; i++, current = current.AddHours(1))
        {
            // Cycle: clear → partly → overcast → rain → fog → clear
            var phase = i % 12;
            var (cloud, precip, visibility, weatherCode) = phase switch
            {
                0 or 1 or 2 => (5.0, 0.0, 30000.0, 0),         // Clear
                3 or 4 => (30.0, 0.0, 20000.0, 2),              // Partly cloudy
                5 or 6 => (75.0, 0.0, 10000.0, 3),              // Overcast
                7 or 8 => (90.0, 1.5, 5000.0, 61),              // Rain
                9 => (95.0, 0.0, 500.0, 45),                    // Fog
                _ => (8.0, 0.0, 25000.0, 0),                    // Clear
            };

            forecasts.Add(new HourlyWeatherForecast(
                Time: current,
                CloudCover: cloud,
                Precipitation: precip,
                Temperature: 12.0 - i * 0.3,
                Humidity: 65.0 + phase * 2.0,
                DewPoint: 5.0,
                WindSpeed: 2.0 + phase * 0.3,
                WindGust: 4.0 + phase * 0.5,
                WindDirection: 220.0,
                Visibility: visibility,
                WeatherCode: weatherCode
            ));
        }

        return Task.FromResult<IReadOnlyList<HourlyWeatherForecast>>(forecasts);
    }
}
