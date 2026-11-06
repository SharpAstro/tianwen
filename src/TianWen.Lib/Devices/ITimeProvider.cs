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
    /// Production (<see cref="SystemTimeProvider"/>): <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, except a short wait on
    /// Windows, which waits on a high-resolution timer so it does not last a whole 15.6 ms timer tick (<see cref="HighResolutionWait"/>).
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

public static class TimeProviderExtensions
{
    extension(ITimeProvider timeProvider)
    {
        /// <summary>
        /// A <see cref="CancellationTokenSource"/> that cancels after <paramref name="delay"/> on THIS clock: the one way to
        /// build a deadline, so a test's fake clock ends it when advanced past it. Never <c>new CancellationTokenSource(delay)</c>
        /// or <c>CancelAfter</c> on a plain source, which run on the wall clock whatever the injected one says. An extension,
        /// not a default interface member, so it is called on a concrete clock (a test's fake) without a cast.
        /// </summary>
        public CancellationTokenSource CancelAfter(TimeSpan delay) => new(delay, timeProvider.System);
    }

    extension(TimeProvider clock)
    {
        /// <summary>
        /// The same deadline for a clock held as a BCL <see cref="TimeProvider"/> rather than an <see cref="ITimeProvider"/>:
        /// it runs on THAT clock, whichever it is, so the one spelling covers both.
        /// </summary>
        public CancellationTokenSource CancelAfter(TimeSpan delay) => new(delay, clock);
    }
}
