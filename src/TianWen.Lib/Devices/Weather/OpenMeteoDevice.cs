using System;
using TianWen.Lib;

namespace TianWen.Lib.Devices.Weather;

/// <summary>
/// Device record for the built-in Open-Meteo weather forecast service.
/// Always available (free API, no key required). Lat/lon is resolved from the mount in the profile.
/// </summary>
public record class OpenMeteoDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    private const string DefaultDeviceId = "openmeteo";
    private const string DefaultDisplayName = "Open-Meteo";

    public OpenMeteoDevice()
        : this(new Uri($"{DeviceType.Weather}://{typeof(OpenMeteoDevice).Name}/{DefaultDeviceId}#{DefaultDisplayName}"))
    {
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Weather => new OpenMeteoDriver(this, sp.External),
        _ => null
    };
}
