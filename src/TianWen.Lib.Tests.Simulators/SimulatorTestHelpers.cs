using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Tests.Simulators;

internal static class SimulatorTestHelpers
{
    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true or <paramref name="timeout"/> elapses,
    /// sleeping between polls. The wait goes through <see cref="ITimeProvider"/> (not raw
    /// <c>Task.Delay</c>/<c>Stopwatch</c>) to satisfy the project's "no raw Task.Delay" rule -- but the
    /// caller passes a real System-backed provider (<see cref="SystemTimeProvider.Instance"/>), NOT a
    /// fake one: the simulator runs in real wall-clock time over HTTP/COM, so a fake clock's
    /// auto-advancing <c>SleepAsync</c> would busy-spin instead of actually waiting.
    /// </summary>
    public static async Task<bool> WaitAsync(ITimeProvider timeProvider, Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var start = timeProvider.GetTimestamp();
        while (timeProvider.GetElapsedTime(start) < timeout)
        {
            if (await condition()) return true;
            await timeProvider.SleepAsync(TimeSpan.FromMilliseconds(200), ct);
        }
        return await condition();
    }
}
