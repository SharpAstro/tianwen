using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Astap.Lib.Devices;

public class DeviceMap<TDevice>
    where TDevice : DeviceBase
{
    private readonly Dictionary<string, TDevice> _deviceIdToDevice = new();
    private readonly Dictionary<string, List<TDevice>> _devicesByType = new();

    public DeviceMap(IDeviceSource<TDevice> source)
    {
        var types = source.RegisteredDeviceTypes;

        foreach (var type in types)
        {
            if (!_devicesByType.TryGetValue(type, out var typeDeviceList))
            {
                typeDeviceList = _devicesByType[type] = new List<TDevice>();
            }

            foreach (var device in source.RegisteredDevices(type))
            {
                _deviceIdToDevice.Add(device.DeviceId, device);

                typeDeviceList.Add(device);
            }
        }
    }

    public bool TryFindByProgId(string deviceId, [NotNullWhen(true)] out TDevice? device) => _deviceIdToDevice.TryGetValue(deviceId, out device);

    public IReadOnlyCollection<TDevice> FindAllByType(string type) => _devicesByType.TryGetValue(type, out var list) ? list : Array.Empty<TDevice>();
}
