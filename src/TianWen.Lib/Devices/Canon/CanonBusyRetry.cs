using FC.SDK.Canon;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Canon;

/// <summary>
/// A Canon body answers <c>DeviceBusy</c> to a write sent while it is still busy with the last one (or with a release), and
/// the write is then simply not made. The driver used to go on regardless, so an exposure ran at the previous shutter speed
/// and ISO with nothing said. Busy is a transient answer: ask again after a short wait, and report it if it stays.
/// </summary>
internal static class CanonBusyRetry
{
    /// <summary>How often a busy write is tried, in all.</summary>
    public const int Attempts = 6;

    /// <summary>The wait between tries.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Runs <paramref name="attempt"/> until it is not answered <see cref="EdsError.DeviceBusy"/>, at most
    /// <see cref="Attempts"/> times, and returns the last answer.
    /// </summary>
    public static async ValueTask<EdsError> RunAsync(Func<Task<EdsError>> attempt, ITimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var result = await attempt();
        for (var tries = 1; result is EdsError.DeviceBusy && tries < Attempts; tries++)
        {
            await timeProvider.SleepAsync(Delay, cancellationToken);
            result = await attempt();
        }

        return result;
    }
}
