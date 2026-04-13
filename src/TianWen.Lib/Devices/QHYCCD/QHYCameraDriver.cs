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

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        if (_deviceInfo.Init())
        {
            return ValueTask.FromResult(true);
        }
        else
        {
            // close this device again as we failed to initialise it
            _deviceInfo.Close();

            return ValueTask.FromResult(false);
        }
    }
}
