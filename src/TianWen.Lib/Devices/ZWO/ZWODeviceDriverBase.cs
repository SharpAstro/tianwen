using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using ZWOptical.SDK;

namespace TianWen.Lib.Devices.ZWO;

internal abstract class ZWODeviceDriverBase<TDeviceInfo>(ZWODevice device, IExternal external) : DeviceDriverBase<ZWODevice, TDeviceInfo>(device, external)
    where TDeviceInfo : struct, INativeDeviceInfo
{
    public override string? DriverInfo => $"ZWO Driver v{DriverVersion}";

    protected override Task<(bool Success, int ConnectionId, TDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        var deviceIterator = new DeviceIterator<TDeviceInfo>();
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
