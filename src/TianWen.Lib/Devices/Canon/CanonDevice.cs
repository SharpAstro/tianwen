using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Devices.Canon;

/// <summary>
/// Device record for Canon DSLR cameras connected via USB or WiFi (PTP/IP).
/// URI format: <c>Camera://CanonDevice/{serialOrGuid}?port={usb|wifi}&amp;host={ipAddr}#{modelName}</c>
/// </summary>
public record class CanonDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    /// <summary>Whether this device connects over WiFi (PTP/IP) rather than USB.</summary>
    public bool IsWifi => DeviceUri.QueryValue(DeviceQueryKey.Port) == "wifi";

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

    protected override IDeviceDriver? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        DeviceType.Camera => new CanonCameraDriver(this, external),
        _ => null
    };
}
