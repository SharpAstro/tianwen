using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Astap.Lib.Devices;

public class DeviceMap<TDevice>
    where TDevice : DeviceBase
{
    private readonly Dictionary<string, TDevice> _deviceIdToDevice = [];
    private readonly Dictionary<DeviceType, List<TDevice>> _devicesByType = [];

    public DeviceMap(IDeviceSource<TDevice> source)
    {
        var types = source.RegisteredDeviceTypes;

        foreach (var type in types)
        {
            var deviceList = _devicesByType.GetOrAdd(type, []);

            foreach (var device in source.RegisteredDevices(type))
            {
                _deviceIdToDevice.Add(device.DeviceId, device);

                deviceList.Add(device);
            }
        }
    }

    public bool TryFindByDeviceId(string deviceId, [NotNullWhen(true)] out TDevice? device) => _deviceIdToDevice.TryGetValue(deviceId, out device);

    public IReadOnlyCollection<TDevice> FindAllByType(DeviceType type) => _devicesByType.TryGetValue(type, out var list) ? list : [];
}
