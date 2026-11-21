using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// Can be used as a base class for focusers, filter wheels
/// </summary>
/// <param name="fakeDevice"></param>
/// <param name="external"></param>
internal abstract class FakePositionBasedDriver(FakeDevice fakeDevice, IServiceProvider serviceProvider) : FakeDeviceDriverBase(fakeDevice, serviceProvider)
{
    /// <summary>
    /// How fast the fake turns: 200 steps a second. The previous 100 ms a step (10 steps a second) made the GUI's focuser
    /// jog feel laggy: a <c>&gt;</c> click fires a +10 jog that took a full second to walk to its target one step at a
    /// time, so the user saw +1 and took the button to increment by 1. Real focusers run on the order of 100 to 500
    /// steps a second.
    /// </summary>
    internal const int StepsPerSecond = 200;

    /// <summary>
    /// How often the position is refreshed, one step at the nominal rate. It is a poll and not a step: a callback moves the
    /// position by the time that elapsed since the move began, since a timer on the real clock does not fire on time (Windows
    /// ticks every 15.6 ms, so a step per 5 ms callback ran at about 64 steps a second, #2022). The tests' fake clock fires
    /// the timer exactly on its period, so for them one callback is still one step.
    /// </summary>
    private static readonly TimeSpan TickPeriod = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / StepsPerSecond);

    protected volatile int _position;

    /// <summary>
    /// The move under way, or null. ONE field holds everything about it (where it ends, its clock, its timer), so a
    /// move begins, is replaced, is halted and ends in one atomic step each. There used to be three (a flag, the
    /// timer and the move), set one after the other, and a callback of a replaced move that was already past its
    /// identity check could clear the flag and dispose the timer of the move that replaced it (#2094).
    /// </summary>
    private MovingState? _currentMove;

    /// <summary>Whether a move is under way.</summary>
    protected bool IsMoving => Volatile.Read(ref _currentMove) is not null;

    /// <summary>
    /// Sets position (absolute)
    /// </summary>
    /// <param name="position"></param>
    /// <returns>true iff position is accepted and moving started</returns>
    /// <remarks>Does not validate inputs (subclass responsibility)</remarks>
    protected virtual Task BeginSetPositionAsync(int position, CancellationToken cancellationToken = default)
    {
        var currentPosition = _position;

        if (currentPosition != position)
        {
            var state = new MovingState(position, TimeProvider.GetTimestamp());
            // Made dormant and started only once the move is the current one, so no callback runs for a move that is
            // not yet (or no longer) it.
            var timer = TimeProvider.CreateTimer(MovingTimerCallback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            state.Timer = timer;

            // The move this one replaces loses its timer here; its callbacks already running return at the identity check.
            Interlocked.Exchange(ref _currentMove, state)?.Timer?.Dispose();

            timer.Change(TickPeriod, TickPeriod);
        }

        return Task.CompletedTask;
    }

    protected virtual Task BeginStopMovingAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _currentMove, null)?.Timer?.Dispose();
        return Task.CompletedTask;
    }

    private void MovingTimerCallback(object? state)
    {
        // A Halt clears the move and disposes its timer, but a callback already on its way still gets here. So does one
        // of a move a newer move replaced: it must not step or end the newer move, which has a timer and clock of its own.
        if (state is not MovingState move || !ReferenceEquals(Volatile.Read(ref _currentMove), move))
        {
            return;
        }

        var currentPosition = _position;
        var remaining = (long)move.End - currentPosition;

        // Never past the target: a late callback owing more steps than are left takes only what is left.
        var steps = Math.Min(move.TakeEarnedSteps(TimeProvider), Math.Abs(remaining));
        var nextPosition = currentPosition + (int)(Math.Sign(remaining) * steps);

        if (steps > 0 && Interlocked.CompareExchange(ref _position, nextPosition, currentPosition) != currentPosition)
        {
            // Not reachable from a move's own callbacks (one timer per move, and a callback of a replaced move returns above);
            // only a subclass writing _position directly gets here. The next callback takes the move up from where it now is.
            return;
        }

        if (nextPosition == move.End)
        {
            // Ends THIS move and nothing else: if a newer move replaced it since the check above, the exchange fails
            // and that move stays current, with its own timer untouched. This move's timer goes either way.
            Interlocked.CompareExchange(ref _currentMove, null, move);
            move.Timer?.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            Interlocked.Exchange(ref _currentMove, null)?.Timer?.Dispose();
        }
    }

    /// <summary>One move: where it ends, when it began on the driver's clock, and how many of the steps it has earned are taken.</summary>
    private sealed class MovingState(int end, long startTimestamp)
    {
        private long _stepsTaken;

        public int End { get; } = end;

        /// <summary>The timer that steps this move, set once before the move becomes the current one.</summary>
        public ITimer? Timer { get; set; }

        /// <summary>
        /// The steps this move has earned by now and not yet taken, by the time that elapsed since it began at
        /// <see cref="StepsPerSecond"/>, rounded to the nearest step. The count is cumulative from the start and never per
        /// callback, so the fraction of a step one callback arrived short of (or past) is carried into the next, and the
        /// rate over the whole move is exactly the nominal one however late the ticks are. Lock-free: a callback that
        /// loses the exchange to another reads again, and the steps are shared out between them, never taken twice.
        /// </summary>
        public long TakeEarnedSteps(ITimeProvider clock)
        {
            var elapsedTicks = Math.Max(0L, clock.GetTimestamp() - startTimestamp);
            var frequency = clock.TimestampFrequency;
            var earned = (elapsedTicks * StepsPerSecond + frequency / 2) / frequency;

            var taken = Volatile.Read(ref _stepsTaken);
            while (earned > taken)
            {
                var seen = Interlocked.CompareExchange(ref _stepsTaken, earned, taken);
                if (seen == taken)
                {
                    return earned - taken;
                }

                taken = seen;
            }

            return 0;
        }
    }
}
