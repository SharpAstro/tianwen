using ZWOptical.SDK;

namespace TianWen.Lib.Devices.ZWO;

internal abstract class ZWODeviceDriverBase<TDeviceInfo>(ZWODevice device, IExternal external) : DeviceDriverBase<ZWODevice, TDeviceInfo>(device, external)
    where TDeviceInfo : struct, IZWODeviceInfo
{
    public override string? DriverInfo => $"ZWO Driver v{DriverVersion}";

    protected override bool OnConnectDevice(out int connectionId, out TDeviceInfo connectedDeviceInfo)
    {
        var deviceIterator = new DeviceIterator<TDeviceInfo>();
        var searchId = _device.DeviceId;

        foreach (var (deviceId, deviceInfo) in deviceIterator)
        {
            bool hasOpened = false;
            try
            {
                hasOpened = deviceInfo.Open();
                if (hasOpened && (IsSameSerialNumber(deviceInfo) || IsSameCustomId(deviceInfo) || IsSameName(deviceInfo)))
                {
                    connectionId = deviceId;
                    connectedDeviceInfo = deviceInfo;

                    return true;
                }
            }
            finally
            {
                if (hasOpened)
                {
                    deviceInfo.Close();
                }
            }
        }

        connectionId = int.MinValue;
        connectedDeviceInfo = default;

        return false;

        bool IsSameSerialNumber(in TDeviceInfo deviceInfo) => deviceInfo.SerialNumber?.ToString() is { Length: > 0 } serialNumber && serialNumber == searchId;

        bool IsSameCustomId(in TDeviceInfo deviceInfo) => deviceInfo.IsUSB3Device && deviceInfo.CustomId is { Length: > 0 } customId && customId == searchId;

        bool IsSameName(in TDeviceInfo deviceInfo) => deviceInfo.Name is { Length: > 0 } name && name == searchId;
    }

    protected override bool OnDisconnectDevice(int connectionId) => _deviceInfo.Close();
}
