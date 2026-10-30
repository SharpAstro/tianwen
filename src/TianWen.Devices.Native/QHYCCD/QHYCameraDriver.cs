using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices.DAL;
using QHYCCD.SDK;
using static QHYCCD.SDK.QHYCamera;

namespace TianWen.Lib.Devices.QHYCCD;

internal class QHYCameraDriver(QHYDevice device, IServiceProvider sp) : DALCameraDriver<QHYDevice, QHYCCD_CAMERA_INFO>(device, sp)
{
    public override string? DriverInfo => $"QHY Camera Driver v{DriverVersion}";

    public override string? Description { get; } = $"QHY Camera driver using C# SDK wrapper v{GetSDKVersion()}";

    public override double ExposureResolution { get; } = 1E-06;

    protected override INativeDeviceIterator<QHYCCD_CAMERA_INFO> NewIterator() => new DeviceIterator<QHYCCD_CAMERA_INFO>();

    protected override Exception NotConnectedException() => new QHYDriverException("Camera is not connected");

    protected override Exception OperationalException(CMOSErrorCode errorCode, string message) => new QHYDriverException($"{errorCode}: {message}");

    protected override ValueTask<bool> InitSdkAsync(CancellationToken cancellationToken)
    {
        if (_deviceInfo.Init())
        {
            return ValueTask.FromResult(true);
        }
        else
        {
            // Not closed here: the connect that called this closes what it opened when an init fails
            // (DeviceDriverBase.ReleaseFailedConnectionAsync), through the SDK gate (#1758); closing here too closed it twice.
            return ValueTask.FromResult(false);
        }
    }
}
