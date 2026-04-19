using TianWen.Lib.Devices.Weather;

namespace TianWen.Hosting.Dto.NinaV2;

/// <summary>
/// Weather info DTO matching ninaAPI v2 <c>/v2/api/equipment/weather/info</c> response shape.
/// </summary>
public sealed class NinaWeatherInfoDto
{
    public required bool Connected { get; init; }
    public required double CloudCover { get; init; }
    public required double DewPoint { get; init; }
    public required double Humidity { get; init; }
    public required double Pressure { get; init; }
    public required double RainRate { get; init; }
    public required double SkyQuality { get; init; }
    public required double SkyTemperature { get; init; }
    public required double StarFWHM { get; init; }
    public required double Temperature { get; init; }
    public required double WindDirection { get; init; }
    public required double WindGust { get; init; }
    public required double WindSpeed { get; init; }

    public static NinaWeatherInfoDto FromDriver(IWeatherDriver driver)
    {
        return new NinaWeatherInfoDto
        {
            Connected = driver.Connected,
            CloudCover = driver.CloudCover,
            DewPoint = driver.DewPoint,
            Humidity = driver.Humidity,
            Pressure = driver.Pressure,
            RainRate = driver.RainRate,
            SkyQuality = driver.SkyQuality,
            SkyTemperature = driver.SkyTemperature,
            StarFWHM = driver.StarFWHM,
            Temperature = driver.Temperature,
            WindDirection = driver.WindDirection,
            WindGust = driver.WindGust,
            WindSpeed = driver.WindSpeed,
        };
    }

    public static NinaWeatherInfoDto Disconnected { get; } = new NinaWeatherInfoDto
    {
        Connected = false, CloudCover = double.NaN, DewPoint = double.NaN, Humidity = double.NaN,
        Pressure = double.NaN, RainRate = double.NaN, SkyQuality = double.NaN,
        SkyTemperature = double.NaN, StarFWHM = double.NaN, Temperature = double.NaN,
        WindDirection = double.NaN, WindGust = double.NaN, WindSpeed = double.NaN,
    };
}
