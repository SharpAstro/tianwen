using System;
using System.Text.Json.Serialization;

namespace TianWen.Lib.Devices.Guider;

public record class GuiderDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    private (string Host, uint InstanceId, string? ProfileName)? _parsedDeviceId;

    public GuiderDevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(GuiderDevice).Name}/{deviceId}#{displayName}"))
    {

    }

    internal static (string Host, uint InstanceId, string? ProfileName) ParseDeviceId(string deviceId)
    {
        var deviceIdSplit = deviceId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (deviceIdSplit.Length < 2 || !IsValidHost(deviceIdSplit[0]))
        {
            throw new ArgumentException($"Could not parse hostname in {deviceId}", nameof(deviceId));
        }

        if (!uint.TryParse(deviceIdSplit[1], out uint instanceId))
        {
            throw new ArgumentException($"Could not parse instance id in {deviceId}", nameof(deviceId));
        }

        var host = deviceIdSplit[0];

        if (deviceIdSplit.Length > 2 && deviceIdSplit[2] is { Length: > 0 } profileName && !string.IsNullOrWhiteSpace(profileName))
        {
            return (host, instanceId, profileName.Trim());
        }
        else
        {
            return (host, instanceId, null);
        }
    }

    [JsonIgnore]
    public string Host => (_parsedDeviceId ??= ParseDeviceId(DeviceId)).Host;

    [JsonIgnore]
    public uint InstanceId => (_parsedDeviceId ??= ParseDeviceId(DeviceId)).InstanceId;

    [JsonIgnore]
    public string? ProfileName => (_parsedDeviceId ??= ParseDeviceId(DeviceId)).ProfileName;

    protected override IDeviceDriver? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        DeviceType.DedicatedGuiderSoftware => new PHD2GuiderDriver(this, external),
        _ => null
    };
}