using System.Threading;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Devices;

public interface ICoverDriver : IDeviceDriver
{
    ValueTask<CoverStatus> GetCoverStateAsync(CancellationToken cancellationToken = default);

    ValueTask<CalibratorStatus> GetCalibratorStateAsync(CancellationToken cancellationToken = default);

    ValueTask<int> GetBrightnessAsync(CancellationToken cancellationToken = default);

    async ValueTask<bool> IsCalibrationReadyAsync(CancellationToken cancellationToken = default)
    {
        var coverState = await GetCoverStateAsync(cancellationToken);
        var calState = await GetCalibratorStateAsync(cancellationToken);
        return coverState is not CoverStatus.Error and not CoverStatus.Moving
            && calState is not CalibratorStatus.NotReady and not CalibratorStatus.NotPresent and not CalibratorStatus.Error;
    }

    /// <summary>
    /// Asyncronously opens the cover.
    /// </summary>
    Task BeginOpen(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asyncronously closes the cover.
    /// </summary>
    Task BeginClose(CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns on calibrator (if present) and sets brightness.
    /// </summary>
    Task BeginCalibratorOn(int brightness, CancellationToken cancellationToken = default);

    Task BeginCalibratorOff(CancellationToken cancellationToken = default);

    /// <summary>
    /// Maximum brightness value, will be -1 if unknown.
    /// </summary>
    int MaxBrightness { get; }

    /// <summary>
    /// Higher-level function to turn off the calibrator (if present)
    /// </summary>
    async ValueTask<bool> TurnOffCalibratorAndWaitAsync(CancellationToken cancellationToken = default)
    {
        var calState = await GetCalibratorStateAsync(cancellationToken);

        if (calState is CalibratorStatus.NotPresent or CalibratorStatus.Off)
        {
            return true;
        }
        else if (calState is CalibratorStatus.Unknown or CalibratorStatus.Error)
        {
            return false;
        }

        await BeginCalibratorOff(cancellationToken);

        var tries = 0;
        while ((calState = await GetCalibratorStateAsync(cancellationToken)) == CalibratorStatus.NotReady
            && !cancellationToken.IsCancellationRequested
            && ++tries < MAX_FAILSAFE)
        {
            await External.SleepAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return calState is CalibratorStatus.Off;
    }
}
