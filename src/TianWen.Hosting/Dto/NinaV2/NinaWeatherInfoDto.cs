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
            // A weather driver reports NaN for anything its hardware does not measure, which is the
            // common case (most stations report a handful of these) -- see JsonNumber.
            CloudCover = JsonNumber.Finite(driver.CloudCover),
            DewPoint = JsonNumber.Finite(driver.DewPoint),
            Humidity = JsonNumber.Finite(driver.Humidity),
            Pressure = JsonNumber.Finite(driver.Pressure),
            RainRate = JsonNumber.Finite(driver.RainRate),
            SkyQuality = JsonNumber.Finite(driver.SkyQuality),
            SkyTemperature = JsonNumber.Finite(driver.SkyTemperature),
            StarFWHM = JsonNumber.Finite(driver.StarFWHM),
            Temperature = JsonNumber.Finite(driver.Temperature),
            WindDirection = JsonNumber.Finite(driver.WindDirection),
            WindGust = JsonNumber.Finite(driver.WindGust),
            WindSpeed = JsonNumber.Finite(driver.WindSpeed),
        };
    }

    public static NinaWeatherInfoDto Disconnected { get; } = new NinaWeatherInfoDto
    {
        Connected = false, CloudCover = 0, DewPoint = 0, Humidity = 0,
        Pressure = 0, RainRate = 0, SkyQuality = 0,
        SkyTemperature = 0, StarFWHM = 0, Temperature = 0,
        WindDirection = 0, WindGust = 0, WindSpeed = 0,
    };
}
