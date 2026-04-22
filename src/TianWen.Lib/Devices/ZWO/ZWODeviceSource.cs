using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using ZWOptical.SDK;
using static ZWOptical.SDK.ASICamera2;
using static ZWOptical.SDK.EAFFocuser1_6;
using static ZWOptical.SDK.EFW1_7;
using TianWen.DAL;

namespace TianWen.Lib.Devices.ZWO;

internal class ZWODeviceSource : IDeviceSource<ZWODevice>
{
    static readonly Dictionary<DeviceType, bool> _supportedDeviceTypes = [];

    static ZWODeviceSource()
    {
        CheckSupport(DeviceType.Camera, ASIGetSDKVersion);
        CheckSupport(DeviceType.Focuser, EAFGetSDKVersion);
        CheckSupport(DeviceType.FilterWheel, EFWGetSDKVersion);
    }

    private static void CheckSupport(DeviceType deviceType, Func<Version> sdkVersion)
    {
        bool isSupported;
        try
        {
            isSupported = sdkVersion().Major > 0;
        }
        catch
        {
            isSupported = false;
        }

        _supportedDeviceTypes[deviceType] = isSupported;
    }

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_supportedDeviceTypes.Count > 0);

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public IEnumerable<DeviceType> RegisteredDeviceTypes { get; } = _supportedDeviceTypes
        .Where(p => p.Value)
        .Select(p => p.Key)
        .ToList();

    public IEnumerable<ZWODevice> RegisteredDevices(DeviceType deviceType)
    {
        if (_supportedDeviceTypes.TryGetValue(deviceType, out var isSupported) && isSupported)
        {
            return deviceType switch
            {
                DeviceType.Camera => ListCameras(),
                DeviceType.Focuser => ListEAFs(),
                DeviceType.FilterWheel => ListEFWs(),
                _ => throw new ArgumentException($"Device type {deviceType} not implemented!", nameof(deviceType))
            };
        }
        else
        {
            return [];
        }
    }

    static IEnumerable<ZWODevice> ListCameras() => ListDevice<ASI_CAMERA_INFO>(DeviceType.Camera);

    static IEnumerable<ZWODevice> ListEAFs() => ListDevice<EAF_INFO>(DeviceType.Focuser);

    static IEnumerable<ZWODevice> ListEFWs() => ListDevice<EFW_INFO>(DeviceType.FilterWheel, SeedFilterParams);

    /// <summary>
    /// Builds query params seeding default filter names from the hardware slot count
    /// (available while the EFW is open during discovery).
    /// </summary>
    private static string? SeedFilterParams(EFW_INFO efwInfo)
    {
        var slotCount = efwInfo.NumberOfSlots;
        if (slotCount <= 0)
        {
            return null;
        }

        var parts = new string[slotCount];
        for (var i = 0; i < slotCount; i++)
        {
            parts[i] = $"{DeviceQueryKeyExtensions.FilterKey(i + 1)}={Uri.EscapeDataString($"Filter {i + 1}")}";
        }
        return string.Join("&", parts);
    }

    static IEnumerable<ZWODevice> ListDevice<TDeviceInfo>(DeviceType deviceType, Func<TDeviceInfo, string?>? seedQueryParams = null) where TDeviceInfo : struct, INativeDeviceInfo
    {
        var ids = new HashSet<int>();

        var iterator = new DeviceIterator<TDeviceInfo>();

        foreach (var deviceInfo in iterator)
        {
            if (!ids.Contains(deviceInfo.ID) && deviceInfo.Open())
            {
                try
                {
                    // ZWOptical.SDK returns null from SerialNumber / CustomId when
                    // the underlying 8-byte ZWO_ID is not a valid printable ASCII
                    // identifier (e.g. uninitialized / factory-default / binary).
                    var deviceId = deviceInfo.SerialNumber is { Length: > 0 } sn ? sn
                        : deviceInfo.IsUSB3Device && deviceInfo.CustomId is { Length: > 0 } cid ? cid
                        : deviceInfo.Name;

                    var extraQuery = seedQueryParams?.Invoke(deviceInfo);
                    var queryPart = extraQuery is { Length: > 0 } ? $"?{extraQuery}" : "";
                    // Escape both the deviceId and the fragment — even a valid ASCII
                    // id can contain '/', spaces, or other URI-unsafe characters (and
                    // product names definitely have spaces).
                    var uri = new Uri($"{deviceType}://{typeof(ZWODevice).Name}/{Uri.EscapeDataString(deviceId)}{queryPart}#{Uri.EscapeDataString(deviceInfo.Name)}");
                    yield return new ZWODevice(uri);

                    ids.Add(deviceInfo.ID);
                }
                finally
                {
                    _ = deviceInfo.Close();
                }
            }
        }
    }
}
