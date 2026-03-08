namespace TianWen.Lib.Devices.Ascom;

using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Ascom.ComInterop;

[SupportedOSPlatform("windows")]
internal class AscomCoverCalibratorDriver : AscomDeviceDriverBase, ICoverDriver
{
    private readonly AscomDispatchCoverCalibrator _coverCalibrator;

    internal AscomCoverCalibratorDriver(AscomDevice device, IExternal external) : base(device, external)
    {
        _coverCalibrator = new AscomDispatchCoverCalibrator(_dispatchDevice.Dispatch);
    }

    public int MaxBrightness => _coverCalibrator.MaxBrightness;

    public async Task BeginCalibratorOn(int brightness, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Cover is not connected");
        }
        else if (brightness < 0 || brightness > MaxBrightness)
        {
            throw new ArgumentOutOfRangeException(nameof(brightness), brightness, $"Brightness out of range (0..{MaxBrightness})");
        }
        else if (!await ((ICoverDriver)this).IsCalibrationReadyAsync(cancellationToken))
        {
            throw new InvalidOperationException("Cover is not ready");
        }
        else
        {
            _coverCalibrator.CalibratorOn(brightness);
        }
    }

    public Task BeginCalibratorOff(CancellationToken cancellationToken = default)
    {
        _coverCalibrator.CalibratorOff();
        return Task.CompletedTask;
    }

    public ValueTask<int> GetBrightnessAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_coverCalibrator.Brightness);

    public ValueTask<CoverStatus> GetCoverStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? (CoverStatus)_coverCalibrator.CoverState : CoverStatus.Unknown);

    public ValueTask<CalibratorStatus> GetCalibratorStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? (CalibratorStatus)_coverCalibrator.CalibratorState : CalibratorStatus.Unknown);

    public Task BeginClose(CancellationToken cancellationToken = default)
    {
        _coverCalibrator.CloseCover();
        return Task.CompletedTask;
    }

    public Task BeginOpen(CancellationToken cancellationToken = default)
    {
        _coverCalibrator.OpenCover();
        return Task.CompletedTask;
    }
}
