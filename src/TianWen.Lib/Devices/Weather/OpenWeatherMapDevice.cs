using System;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// Device record for the OpenWeatherMap weather service.
/// Requires an API key, held in the OS credential vault (<see cref="ICredentialStore"/>) under
/// <see cref="CredentialKey"/> — NOT on the device URI — so it survives provider switches and
/// re-discovery. Entered via the device's settings in the Equipment tab.
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
    /// Credential-store key under which this device's API key is held (<c>{deviceId}/apiKey</c>).
    /// Stable across device-URI changes so the secret is not lost when the URI is replaced.
    /// </summary>
    public string CredentialKey => ICredentialStore.KeyFor(DeviceId, DeviceQueryKey.ApiKey.Key);

    public override ImmutableArray<DeviceSettingDescriptor> Settings { get; } =
    [
        DeviceSettingHelper.StringSetting(
            DeviceQueryKey.ApiKey.Key, "API Key",
            placeholder: "Enter OpenWeatherMap API key...",
            mask: true),
    ];

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        // The API key lives in the credential store (see CredentialKey), not the URI. Throw a
        // user-meaningful error rather than returning null silently — the connect path surfaces it
        // to the status bar; silent-fetch (FetchWeatherForecastAsync) wraps this in try/catch.
        DeviceType.Weather => sp.GetRequiredService<ICredentialStore>().Get(CredentialKey) is { Length: > 0 } apiKey
            ? new OpenWeatherMapDriver(this, sp, apiKey)
            : throw new InvalidOperationException(
                "OpenWeatherMap API key not set — open the device's settings in the Equipment tab and enter your key."),
        _ => null
    };
}
