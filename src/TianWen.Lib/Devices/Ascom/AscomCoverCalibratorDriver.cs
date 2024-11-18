namespace TianWen.Lib.Devices.Ascom;

using System;
using System.Threading;
using System.Threading.Tasks;
using AscomCoverCalibrator = ASCOM.Com.DriverAccess.CoverCalibrator;

internal class AscomCoverCalibratorDriver(AscomDevice device, IExternal external)
    : AscomDeviceDriverBase<AscomCoverCalibrator>(device, external, (progId, logger) => new AscomCoverCalibrator(progId, new AscomLoggerWrapper(logger))), ICoverDriver
{
    public int MaxBrightness => _comObject?.MaxBrightness is int maxBrightness ? maxBrightness : -1;

    public Task BeginCalibratorOn(int brightness, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Cover is not connected");
        }
        else if (brightness < 0 || brightness > MaxBrightness)
        {
            throw new ArgumentOutOfRangeException(nameof(brightness), brightness, $"Brightness out of range (0..{MaxBrightness})");
        }
        else if (!((ICoverDriver)this).IsCalibrationReady)
        {
            throw new InvalidOperationException("Cover is not ready");
        }
        else
        {
            _comObject.CalibratorOn(brightness);
        }

        return Task.CompletedTask;
    }

    public Task BeginCalibratorOff(CancellationToken cancellationToken = default)
    {
        _comObject.CalibratorOff();

        return Task.CompletedTask;
    }

    public int Brightness => _comObject?.Brightness is int brightness ? brightness : -1;

    public CoverStatus CoverState => Connected ? (CoverStatus)(int)_comObject.CoverState : CoverStatus.Unknown;

    public CalibratorStatus CalibratorState => Connected ? (CalibratorStatus)(int)_comObject.CalibratorState : CalibratorStatus.Unknown;

    public Task BeginClose(CancellationToken cancellationToken = default)
    {
        _comObject.CloseCover();

        return Task.CompletedTask;
    }

    public Task BeginOpen(CancellationToken cancellationToken = default)
    {
        _comObject.OpenCover();

        return Task.CompletedTask;
    }
}