using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

/// <summary>
/// Production <see cref="ITimeProvider"/> that forwards to <see cref="TimeProvider.System"/>.
/// </summary>
internal sealed class SystemTimeProvider : ITimeProvider
{
    public static SystemTimeProvider Instance { get; } = new SystemTimeProvider();

    public DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

    public long GetTimestamp() => TimeProvider.System.GetTimestamp();

    public long TimestampFrequency => TimeProvider.System.TimestampFrequency;

    public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => TimeProvider.System.CreateTimer(callback, state, dueTime, period);

    public ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => new ValueTask(Task.Delay(duration, cancellationToken));

    public TimeProvider System => TimeProvider.System;
}
