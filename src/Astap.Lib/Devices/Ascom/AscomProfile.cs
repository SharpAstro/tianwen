using System;
using System.Collections.Generic;
using System.Linq;

namespace Astap.Lib.Devices.Ascom;

public class AscomProfile : DynamicComObject, IDeviceSource<AscomDevice>
{
    public AscomProfile() : base("ASCOM.Utilities.Profile") { }

    /// <summary>
    /// Returns true if COM object was initalised successfully.
    /// </summary>
    public bool IsSupported => _comObject is not null;

    public IEnumerable<DeviceType> RegisteredDeviceTypes => RegisteredDeviceTypesInternal.Select(DeviceTypeHelper.TryParseDeviceType);

    private IEnumerable<string> RegisteredDeviceTypesInternal => EnumerateProperty<string>(_comObject?.RegisteredDeviceTypes);

    private IEnumerable<KeyValuePair<string, string>> RegisteredDevicesKV(DeviceType deviceType) => EnumerateKeyValueProperty(_comObject?.RegisteredDevices(deviceType));

    public IEnumerable<AscomDevice> RegisteredDevices(DeviceType deviceType)
        => RegisteredDevicesKV(deviceType).Select(p => new AscomDevice(deviceType, p.Key, p.Value));
}
