using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Astrometry.Focus;

/// <summary>
/// Provides backlash-compensated focuser movement. Always approaches the target
/// from the preferred direction (determined by <see cref="FocusDirection"/>) by
/// overshooting past the target and returning, ensuring consistent mechanical engagement.
/// </summary>
public static class BacklashCompensation
{
    /// <summary>
    /// Moves the focuser to <paramref name="targetPosition"/> with backlash compensation,
    /// always approaching from the preferred direction as defined by <paramref name="focusDirection"/>.
    /// When a direction reversal occurs, overshoots past target and returns from the preferred side.
    /// </summary>
    public static async Task MoveWithCompensationAsync(
        IFocuserDriver focuser,
        int targetPosition,
        int currentPosition,
        int backlashStepsIn,
        int backlashStepsOut,
        FocusDirection focusDirection,
        IExternal external,
        CancellationToken cancellationToken)
    {
        if (targetPosition == currentPosition)
        {
            return;
        }

        var movingPositive = targetPosition > currentPosition;
        var approachingFromPreferred = movingPositive == focusDirection.PreferredDirectionIsPositive;

        if (approachingFromPreferred)
        {
            // Moving in the preferred direction — no compensation needed, just move directly
            await MoveAndWaitAsync(focuser, targetPosition, external, cancellationToken);
        }
        else
        {
            // Moving against the preferred direction — overshoot past target, then approach from preferred side
            var backlashSteps = movingPositive ? backlashStepsOut : backlashStepsIn;
            if (backlashSteps > 0)
            {
                // Overshoot past target in the movement direction
                var overshootPos = movingPositive
                    ? targetPosition + backlashSteps
                    : targetPosition - backlashSteps;

                // Clamp to valid range
                overshootPos = Math.Max(0, overshootPos);
                if (focuser.MaxStep > 0)
                {
                    overshootPos = Math.Min(focuser.MaxStep, overshootPos);
                }

                await MoveAndWaitAsync(focuser, overshootPos, external, cancellationToken);
            }
            // Now approach target from the preferred direction
            await MoveAndWaitAsync(focuser, targetPosition, external, cancellationToken);
        }
    }

    /// <summary>
    /// Overload without FocusDirection for backward compatibility.
    /// Defaults to preferring positive direction (approach from below).
    /// </summary>
    public static Task MoveWithCompensationAsync(
        IFocuserDriver focuser,
        int targetPosition,
        int currentPosition,
        int backlashStepsIn,
        int backlashStepsOut,
        IExternal external,
        CancellationToken cancellationToken)
    {
        return MoveWithCompensationAsync(
            focuser, targetPosition, currentPosition,
            backlashStepsIn, backlashStepsOut,
            new FocusDirection(PreferOutward: true, OutwardIsPositive: true),
            external, cancellationToken);
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
