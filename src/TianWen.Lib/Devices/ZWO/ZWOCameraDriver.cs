using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices.DAL;
using ZWOptical.SDK;
using static ZWOptical.SDK.ASICamera2;
using static ZWOptical.SDK.ASICamera2.ASI_ERROR_CODE;

namespace TianWen.Lib.Devices.ZWO;

internal class ZWOCameraDriver(ZWODevice device, IExternal external) : DALCameraDriver<ZWODevice, ASI_CAMERA_INFO>(device, external)
{
    public override string? DriverInfo => $"ZWO Camera Driver v{DriverVersion}";

    public override string? Description { get; } = $"ZWO Camera driver using C# SDK wrapper v{ASIGetSDKVersion}";

    public override double ExposureResolution { get; } = 1E-06;

    protected override INativeDeviceIterator<ASI_CAMERA_INFO> NewIterator() => new DeviceIterator<ASI_CAMERA_INFO>();

    protected override Exception NotConnectedException() => new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");

    protected override Exception OperationalException(CMOSErrorCode errorCode, string message) => new ZWODriverException((ASI_ERROR_CODE)errorCode, message);

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        if (ASIInitCamera(_deviceInfo.ID) is ASI_SUCCESS)
        {
            return ValueTask.FromResult(true);
        }
        else
        {
            // close this device again as we failed to initalize it
            _deviceInfo.Close();

            return ValueTask.FromResult(false);
        }
    }
}
