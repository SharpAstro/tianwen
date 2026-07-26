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
            CloudCover = JsonNumber.ForWire(driver.CloudCover),
            DewPoint = JsonNumber.ForWire(driver.DewPoint),
            Humidity = JsonNumber.ForWire(driver.Humidity),
            Pressure = JsonNumber.ForWire(driver.Pressure),
            RainRate = JsonNumber.ForWire(driver.RainRate),
            SkyQuality = JsonNumber.ForWire(driver.SkyQuality),
            SkyTemperature = JsonNumber.ForWire(driver.SkyTemperature),
            StarFWHM = JsonNumber.ForWire(driver.StarFWHM),
            Temperature = JsonNumber.ForWire(driver.Temperature),
            WindDirection = JsonNumber.ForWire(driver.WindDirection),
            WindGust = JsonNumber.ForWire(driver.WindGust),
            WindSpeed = JsonNumber.ForWire(driver.WindSpeed),
        };
    }

    public static NinaWeatherInfoDto Disconnected { get; } = new NinaWeatherInfoDto
    {
        Connected = false, CloudCover = JsonNumber.Unknown, DewPoint = JsonNumber.Unknown, Humidity = JsonNumber.Unknown,
        Pressure = JsonNumber.Unknown, RainRate = JsonNumber.Unknown, SkyQuality = JsonNumber.Unknown,
        SkyTemperature = JsonNumber.Unknown, StarFWHM = JsonNumber.Unknown, Temperature = JsonNumber.Unknown,
        WindDirection = JsonNumber.Unknown, WindGust = JsonNumber.Unknown, WindSpeed = JsonNumber.Unknown,
    };
}
