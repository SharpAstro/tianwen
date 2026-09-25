using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Astrometry.Focus;

/// <summary>
/// How a compensated move commands the focuser and asks whether it has arrived. The session passes
/// its resilient versions (a transient fault retried and counted against reconnect escalation, like
/// every other hot-path driver call); a caller with no resilience layer takes <see cref="Direct"/>.
/// <para>A move-wait that polled the driver directly stopped at the first failed read: a driver that
/// reported the failure as "not moving" ended the wait while the focuser was still travelling, and one
/// that throws failed the whole autofocus run on a single glitch the resilience layer would have
/// retried (#781).</para>
/// </summary>
public readonly record struct FocuserMotion(
    Func<int, CancellationToken, ValueTask> BeginMoveAsync,
    Func<CancellationToken, ValueTask<bool>> IsMovingAsync)
{
    /// <summary>The driver's own calls, with no retry: for tools and tests.</summary>
    public static FocuserMotion Direct(IFocuserDriver focuser)
        => new(async (position, ct) => await focuser.BeginMoveAsync(position, ct).ConfigureAwait(false), focuser.GetIsMovingAsync);
}

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
    /// Every move and every is-moving poll goes through <paramref name="motion"/>.
    /// </summary>
    public static async Task MoveWithCompensationAsync(
        IFocuserDriver focuser,
        FocuserMotion motion,
        int targetPosition,
        int currentPosition,
        int backlashStepsIn,
        int backlashStepsOut,
        FocusDirection focusDirection,
        ITimeProvider timeProvider,
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
            // Moving in the preferred direction: no compensation needed, just move directly
            await MoveAndWaitAsync(motion, targetPosition, timeProvider, cancellationToken);
        }
        else
        {
            // Moving against the preferred direction: overshoot past target, then approach from preferred side
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

                await MoveAndWaitAsync(motion, overshootPos, timeProvider, cancellationToken);
            }
            // Now approach target from the preferred direction
            await MoveAndWaitAsync(motion, targetPosition, timeProvider, cancellationToken);
        }
    }

    private static async Task MoveAndWaitAsync(
        FocuserMotion motion,
        int position,
        ITimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        await motion.BeginMoveAsync(position, cancellationToken);

        while (await motion.IsMovingAsync(cancellationToken) && !cancellationToken.IsCancellationRequested)
        {
            await timeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }
}
