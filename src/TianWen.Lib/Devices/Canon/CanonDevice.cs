using FC.SDK;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib;

namespace TianWen.Lib.Devices.Canon;

/// <summary>
/// Device record for Canon DSLR cameras connected via WPD, USB, or WiFi (PTP/IP).
/// URI format: <c>Camera://CanonDevice/{id}?port={wpd|usb|wifi}&amp;host={ipAddr}#{modelName}</c>. Over WPD the id is the
/// body's serial and the current WPD path rides in <c>wpd=</c>: <c>Camera://CanonDevice/{serial}?port=wpd&amp;wpd={path}#{modelName}</c>
/// (#1097). A camera whose serial could not be read keeps the older form, the WPD path itself as the id.
/// </summary>
public record class CanonDevice(Uri DeviceUri) : DeviceBase(DeviceUri), IDeviceWithGainModes, IUncooledCamera
{
    /// <summary>Whether this device connects over WiFi (PTP/IP) rather than USB.</summary>
    public bool IsWifi => DeviceUri.QueryValue(DeviceQueryKey.Port) == "wifi";

    /// <summary>Whether this device connects via WPD (Windows Portable Devices).</summary>
    public bool IsWpd => DeviceUri.QueryValue(DeviceQueryKey.Port) == "wpd";

    /// <summary>
    /// The id as the transport wants it. <see cref="DeviceBase.DeviceId"/> is the URI's path, which is still percent-escaped:
    /// a WPD path or a USB device path reached the transport as <c>%5C%5C%3F%5Cusb%23...</c>, which WPD rejects (#1096).
    /// </summary>
    public string RawDeviceId => Uri.UnescapeDataString(DeviceId);

    /// <summary>
    /// The WPD device id to open: the current path from the <c>wpd</c> query for a camera keyed by its serial, else the
    /// unescaped path id of the older form.
    /// </summary>
    public string WpdDeviceId => DeviceUri.QueryValue(DeviceQueryKey.WpdDeviceId) is { Length: > 0 } path ? path : RawDeviceId;

    /// <summary>WiFi host/IP address, read from the <c>host</c> query parameter.</summary>
    public string? WifiHost => DeviceUri.QueryValue(DeviceQueryKey.Host);

    /// <summary>
    /// Whether each exposure is taken with the mirror locked up first (the <c>mirrorLockup</c> setting; on unless it says false):
    /// the body raises the mirror, lets it settle for its 2 s self-timer and exposes on its own. Up to 30 s only; a bulb
    /// exposure is taken without it.
    /// </summary>
    public bool MirrorLockup => !bool.TryParse(DeviceUri.QueryValue(DeviceQueryKey.MirrorLockup), out var on) || on;

    /// <summary>
    /// Whether the connect fixes the body's white balance at Daylight (the <c>daylightWhiteBalance</c> setting; on unless it says
    /// false). A frame carries the as-shot white balance in its pixels, so under Auto each frame is scaled per colour by what
    /// the body chose for it, a dark as much as a light.
    /// </summary>
    public bool DaylightWhiteBalance => !bool.TryParse(DeviceUri.QueryValue(DeviceQueryKey.DaylightWhiteBalance), out var on) || on;

    /// <summary>
    /// Settings surface, applied when the camera connects: mirror lockup and white balance, and the WiFi host address
    /// (editable via StringEditor in the equipment tab, only visible when <c>port=wifi</c>).
    /// </summary>
    public override ImmutableArray<DeviceSettingDescriptor> Settings { get; } =
    [
        DeviceSettingHelper.BoolSetting(
            DeviceQueryKey.MirrorLockup.Key, "Mirror lockup",
            defaultValue: true, trueLabel: "On (2 s settle)", falseLabel: "Off"),
        DeviceSettingHelper.BoolSetting(
            DeviceQueryKey.DaylightWhiteBalance.Key, "White balance",
            defaultValue: true, trueLabel: "Daylight (fixed)", falseLabel: "As set on the camera"),
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
