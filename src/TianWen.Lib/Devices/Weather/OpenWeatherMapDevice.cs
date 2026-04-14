using System;
using System.Collections.Immutable;
using TianWen.Lib;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// Device record for the OpenWeatherMap weather service.
/// Requires an API key stored as the <c>apiKey</c> query parameter on the device URI.
/// </summary>
public record class OpenWeatherMapDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    private const string DefaultDeviceId = "openweathermap";
    private const string DefaultDisplayName = "OpenWeatherMap";

    /// <summary>
    /// Creates a default OpenWeatherMap device with no API key.
    /// The key is supplied later via the equipment dialog and stored on the profile's device URI.
    /// </summary>
    public OpenWeatherMapDevice()
        : this(new Uri($"{DeviceType.Weather}://{typeof(OpenWeatherMapDevice).Name}/{DefaultDeviceId}#{DefaultDisplayName}"))
    {
    }

    /// <summary>
    /// The OpenWeatherMap API key, read from the <c>apiKey</c> query parameter.
    /// </summary>
    public string? ApiKey => DeviceUri.QueryValue(DeviceQueryKey.ApiKey);

    public override ImmutableArray<DeviceSettingDescriptor> Settings { get; } =
    [
        DeviceSettingHelper.StringSetting(
            DeviceQueryKey.ApiKey.Key, "API Key",
            placeholder: "Enter OpenWeatherMap API key...",
            mask: true),
    ];

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        // Throw a user-meaningful error rather than returning null silently — the connect path
        // surfaces the message to the status bar; silent-fetch (FetchWeatherForecastAsync) wraps
        // this in try/catch and just logs.
        DeviceType.Weather => ApiKey is { Length: > 0 }
            ? new OpenWeatherMapDriver(this, sp)
            : throw new InvalidOperationException(
                "OpenWeatherMap API key not set — open the device's settings in the Equipment tab and enter your key."),
        _ => null
    };
}
