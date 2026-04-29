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
    protected volatile int _position;
    protected volatile bool _isMoving;

    private ITimer? _movingTimer;

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
            _isMoving = true;

            var state = new MovingState(currentPosition, position);

            var movingTimer = TimeProvider.CreateTimer(MovingTimerCallback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            Interlocked.Exchange(ref _movingTimer, movingTimer)?.Dispose();

            // 5ms per step = 200 steps/sec. The previous 100ms (10 steps/sec)
            // made GUI focuser jog feel laggy: a `>` button click fires a
            // +10 jog that took a full second to walk to its target one step
            // at a time -- the user sees +1 and assumes the button only
            // increments by 1. Real focusers run on the order of 100-500
            // steps/sec; tests use FakeTimeProvider so wall-time cadence
            // doesn't affect them.
            movingTimer.Change(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5));

        }

        return Task.CompletedTask;
    }

    protected virtual Task BeginStopMovingAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _movingTimer, null)?.Dispose();
        _isMoving = false;
        return Task.CompletedTask;
    }

    private void MovingTimerCallback(object? state)
    {
        if (state is MovingState movingState && _isMoving)
        {
            // check if Halt has been called
            if (!_isMoving)
            {
                return;
            }

            var currentPosition = _position;
            switch (Math.Sign(movingState.End - currentPosition))
            {
                case -1:
                    Interlocked.CompareExchange(ref _position, currentPosition - 1, currentPosition);
                    break;

                case 1:
                    Interlocked.CompareExchange(ref _position, currentPosition + 1, currentPosition);
                    break;

                case 0:
                    _isMoving = false;
                    Interlocked.Exchange(ref _movingTimer, null)?.Dispose();
                    break;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            Interlocked.Exchange(ref _movingTimer, null)?.Dispose();
        }
    }

    private record MovingState(int Start, int End);
}
