using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

/// <summary>
/// Abstraction over <see cref="TimeProvider"/> that adds <see cref="SleepAsync"/> for
/// deterministic fake-time testing. Production code uses <see cref="SystemTimeProvider"/>;
/// tests use a wrapper around <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>
/// that auto-advances on <see cref="SleepAsync"/>.
/// </summary>
public interface ITimeProvider
{
    /// <summary>Gets the current UTC time.</summary>
    DateTimeOffset GetUtcNow();

    /// <summary>Gets the current high-resolution timestamp (ticks).</summary>
    long GetTimestamp();

    /// <summary>Frequency of <see cref="GetTimestamp"/> ticks per second.</summary>
    long TimestampFrequency { get; }

    /// <summary>Returns elapsed time between two timestamps.</summary>
    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp)
        => new((long)((endingTimestamp - startingTimestamp) * ((double)TimeSpan.TicksPerSecond / TimestampFrequency)));

    /// <summary>Returns elapsed time since <paramref name="startingTimestamp"/>.</summary>
    TimeSpan GetElapsedTime(long startingTimestamp)
        => GetElapsedTime(startingTimestamp, GetTimestamp());

    /// <summary>Creates a timer backed by this time provider.</summary>
    ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period);

    /// <summary>
    /// Delays execution for the specified <paramref name="duration"/>.
    /// <para>
    /// Production (<see cref="SystemTimeProvider"/>): delegates to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// Test (FakeTimeProviderWrapper): auto-advances the fake clock, enabling deterministic
    /// time-dependent tests without an external pump loop.
    /// </para>
    /// </summary>
    ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the underlying <see cref="TimeProvider"/> for BCL interop
    /// (<see cref="PeriodicTimer"/>, <see cref="CancellationTokenSource"/>, etc.).
    /// </summary>
    TimeProvider System { get; }
}
