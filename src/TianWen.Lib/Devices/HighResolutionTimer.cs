using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

/// <summary>
/// The <see cref="ITimer"/> <see cref="SystemTimeProvider"/> hands out on Windows: a short ONE-SHOT is timed by
/// <see cref="HighResolutionWait"/>, so it fires within about a millisecond of its due time rather than at the next 15.6 ms
/// timer tick; anything else (a periodic timer, a long due time) is the inner provider's own timer, unchanged (#2020).
/// </summary>
/// <remarks>
/// The DAL camera's driver-timed guide pulse ends on such a one-shot, and its length is the guide correction. Each
/// <see cref="Change"/> supersedes what was armed before, as a BCL timer's does: a precise one-shot still waiting is cancelled
/// (releasing its kernel timer) and fires nothing. As with a BCL timer, a callback already past that point may still run once
/// after a <see cref="Change"/> or <see cref="Dispose"/>.
/// </remarks>
internal sealed class HighResolutionTimer : ITimer
{
    private readonly TimerCallback _callback;
    private readonly object? _state;
    private readonly ITimer _coarse;
    private CancellationTokenSource? _pending;
    private int _disposed;

    public HighResolutionTimer(TimeProvider inner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _callback = callback;
        _state = state;
        _coarse = inner.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Change(dueTime, period);
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        CancelPending(null);

        if (period == Timeout.InfiniteTimeSpan && HighResolutionWait.Applies(dueTime))
        {
            _coarse.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var armed = new CancellationTokenSource();
            CancelPending(armed);
            _ = FireAsync(dueTime, armed);
            return true;
        }

        return _coarse.Change(dueTime, period);
    }

    /// <summary>Fires the callback once <paramref name="dueTime"/> has passed, unless <paramref name="armed"/> was superseded first.</summary>
    private async Task FireAsync(TimeSpan dueTime, CancellationTokenSource armed)
    {
        // A superseded one-shot ends its wait cancelled; the slot, not the exception, says whether this one still fires.
        await HighResolutionWait.DelayAsync(dueTime, armed.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Only the one-shot still armed fires; taking it out of the slot is what makes it fire once.
        if (Interlocked.CompareExchange(ref _pending, null, armed) == armed)
        {
            _callback(_state);
        }
    }

    /// <summary>Puts <paramref name="next"/> in the slot and cancels whatever one-shot was there.</summary>
    private void CancelPending(CancellationTokenSource? next)
    {
        if (Interlocked.Exchange(ref _pending, next) is { } superseded)
        {
            // Cancelled, never disposed: a source with no timer of its own holds nothing to free, and a disposed one would
            // throw from a FireAsync still reading its token.
            superseded.Cancel();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CancelPending(null);
            _coarse.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
