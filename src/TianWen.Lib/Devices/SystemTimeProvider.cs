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
/// <para>On Windows a short wait and a short one-shot timer end within about a millisecond of their time rather than at the next
/// 15.6 ms system timer tick (<see cref="HighResolutionWait"/>, <see cref="HighResolutionTimer"/>, #2020): a guide pulse timed on
/// our side lasts as long as its wait, so the tick was guiding error. Nothing here calls <c>timeBeginPeriod</c>.</para>
/// </summary>
internal sealed class SystemTimeProvider(TimeProvider? inner = null) : ITimeProvider
{
    private readonly TimeProvider _inner = inner ?? TimeProvider.System;

    public static SystemTimeProvider Instance { get; } = new SystemTimeProvider();

    public DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public long GetTimestamp() => _inner.GetTimestamp();

    public long TimestampFrequency => _inner.TimestampFrequency;

    public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => OperatingSystem.IsWindows()
            ? new HighResolutionTimer(_inner, callback, state, dueTime, period)
            : _inner.CreateTimer(callback, state, dueTime, period);

    public ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => new ValueTask(HighResolutionWait.DelayAsync(duration, cancellationToken));

    public TimeProvider System => _inner;
}
