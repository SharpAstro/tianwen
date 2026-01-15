using System;
using System.Text;
using System.Text.Json.Serialization;
using TianWen.Lib.Devices.Guider;

namespace TianWen.Lib.Devices.OpenPHD2;

public record class OpenPHD2GuiderDevice(Uri DeviceUri) : GuiderDeviceBase(DeviceUri)
{
    private (string Host, uint InstanceId, string? ProfileName)? _parsedDeviceId;

    public OpenPHD2GuiderDevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(OpenPHD2GuiderDevice).Name}/{deviceId}#{displayName}"))
    {

    }

    internal static (string Host, uint InstanceId, string? ProfileName) ParseDeviceId(string deviceId)
    {
        var deviceIdSplit = deviceId.Split('/', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (deviceIdSplit.Length < 2 || !IsValidHost(deviceIdSplit[0]))
        {
            throw new ArgumentException($"Could not parse hostname in {deviceId}", nameof(deviceId));
        }

        if (!uint.TryParse(deviceIdSplit[1], out uint instanceId))
        {
            throw new ArgumentException($"Could not parse instance id in {deviceId}", nameof(deviceId));
        }

        var host = deviceIdSplit[0];

        if (deviceIdSplit.Length is 3 && deviceIdSplit[2] is { Length: > 0 } profileName && !string.IsNullOrWhiteSpace(profileName))
        {
            return (host, instanceId, profileName.Trim());
        }
        else
        {
            return (host, instanceId, null);
        }
    }

    internal OpenPHD2GuiderDevice WithProfile(string profileName)
    {
        var (host, instance, _) = _parsedDeviceId ??= ParseDeviceId(DeviceId);

        return new OpenPHD2GuiderDevice(DeviceType, $"{host}/{instance}/{profileName}", profileName);
    }

    protected override bool PrintMembers(StringBuilder stringBuilder) => base.PrintMembers(stringBuilder);

    [JsonIgnore]
    public string Host => (_parsedDeviceId ??= ParseDeviceId(DeviceId)).Host;

    [JsonIgnore]
    public uint InstanceId => (_parsedDeviceId ??= ParseDeviceId(DeviceId)).InstanceId;

    [JsonIgnore]
    public override string? ProfileName => (_parsedDeviceId ??= ParseDeviceId(DeviceId)).ProfileName;

    protected override IDeviceDriver? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        DeviceType.Guider => new OpenPHD2GuiderDriver(this, external),
        _ => null
    };
}