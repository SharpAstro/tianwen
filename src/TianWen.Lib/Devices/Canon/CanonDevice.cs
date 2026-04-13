using FC.SDK;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib;

namespace TianWen.Lib.Devices.Canon;

/// <summary>
/// Device record for Canon DSLR cameras connected via WPD, USB, or WiFi (PTP/IP).
/// URI format: <c>Camera://CanonDevice/{id}?port={wpd|usb|wifi}&amp;host={ipAddr}#{modelName}</c>
/// </summary>
public record class CanonDevice(Uri DeviceUri) : DeviceBase(DeviceUri), IDeviceWithGainModes, IUncooledCamera
{
    /// <summary>Whether this device connects over WiFi (PTP/IP) rather than USB.</summary>
    public bool IsWifi => DeviceUri.QueryValue(DeviceQueryKey.Port) == "wifi";

    /// <summary>Whether this device connects via WPD (Windows Portable Devices).</summary>
    public bool IsWpd => DeviceUri.QueryValue(DeviceQueryKey.Port) == "wpd";

    /// <summary>WiFi host/IP address, read from the <c>host</c> query parameter.</summary>
    public string? WifiHost => DeviceUri.QueryValue(DeviceQueryKey.Host);

    /// <summary>
    /// Settings surface: WiFi host address is editable via StringEditor in the equipment tab.
    /// Only visible when <c>port=wifi</c>.
    /// </summary>
    public override ImmutableArray<DeviceSettingDescriptor> Settings { get; } =
    [
        DeviceSettingHelper.StringSetting(
            DeviceQueryKey.Host.Key, "WiFi Host / IP",
            placeholder: "Camera IP address...",
            isVisible: uri => uri.QueryValue(DeviceQueryKey.Port) == "wifi"),
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> GainModes { get; } =
    [
        "ISO 100", "ISO 125", "ISO 160", "ISO 200", "ISO 250", "ISO 320",
        "ISO 400", "ISO 500", "ISO 640", "ISO 800", "ISO 1000", "ISO 1250",
        "ISO 1600", "ISO 2000", "ISO 2500", "ISO 3200", "ISO 4000", "ISO 5000",
        "ISO 6400", "ISO 8000", "ISO 10000", "ISO 12800", "ISO 16000", "ISO 20000",
        "ISO 25600",
    ];

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Camera => new CanonCameraDriver(this, sp, sp.GetRequiredService<CanonCameraFactory>()),
        _ => null
    };
}
