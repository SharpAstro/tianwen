using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

/// <summary>
/// Shared polling helpers for <see cref="ICameraDriver"/>.
/// </summary>
internal static class CameraDriverExtensions
{
    // Adaptive image-ready poll cadence. During the bulk of an exposure the frame
    // cannot possibly be ready, so we sleep straight through that dead time in one
    // chunk (zero polls); only once we are within the lead margin of the predicted
    // end do we start polling, coarse first and then a 1 ms cadence for the final
    // stretch so pickup latency at exposure end stays near-minimal. GetImageReadyAsync
    // is a network round-trip on ASCOM/Alpaca cameras, so the long dead time is
    // exactly where naive fixed-interval polling wastes effort.
    private static readonly TimeSpan _coarsePollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan _finePollInterval = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan _finePollWindow = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Computes the next image-ready poll delay from the time <paramref name="remaining"/>
    /// until the exposure's predicted end. More than <paramref name="leadMargin"/> remaining
    /// returns a single long sleep that lands <paramref name="leadMargin"/> before the end
    /// (so the dead time costs no polls); inside the lead window it returns a coarse 10 ms
    /// cadence; in the final ~10 ms — and on overrun, where <paramref name="remaining"/> is
    /// &lt;= zero — it returns a 1 ms cadence. The result is always strictly positive, so a
    /// caller loop can never busy-spin.
    /// </summary>
    internal static TimeSpan NextImageReadyPollDelay(TimeSpan remaining, TimeSpan leadMargin)
    {
        if (remaining > leadMargin)
        {
            return remaining - leadMargin;
        }

        if (remaining > _finePollWindow)
        {
            return _coarsePollInterval;
        }

        return _finePollInterval;
    }

    extension(ICameraDriver camera)
    {
        /// <summary>
        /// Waits until the camera reports image-ready, polling with an adaptive cadence
        /// (see <see cref="NextImageReadyPollDelay"/>): one long sleep through the exposure's
        /// dead time, then a coarse cadence through the final <paramref name="pollLeadMargin"/>,
        /// then 1 ms polls in the final window (and on overrun) until ready. Assumes an
        /// exposure of <paramref name="exposure"/> was just started — the caller owns the
        /// preceding <c>StartExposureAsync</c>.
        /// </summary>
        /// <param name="exposure">The exposure duration just started; used to predict the ready time.</param>
        /// <param name="timeProvider">Clock for elapsed-time measurement and sleeping (fake-time testable).</param>
        /// <param name="pollLeadMargin">How far before the predicted end to switch from one long sleep to active polling.</param>
        /// <param name="timeout">Optional overall budget measured from entry; when it elapses the method returns <c>false</c> without the image being ready. <c>null</c> waits indefinitely (bounded only by <paramref name="cancellationToken"/>).</param>
        /// <param name="cancellationToken">Cancels the wait (surfaces as <see cref="OperationCanceledException"/> from the sleep).</param>
        /// <returns><c>true</c> once image-ready; <c>false</c> if <paramref name="timeout"/> elapsed first.</returns>
        public async ValueTask<bool> WaitForImageReadyAsync(TimeSpan exposure, ITimeProvider timeProvider, TimeSpan pollLeadMargin, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var startedAt = timeProvider.GetTimestamp();

            while (!await camera.GetImageReadyAsync(cancellationToken))
            {
                var elapsed = timeProvider.GetElapsedTime(startedAt);
                if (timeout is { } budget && elapsed >= budget)
                {
                    return false;
                }

                await timeProvider.SleepAsync(NextImageReadyPollDelay(exposure - elapsed, pollLeadMargin), cancellationToken);
            }

            return true;
        }
    }
}
