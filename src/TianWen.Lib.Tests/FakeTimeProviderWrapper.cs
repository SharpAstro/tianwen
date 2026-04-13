using Microsoft.Extensions.Time.Testing;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Tests;

/// <summary>
/// Test <see cref="ITimeProvider"/> that wraps <see cref="FakeTimeProvider"/>.
/// <see cref="SleepAsync"/> auto-advances fake time (unless <see cref="ExternalTimePump"/> is set),
/// enabling deterministic time-dependent tests without a pump loop.
/// </summary>
public sealed class FakeTimeProviderWrapper : ITimeProvider
{
    private readonly FakeTimeProvider _fake;

    public FakeTimeProviderWrapper(DateTimeOffset? now = null, TimeSpan? autoAdvanceAmount = null)
    {
        _fake = now is { }
            ? new FakeTimeProvider(now.Value) { AutoAdvanceAmount = autoAdvanceAmount ?? TimeSpan.Zero }
            : new FakeTimeProvider() { AutoAdvanceAmount = autoAdvanceAmount ?? TimeSpan.Zero };
    }

    /// <summary>
    /// When true, <see cref="SleepAsync"/> waits for the fake time to advance (driven by an external pump)
    /// rather than advancing time itself. This prevents concurrent Advance calls from racing.
    /// </summary>
    public bool ExternalTimePump { get; set; }

    /// <summary>
    /// Advances the fake time provider by the specified duration.
    /// Only for use by the external time pump (test thread).
    /// </summary>
    public void Advance(TimeSpan duration) => _fake.Advance(duration);

    public DateTimeOffset GetUtcNow() => _fake.GetUtcNow();

    public long GetTimestamp() => _fake.GetTimestamp();

    public long TimestampFrequency => _fake.TimestampFrequency;

    public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => _fake.CreateTimer(callback, state, dueTime, period);

    public async ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (ExternalTimePump)
        {
            // Wait until the external pump has advanced time past our target
            var target = _fake.GetUtcNow() + duration;
            while (_fake.GetUtcNow() < target && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1, cancellationToken);
            }
        }
        else
        {
            _fake.Advance(duration);
        }
    }

    /// <summary>Returns the underlying <see cref="FakeTimeProvider"/> for BCL interop.</summary>
    public TimeProvider System => _fake;
}
