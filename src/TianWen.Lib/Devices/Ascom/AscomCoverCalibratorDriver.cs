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

    internal AscomCoverCalibratorDriver(AscomDevice device, IServiceProvider sp) : base(device, sp)
    {
        _coverCalibrator = new AscomDispatchCoverCalibrator(_dispatchDevice.Dispatch);
    }

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        MaxBrightness = SafeGet(() => _coverCalibrator.MaxBrightness, 0);
        return ValueTask.FromResult(true);
    }

    public int MaxBrightness { get; private set; }

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
            SafeDo(() => _coverCalibrator.CalibratorOn(brightness));
        }
    }

    public Task BeginCalibratorOff(CancellationToken cancellationToken = default)
        => SafeTask(() => _coverCalibrator.CalibratorOff());

    public ValueTask<int> GetBrightnessAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SafeGet(() => _coverCalibrator.Brightness, 0));

    public ValueTask<CoverStatus> GetCoverStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? SafeGet(() => (CoverStatus)_coverCalibrator.CoverState, CoverStatus.Unknown) : CoverStatus.Unknown);

    public ValueTask<CalibratorStatus> GetCalibratorStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? SafeGet(() => (CalibratorStatus)_coverCalibrator.CalibratorState, CalibratorStatus.Unknown) : CalibratorStatus.Unknown);

    public Task BeginClose(CancellationToken cancellationToken = default)
        => SafeTask(() => _coverCalibrator.CloseCover());

    public Task BeginOpen(CancellationToken cancellationToken = default)
        => SafeTask(() => _coverCalibrator.OpenCover());
}
