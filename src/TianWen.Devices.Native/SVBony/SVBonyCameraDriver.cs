using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SVBony.SDK;
using TianWen.DAL;
using TianWen.Lib.Devices.DAL;

namespace TianWen.Lib.Devices.SVBony;

internal class SVBonyCameraDriver(SVBonyDevice device, IServiceProvider sp)
    : DALCameraDriver<SVBonyDevice, SVBCameraDevice>(device, sp)
{
    public override string? DriverInfo => $"SVBONY Camera Driver v{DriverVersion}";

    public override string? Description { get; } = $"SVBONY camera driver using C# SDK wrapper, SDK {SVBCamera.SVBGetSDKVersion()}";

    /// <summary>One microsecond: <c>SVB_EXPOSURE</c> is stated in microseconds, from 66.</summary>
    public override double ExposureResolution { get; } = 1E-06;

    protected override INativeDeviceIterator<SVBCameraDevice> NewIterator() => new DeviceIterator();

    protected override Exception NotConnectedException() => new SVBonyDriverException("Camera is not connected");

    /// <summary>The DAL code is what gets reported: the binding already mapped the SDK's error onto it.</summary>
    protected override Exception OperationalException(CMOSErrorCode errorCode, string message)
        => new SVBonyDriverException($"{message} ({errorCode})");

    /// <summary>
    /// Nothing to initialise, since the binding's open already set the stream up and every control that touches a
    /// RAW frame to where it touches nothing; this records the body and anything that open could not set.
    /// </summary>
    protected override ValueTask<bool> InitSdkAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation(
            "SVBONY camera {Name} (serial {Serial}): firmware {Firmware}, SDK {Sdk}, {Link}",
            _deviceInfo.Name, _deviceInfo.SerialNumber, _deviceInfo.FirmwareVersion, SVBCamera.SVBGetSDKVersion(), _deviceInfo.PortType);
        if (_deviceInfo.RawNeutralRefused is { Count: > 0 } refused)
        {
            Logger.LogWarning("SVBONY camera {Name} refused the raw-neutral value of {Controls}: its frames may carry a processing setting another program left", _deviceInfo.Name, string.Join(", ", refused));
        }

        return ValueTask.FromResult(true);
    }
}
