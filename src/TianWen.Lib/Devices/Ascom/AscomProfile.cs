using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Ascom;

internal class AscomProfile : DynamicComObject, IDeviceSource<AscomDevice>
{
    public AscomProfile() : base("ASCOM.Utilities.Profile") { }

    /// <summary>
    /// Returns true if COM object was initalised successfully.
    /// </summary>
    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_comObject is not null);

    public ValueTask DiscoverAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public IEnumerable<DeviceType> RegisteredDeviceTypes => RegisteredDeviceTypesInternal.Select(DeviceTypeHelper.TryParseDeviceType);

    private IEnumerable<string> RegisteredDeviceTypesInternal => EnumerateProperty<string>(_comObject?.RegisteredDeviceTypes);

    private IEnumerable<KeyValuePair<string, string>> RegisteredDevicesKV(DeviceType deviceType) => EnumerateKeyValueProperty(_comObject?.RegisteredDevices(deviceType.ToString()));

    public IEnumerable<AscomDevice> RegisteredDevices(DeviceType deviceType)
        => RegisteredDevicesKV(deviceType).Select(p => new AscomDevice(deviceType, p.Key, p.Value));
}
