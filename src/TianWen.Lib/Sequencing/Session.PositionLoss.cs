using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// A run whose mount lost its position (its power, #1954): the run asks, never guesses. The prompt offers the sync from the
/// last known position; with nobody to answer, the session's unattended policy decides, and declining ends the run with the
/// loss as its reason. A restore points the current target again, since the power loss stopped tracking.
/// </summary>
internal partial record Session
{
    // 1 while a prompt about the lost position is open: a slew wait and the imaging loop both poll, and must not ask twice.
    private int _positionLossAsking;

    // Set when the operator (or the unattended policy) declined the restore: the run ends where the loop next looks.
    private bool _positionLostEndsRun;

    // Set by a restore: the loop points the current target again, from the position the mount now knows.
    private int _repointAfterPositionRestore;

    /// <summary>
    /// True when the mount's position is known: never lost, or restored now through the prompt. False while it is lost, a
    /// prompt about it is already open elsewhere, or the restore was declined (<see cref="_positionLostEndsRun"/>).
    /// </summary>
    private async ValueTask<bool> ResolveMountPositionLossAsync(IMountDriver mount, CancellationToken cancellationToken)
    {
        if (mount.PositionLoss is not { } loss)
        {
            return true;
        }
        if (Volatile.Read(ref _positionLostEndsRun) || Interlocked.CompareExchange(ref _positionLossAsking, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            _logger.LogError("{Loss}", loss.Describe(mount.Name));
            var restore = await RequestUserConfirmationAsync(
                "Mount lost power",
                $"{loss.Describe(mount.Name)} {loss.LastKnownCaveat} Sync it from its last known position and go on with the run, or end the run here.",
                cancellationToken,
                continueLabel: "Sync from last known position",
                cancelLabel: "End the run",
                // Restoring asserts a physical fact: the mount did not move while it was off.
                requiresPhysicalPresence: true);
            cancellationToken.ThrowIfCancellationRequested();

            if (restore)
            {
                try
                {
                    await mount.RestorePositionAsync(MountPositionRestore.LastKnown, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Restoring the position of {Mount} failed", mount.Name);
                    return EndRunOnLostPosition($"{loss.Describe(mount.Name)} Restoring it failed: {ex.Message}");
                }

                _logger.LogWarning("{Mount}: position restored from its last known position; pointing the current target again.", mount.Name);
                // The power loss stopped tracking, and nothing here asked it to: the driver-stop detector must not read that
                // as a limit before the repoint's goto starts tracking again.
                _mountStopCommanded = true;
                Interlocked.Exchange(ref _repointAfterPositionRestore, 1);
                return true;
            }

            return EndRunOnLostPosition($"{loss.Describe(mount.Name)} The run ended there: restore its position from the mount's panel, then start again.");
        }
        finally
        {
            Interlocked.Exchange(ref _positionLossAsking, 0);
        }
    }

    /// <summary>For tests: the restore was declined, and the run ends where the loop next looks.</summary>
    internal bool PositionLostEndsRun => Volatile.Read(ref _positionLostEndsRun);

    /// <summary>For tests: a restore is waiting for the loop to point the current target again.</summary>
    internal bool RepointAfterPositionRestorePending => Volatile.Read(ref _repointAfterPositionRestore) == 1;

    private bool EndRunOnLostPosition(string reason)
    {
        _failureReason = reason;
        Volatile.Write(ref _positionLostEndsRun, true);
        return false;
    }
}
