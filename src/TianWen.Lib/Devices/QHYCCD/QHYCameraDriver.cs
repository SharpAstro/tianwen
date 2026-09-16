using Microsoft.Extensions.Logging;
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
            EnableDdrBuffer();

            return ValueTask.FromResult(true);
        }
        else
        {
            // close this device again as we failed to initialise it
            _deviceInfo.Close();

            return ValueTask.FromResult(false);
        }
    }

    /// <summary>
    /// Turns the camera's DDR frame buffer ON, every connect, wherever the body has one.
    /// </summary>
    /// <remarks>
    /// <para>A QHY body with DDR memory can hold a frame on the camera while the host is busy. With
    /// it off, a readout streams straight down USB with nothing to absorb a stall, and a stall then
    /// costs the frame: dropped, torn, or delivered late. That is the shape of a complaint this
    /// camera's owner has carried for years against other capture software.</para>
    /// <para><b>It is not sticky and has to be set on every connect.</b> Measured on a QHY178M
    /// 2026-09-16: the control reads 0 on a freshly opened handle every time, so a camera that was
    /// left with DDR enabled by another application comes back to us with it off.</para>
    /// <para>Skipped silently where the body does not offer the control, since plenty do not have
    /// the memory; a refusal is logged rather than thrown, because a camera that will not buffer is
    /// still a camera that will image.</para>
    /// </remarks>
    private void EnableDdrBuffer()
    {
        if (!_deviceInfo.TryGetControlRange(CMOSControlType.EnableDDR, out _, out var max) || max < 1)
        {
            Logger.LogDebug("{DeviceId} has no DDR frame buffer to enable", _device.DeviceId);
            return;
        }

        if (_deviceInfo.SetControlValue(CMOSControlType.EnableDDR, 1) is var code and not CMOSErrorCode.Success)
        {
            Logger.LogWarning("{DeviceId} refused to enable its DDR frame buffer ({ErrorCode}); a USB stall can now "
                + "cost a frame rather than delay one", _device.DeviceId, code);
            return;
        }

        Logger.LogInformation("{DeviceId} DDR frame buffer enabled", _device.DeviceId);
    }
}
