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
    /// Whether the driver can actually set the calibrator brightness electronically. <c>true</c> for a
    /// real panel (flip-flat, Gemini FlatPanel Lite, an ASCOM/Alpaca cover-calibrator) whose
    /// <see cref="BeginCalibratorOn"/> physically drives the light; <c>false</c> for a hand-switched
    /// <see cref="ManualCoverDevice"/>, where <see cref="BeginCalibratorOn"/> only records a value and a
    /// human sets the actual level. The flat routine uses this to decide whether it must pause for a user
    /// prompt ("switch the panel on") before capturing -- it does <b>not</b> gate the motorised-cover
    /// (dark-frame) axis, which is queried separately via <see cref="GetCoverStateAsync"/>
    /// (<see cref="CoverStatus.NotPresent"/> = no flap to close). Default <c>true</c>.
    /// </summary>
    bool CanControlBrightness => true;

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
            await TimeProvider.SleepAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return calState is CalibratorStatus.Off;
    }
}
