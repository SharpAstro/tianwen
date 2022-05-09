using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Astap.Lib;

public record class AscomDevice(string ProgId, string DeviceType, string DisplayName);

public class AscomDeviceMap
{
    private readonly Dictionary<string, AscomDevice> _progIdToDevice = new();
    private readonly Dictionary<string, List<AscomDevice>> _devicesByType = new();

    public AscomDeviceMap(AscomProfile ascomProfile)
    {
        var types = ascomProfile.RegisteredDeviceTypes;

        foreach (var type in types)
        {
            if (!_devicesByType.TryGetValue(type, out var typeDeviceList))
            {
                typeDeviceList = _devicesByType[type] = new List<AscomDevice>();
            }

            foreach (var (progId, displayName) in ascomProfile.RegisteredDevices(type))
            {
                var device = new AscomDevice(progId, type, displayName);
                _progIdToDevice.Add(progId, device);

                typeDeviceList.Add(device);
            }
        }
    }

    public bool TryFindByProgId(string progId, [NotNullWhen(true)] out AscomDevice? device) => _progIdToDevice.TryGetValue(progId, out device);

    public IReadOnlyCollection<AscomDevice> FindAllByType(string type) => _devicesByType.TryGetValue(type, out var list) ? list : Array.Empty<AscomDevice>();
}
