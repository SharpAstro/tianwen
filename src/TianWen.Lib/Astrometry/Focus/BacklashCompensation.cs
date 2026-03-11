using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Astrometry.Focus;

/// <summary>
/// Provides backlash-compensated focuser movement. Always approaches the target
/// from the same direction (below/inward) by overshooting past the target and
/// returning, ensuring consistent mechanical engagement.
/// </summary>
public static class BacklashCompensation
{
    /// <summary>
    /// Moves the focuser to <paramref name="targetPosition"/> with direction-dependent backlash compensation.
    /// When moving inward, overshoots by <paramref name="backlashStepsIn"/> then returns.
    /// When moving outward, overshoots by <paramref name="backlashStepsOut"/> then returns.
    /// Convention: always approach from below (lower position numbers).
    /// </summary>
    public static async Task MoveWithCompensationAsync(
        IFocuserDriver focuser,
        int targetPosition,
        int currentPosition,
        int backlashStepsIn,
        int backlashStepsOut,
        IExternal external,
        CancellationToken cancellationToken)
    {
        if (targetPosition == currentPosition)
        {
            return;
        }

        if (targetPosition < currentPosition)
        {
            // Moving inward — overshoot past target, then approach from below
            if (backlashStepsIn > 0)
            {
                var overshootPos = Math.Max(0, targetPosition - backlashStepsIn);
                await MoveAndWaitAsync(focuser, overshootPos, external, cancellationToken);
            }
            await MoveAndWaitAsync(focuser, targetPosition, external, cancellationToken);
        }
        else
        {
            // Moving outward — overshoot past target, then approach from above
            if (backlashStepsOut > 0)
            {
                var overshootPos = targetPosition + backlashStepsOut;
                if (focuser.MaxStep > 0)
                {
                    overshootPos = Math.Min(focuser.MaxStep, overshootPos);
                }
                await MoveAndWaitAsync(focuser, overshootPos, external, cancellationToken);
            }
            await MoveAndWaitAsync(focuser, targetPosition, external, cancellationToken);
        }
    }

    private static async Task MoveAndWaitAsync(
        IFocuserDriver focuser,
        int position,
        IExternal external,
        CancellationToken cancellationToken)
    {
        await focuser.BeginMoveAsync(position, cancellationToken);

        while (await focuser.GetIsMovingAsync(cancellationToken) && !cancellationToken.IsCancellationRequested)
        {
            await external.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }
}
