using System;
using System.Threading;

namespace TianWen.Lib.Devices;

/// <summary>
/// A <see cref="TimeProvider"/> that shifts wall-clock "now" by a fixed <paramref name="offset"/>
/// while leaving elapsed-time and timer behaviour at real-time rate. Used by the
/// <c>TIANWEN_NOW</c> startup override (see <see cref="StartupTimeOverride"/>) so the whole system
/// (planner schedule, session loop, fake mount/camera, mount-reported UTC) can be anchored to a
/// simulated instant for testing without a separate fake-time pump.
/// <para>
/// Because the offset is fixed, the clock still advances naturally: cooling, focus and slew
/// durations stay realistic; only the absolute date/time jumps. <see cref="GetTimestamp"/> and
/// <see cref="CreateTimer"/> forward to <paramref name="inner"/> unchanged, so only the absolute
/// <see cref="GetUtcNow"/> (and the local-now derived from it) are affected.
/// </para>
/// </summary>
internal sealed class OffsetTimeProvider(TimeProvider inner, TimeSpan offset) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + offset;

    public override long GetTimestamp() => inner.GetTimestamp();

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => inner.CreateTimer(callback, state, dueTime, period);
}
