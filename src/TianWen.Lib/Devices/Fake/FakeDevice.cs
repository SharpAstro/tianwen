using System;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json.Serialization;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Fake;

public record FakeDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    /// <summary>
    /// Represents a fake device (for testing and simulation).
    /// </summary>
    /// <param name="deviceType"></param>
    /// <param name="deviceId">Fake device id (starting from 1)</param>
    public FakeDevice(DeviceType deviceType, int deviceId, NameValueCollection? values = null)
        : this(new Uri($"{deviceType}://{typeof(FakeDevice).Name}/Fake{deviceType}{deviceId}{(values is { Count: > 0 } ? "?" + values.ToQueryString() : "")}#Fake {deviceType.PascalCaseStringToName()} {deviceId}"))
    {
        // calls primary constructor
    }

    private static readonly ImmutableArray<DeviceSettingDescriptor> CameraSettings =
    [
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.PePeriodSeconds.Key, "PE Period",
            defaultValue: 600, min: 60, max: 3600, step: 60,
            suffix: "s"),
        DeviceSettingHelper.FloatSetting(
            DeviceQueryKey.PePeakTopeakArcsec.Key, "PE Amplitude",
            defaultValue: 20.0, min: 0.0, max: 120.0, step: 2.0,
            format: "F1", suffix: "\""),
    ];

    private static readonly ImmutableArray<DeviceSettingDescriptor> FocuserSettings =
    [
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.FocuserInitialPosition.Key, "Initial Pos",
            defaultValue: 980, min: 0, max: 2000, step: 10,
            suffix: " steps"),
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.FocuserBestFocus.Key, "Best Focus",
            defaultValue: 1000, min: 0, max: 2000, step: 50,
            suffix: " steps"),
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.FocuserBacklashIn.Key, "Backlash In",
            defaultValue: 20, min: 0, max: 200, step: 5,
            suffix: " steps"),
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.FocuserBacklashOut.Key, "Backlash Out",
            defaultValue: 15, min: 0, max: 200, step: 5,
            suffix: " steps"),
    ];

    public override ImmutableArray<DeviceSettingDescriptor> Settings => DeviceType switch
    {
        DeviceType.Camera => CameraSettings,
        DeviceType.Focuser => FocuserSettings,
        _ => [],
    };

    protected override IDeviceDriver? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        DeviceType.Camera => new FakeCameraDriver(this, external),
        DeviceType.CoverCalibrator => new FakeCoverDriver(this, external),
        DeviceType.FilterWheel => new FakeFilterWheelDriver(this, external),
        DeviceType.Focuser => new FakeFocuserDriver(this, external),
        DeviceType.Guider => new FakeGuider(this, external),
        DeviceType.Mount => CreateMountDriver(external),
        DeviceType.Weather => new FakeWeatherDriver(this, external),
        _ => null
    };

    private IDeviceDriver CreateMountDriver(IExternal external)
    {
        var port = Query.QueryValue(DeviceQueryKey.Port);

        // If port=LX200 is specified, use the full Meade serial protocol stack.
        if (string.Equals(port, "LX200", StringComparison.OrdinalIgnoreCase))
        {
            return new FakeMeadeLX200ProtocolMountDriver(this, external);
        }

        // If port=SGP is specified, use the iOptron SkyGuider Pro serial protocol stack.
        if (string.Equals(port, "SGP", StringComparison.OrdinalIgnoreCase))
        {
            return new FakeSgpMountDriver(this, external);
        }

        // If port=SkyWatcher is specified, use the Skywatcher motor controller protocol stack.
        if (string.Equals(port, "SkyWatcher", StringComparison.OrdinalIgnoreCase))
        {
            return new FakeSkywatcherMountDriver(this, external);
        }

        // Otherwise use the lightweight direct driver.
        return new FakeMountDriver(this, external);
    }

    public override ISerialConnection? ConnectSerialDevice(IExternal external, int baud = 9600, Encoding? encoding = null) => DeviceType switch
    {
        DeviceType.Mount when string.Equals(Query.QueryValue(DeviceQueryKey.Port), "SGP", StringComparison.OrdinalIgnoreCase)
            => new FakeSgpSerialDevice(external.AppLogger, encoding ?? Encoding.ASCII, external.TimeProvider, SiteLatitude >= 0, true),
        DeviceType.Mount when string.Equals(Query.QueryValue(DeviceQueryKey.Port), "SkyWatcher", StringComparison.OrdinalIgnoreCase)
            => new FakeSkywatcherSerialDevice(external.AppLogger, encoding ?? Encoding.ASCII, external.TimeProvider, true),
        DeviceType.Mount
            => new FakeMeadeLX200SerialDevice(external.AppLogger, encoding ?? Encoding.Latin1, external.TimeProvider, SiteLatitude, SiteLongitude, true),
        _ => null
    };

    [JsonIgnore]
    private double SiteLatitude => double.TryParse(Query.QueryValue(DeviceQueryKey.Latitude), out var latitude) ? latitude : throw new InvalidOperationException("Failed to parse latitude");

    [JsonIgnore]
    private double SiteLongitude => double.TryParse(Query.QueryValue(DeviceQueryKey.Longitude), out var longitude) ? longitude : throw new InvalidOperationException("Failed to parse longitude");

}
