using Microsoft.Extensions.Time.Testing;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Tests;

/// <summary>
/// An <see cref="ITimeProvider"/> a test moves by hand: its timestamp is the ticks it was told to advance, and its timers
/// never fire on their own, so a callback arrives exactly when (and as late as) the test says.
/// </summary>
public sealed class HandClock : ITimeProvider
{
    private readonly DateTimeOffset _epoch = new(2026, 11, 10, 22, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _bcl = new();
    private long _ticks;

    /// <summary>Every timer made on this clock, in order of creation.</summary>
    public List<HandTimer> Timers { get; } = [];

    public DateTimeOffset GetUtcNow() => _epoch + TimeSpan.FromTicks(_ticks);

    public long GetTimestamp() => _ticks;

    public long TimestampFrequency => TimeSpan.TicksPerSecond;

    public TimeProvider System => _bcl;

    public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new HandTimer(callback, state);
        Timers.Add(timer);
        return timer;
    }

    public ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        Advance(duration);
        return ValueTask.CompletedTask;
    }

    public void Advance(TimeSpan by) => _ticks += by.Ticks;

    /// <summary>The clock moves on by <paramref name="by"/> and then the newest timer's callback runs, as a tick that arrived that late.</summary>
    public void TickAfter(TimeSpan by)
    {
        Advance(by);
        Timers[^1].Fire();
    }
}

public sealed class HandTimer(TimerCallback callback, object? state) : ITimer
{
    public bool Disposed { get; private set; }

    /// <summary>Runs the callback unless the timer was disposed, as a real timer does once disposal has taken effect.</summary>
    public void Fire()
    {
        if (!Disposed)
        {
            callback(state);
        }
    }

    /// <summary>Runs the callback of a disposed timer: the one that was already queued to the pool when the dispose came.</summary>
    public void FireEvenIfDisposed() => callback(state);

    public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;

    public void Dispose() => Disposed = true;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
