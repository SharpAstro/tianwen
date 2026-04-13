using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    /// <summary>
    /// Closes or opens telescope covers (if any). Also turns of a present calibrator when opening cover.
    /// </summary>
    /// <param name="finalCoverState">One of <see cref="CoverStatus.Open"/> or <see cref="CoverStatus.Closed"/></param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    internal async ValueTask<bool> MoveTelescopeCoversToStateAsync(CoverStatus finalCoverState, CancellationToken cancellationToken)
    {
        var scopes = Setup.Telescopes.Length;

        var finalCoverStateReached = new bool[scopes];
        var coversToWait = new List<int>();
        var shouldOpen = finalCoverState is CoverStatus.Open;

        for (var i = 0; i < scopes; i++)
        {
            if (Setup.Telescopes[i].Cover is { } cover)
            {
                await cover.Driver.ConnectAsync(cancellationToken).ConfigureAwait(false);

                bool calibratorActionCompleted;
                if (await cover.Driver.GetCoverStateAsync(cancellationToken) is CoverStatus.NotPresent)
                {
                    calibratorActionCompleted = true;
                    finalCoverStateReached[i] = true;
                }
                else if (finalCoverState is CoverStatus.Open)
                {
                    calibratorActionCompleted = await cover.Driver.TurnOffCalibratorAndWaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (finalCoverState is CoverStatus.Closed)
                {
                    calibratorActionCompleted = true;
                }
                else
                {
                    throw new ArgumentException($"Invalid final cover state {finalCoverState}, can only be open or closed", nameof(finalCoverState));
                }

                if (calibratorActionCompleted && !finalCoverStateReached[i])
                {
                    if (shouldOpen)
                    {
                        await cover.Driver.BeginOpen(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await cover.Driver.BeginClose(cancellationToken).ConfigureAwait(false);
                    }

                    coversToWait.Add(i);
                }
                else if (!calibratorActionCompleted)
                {
                    _logger.LogError("Failed to turn off calibrator of telescope {TelescopeNumber}, current state {CalibratorState}", i+1, await cover.Driver.GetCalibratorStateAsync(cancellationToken));
                }
            }
            else
            {
                finalCoverStateReached[i] = true;
            }
        }

        foreach (var i in coversToWait)
        {
            if (Setup.Telescopes[i].Cover is { } cover)
            {
                int failSafe = 0;
                CoverStatus cs;
                while ((finalCoverStateReached[i] = (cs = await cover.Driver.GetCoverStateAsync(cancellationToken)) == finalCoverState) is false
                    && cs is CoverStatus.Moving or CoverStatus.Unknown
                    && !cancellationToken.IsCancellationRequested
                    && ++failSafe < IDeviceDriver.MAX_FAILSAFE
                )
                {
                    _logger.LogInformation("Cover {Cover} of telescope {TelescopeNumber} is still {CurrentState} while reaching {FinalCoverState}, waiting.",
                        cover, i + 1, cs, finalCoverState);
                    await _timeProvider.SleepAsync(TimeSpan.FromSeconds(3), cancellationToken);
                }

                var finalCoverStateAfterMoving = await cover.Driver.GetCoverStateAsync(cancellationToken);
                finalCoverStateReached[i] |= finalCoverStateAfterMoving == finalCoverState;

                if (!finalCoverStateReached[i])
                {
                    _logger.LogError("Failed to {CoverAction} cover of telescope {TelescopeNumber} after moving, current state {CurrentCoverState}",
                        shouldOpen ? "open" : "close",  i + 1, finalCoverStateAfterMoving);
                }
            }
        }

        return finalCoverStateReached.All(x => x);
    }



}
