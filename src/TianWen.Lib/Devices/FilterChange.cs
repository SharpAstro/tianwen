using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.Focus;

namespace TianWen.Lib.Devices;

/// <summary>
/// How a filter change reads the wheel's slot and commands it. The session passes its resilient calls (a transient fault
/// retried and counted against reconnect escalation, as every hot-path driver call is); a caller with no resilience layer,
/// the node's device plane, takes <see cref="Direct"/>.
/// </summary>
public readonly record struct FilterWheelMotion(
    Func<CancellationToken, ValueTask<int>> GetPositionAsync,
    Func<int, CancellationToken, ValueTask> BeginMoveAsync)
{
    /// <summary>The driver's own calls, with no retry.</summary>
    public static FilterWheelMotion Direct(IFilterWheelDriver wheel)
        => new(wheel.GetPositionAsync, async (position, ct) => await wheel.BeginMoveAsync(position, ct));
}

/// <summary>
/// The focuser a filter change moves by the focus offset, and how: its position read and its moves (the session's resilient
/// ones, or <see cref="Direct"/>), the backlash its compensated move overshoots by, and the direction it approaches focus from
/// (<see cref="BacklashCompensation"/>).
/// </summary>
public readonly record struct FilterFocuser(
    IFocuserDriver Focuser,
    Func<CancellationToken, ValueTask<int>> GetPositionAsync,
    FocuserMotion Motion,
    int BacklashStepsIn,
    int BacklashStepsOut,
    FocusDirection Direction)
{
    /// <summary>
    /// The driver's own calls and the backlash it reports (a negative one, unknown, overshoots by nothing), for a caller with
    /// no resilience layer and no backlash history. <paramref name="direction"/> is the OTA's, from its profile as
    /// <c>SessionFactory</c> builds it.
    /// </summary>
    public static FilterFocuser Direct(IFocuserDriver focuser, FocusDirection direction)
        => new(focuser, focuser.GetPositionAsync, FocuserMotion.Direct(focuser), focuser.BacklashStepsIn, focuser.BacklashStepsOut, direction);
}

/// <summary>What a filter change did: the slot the wheel left and the one it reached, and the focuser's move by the offset.</summary>
/// <param name="FocusOffsetSteps">How far the focuser was moved (<see cref="FilterChange.FocusOffsetSteps"/>); zero when it was not.</param>
/// <param name="FocuserPosition">Where the focuser was sent, or null when no focuser moved.</param>
public readonly record struct FilterChangeOutcome(int FromPosition, int ToPosition, int FocusOffsetSteps, int? FocuserPosition)
{
    /// <summary>Whether the wheel turned: false when it was in the slot asked for already.</summary>
    public bool Turned => FromPosition != ToPosition;
}

/// <summary>
/// The ONE filter change (Phase G of docs/plans/live-planetary-capture.md, #1618, #1340): the wheel turned to a slot, its
/// arrival awaited within a bound, and the focuser moved by the difference between the focus offsets of the filter arrived at
/// and the filter left. The session's imaging loop, its focus and flat routines, and the node's device plane all call it, so
/// the rule is never copied.
/// </summary>
/// <remarks>
/// <para><b>The offset moved by is the filter arrived at less the filter LEFT, never less the luminance filter.</b>
/// <see cref="InstalledFilter.Position"/> is each filter's focus position against one reference, so a change from R (+20) to
/// B (-15) moves the focuser by -35, and one to a filter whose offset is 0 moves it by minus the offset it left. The session's
/// rule before this one added the target's offset against luminance to wherever the focuser stood, and only when that offset
/// was not zero: on the fake wheel (L 0, R +20, G 0, B -15) R to G left the focuser at +20 and R to B landed it at +5.</para>
/// <para>A slot the wheel names no filter for has no known offset, so a change from or to one moves no focuser; and a wheel
/// still moving when asked is waited for first, since the filter being left is the one it settles on.</para>
/// </remarks>
public static class FilterChange
{
    /// <summary>The longest a wheel may take to report the slot it was sent to (and, asked while moving, the one it settles on).</summary>
    public static readonly TimeSpan ArrivalTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How often a turning wheel is asked where it is.</summary>
    private static readonly TimeSpan ArrivalPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How far a change from slot <paramref name="fromPosition"/> to slot <paramref name="toPosition"/> moves the focuser: the
    /// offset of the filter arrived at less the offset of the one left, and zero when either slot names no filter.
    /// </summary>
    public static int FocusOffsetSteps(IReadOnlyList<InstalledFilter> filters, int fromPosition, int toPosition)
        => IsSlot(filters, fromPosition) && IsSlot(filters, toPosition) ? filters[toPosition].Position - filters[fromPosition].Position : 0;

    /// <summary>
    /// Turns <paramref name="wheel"/> to <paramref name="targetPosition"/> unless it is there already, waits for it to report
    /// that slot, and then moves <paramref name="focuser"/>, when there is one, by <see cref="FocusOffsetSteps"/> through its
    /// backlash rule (<see cref="BacklashCompensation.MoveWithCompensationAsync"/>).
    /// </summary>
    /// <param name="motion">How the wheel is read and commanded: the session's resilient calls, or <see cref="FilterWheelMotion.Direct"/>.</param>
    /// <param name="focuser">The OTA's connected focuser, or null when it has none or none is connected: then only the wheel moves.</param>
    /// <param name="label">Who changes the filter, for the log: "Telescope #1", or the wheel's name.</param>
    /// <exception cref="TimeoutException">The wheel did not report the slot within <see cref="ArrivalTimeout"/>.</exception>
    /// <exception cref="InvalidOperationException">The focus offset would take the focuser past its travel; nothing was moved.</exception>
    public static async ValueTask<FilterChangeOutcome> ChangeAsync(
        IFilterWheelDriver wheel,
        FilterWheelMotion motion,
        int targetPosition,
        FilterFocuser? focuser,
        ITimeProvider timeProvider,
        ILogger logger,
        string label,
        CancellationToken cancellationToken)
    {
        var filters = wheel.Filters;
        var from = await WaitForSlotAsync(motion, wanted: null, timeProvider,
            () => $"{wheel.Name} did not say which filter it holds within {ArrivalTimeout.TotalMinutes:F0} minutes", cancellationToken);
        if (from == targetPosition)
        {
            return new FilterChangeOutcome(from, targetPosition, 0, null);
        }

        // A focuser the offset would take past its travel is refused before the wheel turns, in the words the focuser's own move
        // route uses: turned first, the wheel would leave the new filter out of focus behind a failed move.
        var offset = FocusOffsetSteps(filters, from, targetPosition);
        if (focuser is { } checking && offset != 0)
        {
            var reach = await checking.GetPositionAsync(cancellationToken) + offset;
            if (!checking.Focuser.IsWithinTravel(reach))
            {
                throw new InvalidOperationException(
                    $"{checking.Focuser.Name} moves {checking.Focuser.TravelInWords}: the focus offset from {NameOf(filters, from)} to "
                    + $"{NameOf(filters, targetPosition)} ({offset:+0;-0} steps) would take it to {reach}, so the wheel was not turned");
            }
        }

        logger.LogInformation("{Label}: switching filter from {From} to {To} (position {FromPosition} to {ToPosition}).",
            label, NameOf(filters, from), NameOf(filters, targetPosition), from, targetPosition);
        await motion.BeginMoveAsync(targetPosition, cancellationToken);
        await WaitForSlotAsync(motion, targetPosition, timeProvider,
            () => $"{wheel.Name} did not reach {NameOf(filters, targetPosition)} within {ArrivalTimeout.TotalMinutes:F0} minutes", cancellationToken);

        if (focuser is not { } focusing || offset == 0)
        {
            return new FilterChangeOutcome(from, targetPosition, 0, null);
        }

        var current = await focusing.GetPositionAsync(cancellationToken);
        var target = current + offset;
        logger.LogInformation("{Label}: applying focus offset {Delta} steps from {From} to {To} (focuser {Current} to {Target}).",
            label, offset, NameOf(filters, from), NameOf(filters, targetPosition), current, target);
        await BacklashCompensation.MoveWithCompensationAsync(
            focusing.Focuser, focusing.Motion, target, current, focusing.BacklashStepsIn, focusing.BacklashStepsOut,
            focusing.Direction, timeProvider, cancellationToken);
        return new FilterChangeOutcome(from, targetPosition, offset, target);
    }

    /// <summary>What a slot is called in words: its filter, or "position N" for one the wheel names none for.</summary>
    public static string NameOf(IReadOnlyList<InstalledFilter> filters, int position)
        => IsSlot(filters, position) ? filters[position].DisplayName : $"position {position}";

    private static bool IsSlot(IReadOnlyList<InstalledFilter> filters, int position) => position >= 0 && position < filters.Count;

    // Asks the wheel where it is until it reports `wanted`, or any slot when that is null (a wheel reports -1 while it turns),
    // within ArrivalTimeout; the slot it reported.
    private static async ValueTask<int> WaitForSlotAsync(FilterWheelMotion motion, int? wanted, ITimeProvider timeProvider, Func<string> late,
        CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + ArrivalTimeout;
        while (true)
        {
            var position = await motion.GetPositionAsync(cancellationToken);
            if (wanted is { } slot ? position == slot : position >= 0)
            {
                return position;
            }
            if (timeProvider.GetUtcNow() > deadline)
            {
                throw new TimeoutException(late());
            }
            await timeProvider.SleepAsync(ArrivalPoll, cancellationToken);
        }
    }
}
