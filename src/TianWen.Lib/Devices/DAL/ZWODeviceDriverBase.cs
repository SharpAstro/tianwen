using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;

namespace TianWen.Lib.Devices.DAL;

internal abstract class DALDeviceDriverBase<TDevice, TDeviceInfo>(TDevice device, IExternal external) : DeviceDriverBase<TDevice, TDeviceInfo>(device, external)
    where TDevice : DeviceBase
    where TDeviceInfo : struct, INativeDeviceInfo
{
    protected abstract INativeDeviceIterator<TDeviceInfo> NewIterator();

    protected override Task<(bool Success, int ConnectionId, TDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        var deviceIterator = NewIterator();
        var searchId = _device.DeviceId;

        foreach (var (deviceId, deviceInfo) in deviceIterator)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            bool needsClosing = false;
            try
            {
                var isOpen = deviceInfo.Open();
                if (isOpen && (IsSameSerialNumber(deviceInfo) || IsSameCustomId(deviceInfo) || IsSameName(deviceInfo)))
                {
                    needsClosing = false;
                    return Task.FromResult((true, deviceId, deviceInfo));
                }
                else if (isOpen)
                {
                    needsClosing = true;
                }
            }
            finally
            {
                if (needsClosing)
                {
                    deviceInfo.Close();
                }
            }
        }

        return Task.FromResult((false, CONNECTION_ID_UNKNOWN, default(TDeviceInfo)));

        bool IsSameSerialNumber(in TDeviceInfo deviceInfo) => deviceInfo.SerialNumber is { Length: > 0 } serialNumber && serialNumber == searchId;

        bool IsSameCustomId(in TDeviceInfo deviceInfo) => deviceInfo.IsUSB3Device && deviceInfo.CustomId is { Length: > 0 } customId && customId == searchId;

        bool IsSameName(in TDeviceInfo deviceInfo) => deviceInfo.Name is { Length: > 0 } name && name == searchId;
    }

    protected override Task<bool> DoDisconnectDeviceAsync(int connectionId, CancellationToken cancellationToken) => Task.FromResult(_deviceInfo.Close());
}
