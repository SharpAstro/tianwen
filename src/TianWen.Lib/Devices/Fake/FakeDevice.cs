using System;
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

    protected override IDeviceDriver? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        DeviceType.Camera => new FakeCameraDriver(this, external),
        DeviceType.FilterWheel => new FakeFilterWheelDriver(this, external),
        DeviceType.Focuser => new FakeFocuserDriver(this, external),
        DeviceType.DedicatedGuiderSoftware => new FakeGuider(this, external),
        DeviceType.Mount => new FakeMeadeLX200ProtocolMountDriver(this, external),
        _ => null
    };

    public override ISerialConnection? ConnectSerialDevice(IExternal external, int baud = 9600, Encoding? encoding = null, TimeSpan? ioTimeout = null) => DeviceType switch
    {
        DeviceType.Mount => new FakeMeadeLX200SerialDevice(external.AppLogger, encoding ?? Encoding.Latin1, external.TimeProvider, SiteLatitude, SiteLongitude, true),
        _ => null
    };

    [JsonIgnore]
    private double SiteLatitude => double.TryParse(Query["latitude"], out var latitude) ? latitude : throw new InvalidOperationException("Failed to parse latitude");

    [JsonIgnore]
    private double SiteLongitude => double.TryParse(Query["longitude"], out var latitude) ? latitude : throw new InvalidOperationException("Failed to parse longitude");

}
