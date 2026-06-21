using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

/// <summary>
/// Production <see cref="ITimeProvider"/> that forwards to an underlying <see cref="TimeProvider"/>
/// (<see cref="TimeProvider.System"/> by default). The <paramref name="inner"/> hook lets
/// <c>AddExternal</c> wrap the system clock in an <see cref="OffsetTimeProvider"/> when the
/// <c>TIANWEN_NOW</c> startup override is active (see <see cref="StartupTimeOverride"/>) without any
/// consumer change -- everything that resolves <see cref="ITimeProvider"/> from DI shifts together.
/// </summary>
internal sealed class SystemTimeProvider(TimeProvider? inner = null) : ITimeProvider
{
    private readonly TimeProvider _inner = inner ?? TimeProvider.System;

    public static SystemTimeProvider Instance { get; } = new SystemTimeProvider();

    public DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public long GetTimestamp() => _inner.GetTimestamp();

    public long TimestampFrequency => _inner.TimestampFrequency;

    public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => _inner.CreateTimer(callback, state, dueTime, period);

    public ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => new ValueTask(Task.Delay(duration, cancellationToken));

    public TimeProvider System => _inner;
}
