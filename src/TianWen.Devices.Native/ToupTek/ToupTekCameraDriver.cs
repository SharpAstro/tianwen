using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.DAL;
using TianWen.Lib.Devices.DAL;
using ToupTek.SDK;

namespace TianWen.Lib.Devices.ToupTek;

internal class ToupTekCameraDriver(ToupTekDevice device, IServiceProvider sp)
    : DALCameraDriver<ToupTekDevice, ToupcamCamera>(device, sp)
{
    public override string? DriverInfo => $"ToupTek Camera Driver v{DriverVersion}";

    public override string? Description { get; } = $"ToupTek camera driver using C# SDK wrapper v{ToupcamBrand.ToupTek.Api?.SdkVersion}";

    /// <summary>One microsecond: <c>Toupcam_put_ExpoTime</c> is stated in microseconds.</summary>
    public override double ExposureResolution { get; } = 1E-06;

    protected override INativeDeviceIterator<ToupcamCamera> NewIterator() => new DeviceIterator();

    protected override Exception NotConnectedException() => new ToupTekDriverException("Camera is not connected");

    /// <summary>The DAL code is what gets reported: the binding already mapped the SDK's HRESULT onto
    /// it, and the HRESULT itself is not carried this far.</summary>
    protected override Exception OperationalException(CMOSErrorCode errorCode, string message)
        => new ToupTekDriverException($"{message} ({errorCode})");

    /// <summary>
    /// Nothing to initialise, since the binding's open already configured the stream; this only
    /// records what the body is running.
    /// </summary>
    /// <remarks>
    /// Firmware, SDK and conversion gain mode are logged together because they are a PAIRING on this
    /// family: ToupTek IMX585 bodies garbled HDR frames until a firmware update, after which older SDKs
    /// misbehaved instead (SharpCap forum, t=8307). A frame problem reported against this log can be
    /// matched to the combination that produced it.
    /// </remarks>
    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation(
            "ToupTek camera {Name} (brand {Brand}, serial {Serial}): firmware {Firmware}, SDK {Sdk}, conversion gain {ConversionGain}",
            _deviceInfo.Name, _deviceInfo.Brand?.Name, _deviceInfo.SerialNumber, _deviceInfo.FirmwareVersion,
            _deviceInfo.Brand?.Api?.SdkVersion, _deviceInfo.ConversionGain);
        return ValueTask.FromResult(true);
    }
}
