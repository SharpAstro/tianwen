using System.Threading;
using System;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

public interface ICoverDriver : IDeviceDriver
{
    bool IsCalibrationReady
        => CoverState is not CoverStatus.Error and not CoverStatus.Moving
        && CalibratorState is not CalibratorStatus.NotReady and not CalibratorStatus.NotPresent and not CalibratorStatus.Error;

    /// <summary>
    /// Asyncronously opens the cover.
    /// </summary>
    Task BeginOpen(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asyncronously closes the cover.
    /// </summary>
    Task BeginClose(CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns on calibrator (if present) and sets <see cref="Brightness"/> to t<paramref name="brightness"/>.
    /// </summary>
    /// <param name="brightness"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task BeginCalibratorOn(int brightness, CancellationToken cancellationToken = default);

    Task BeginCalibratorOff(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current calibrator brightness in the range 0 (completely off) to <see cref="MaxBrightness"/> (fully on).
    /// </summary>
    int Brightness { get; }

    /// <summary>
    /// Maximum brightness value for <see cref="CalibratorOn(int)"/>, will be -1 if unknown.
    /// </summary>
    int MaxBrightness { get; }

    CoverStatus CoverState { get; }

    /// <summary>
    /// Returns the state of the calibration device, if present, otherwise returns <see cref="CalibratorStatus.NotPresent"/>.
    /// </summary>
    CalibratorStatus CalibratorState { get; }

    /// <summary>
    /// Higher-level function to turn of the calibrator (if present)
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    async ValueTask<bool> TurnOffCalibratorAndWaitAsync(CancellationToken cancellationToken = default)
    {
        var calState = CalibratorState;

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
        while ((calState = CalibratorState) == CalibratorStatus.NotReady
            && !cancellationToken.IsCancellationRequested
            && ++tries < MAX_FAILSAFE)
        {
            await External.SleepAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return calState is CalibratorStatus.Off;
    }
}
