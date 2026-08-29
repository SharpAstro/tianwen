using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// The waiting half of the pulse contract.
/// </summary>
/// <remarks>
/// <para><see cref="IPulseGuideTarget.StartPulseGuideAsync"/> is the primitive: it commands the
/// hardware and returns, leaving the pulse running. That is the right shape for the driver
/// interface (it is what ASCOM specifies, and it is the only shape that lets a caller drive two
/// axes at once) and the wrong shape for most callers, who simply want the mount to have finished
/// moving before they measure it again.</para>
///
/// <para>So the composite lives here: <c>PulseGuideAsync</c> starts a pulse AND waits for it.
/// Making the waiting form the ordinary one is deliberate -- the alternative is a contract whose
/// documentation has to warn that "awaiting this is not waiting for the pulse", and every caller
/// has to remember it. <see cref="GuiderCalibration"/> had hand-written this pair at eight
/// consecutive-line call sites, which is the evidence it should have been one call.</para>
///
/// <para>It is an extension rather than an interface member because a driver has nothing to
/// contribute to it: the wait is entirely expressible in terms of the primitive plus
/// <see cref="IPulseGuideTarget.IsPulseGuidingAsync"/>, so an implementation is a chance to get it
/// subtly different, not an opportunity. And it stays on the internal guider surface rather than on
/// <see cref="IMountDriver"/>, because the callers that want waiting are the guide loop and the
/// calibration routine; the Alpaca device plane and the planetary recenter nudge both genuinely
/// want start-and-return, and giving them a same-named blocking overload to trip over buys nothing.
/// </para>
/// </remarks>
internal static class PulseGuideTargetExtensions
{
    extension(IPulseGuideTarget target)
    {
        /// <summary>
        /// Start a guide pulse and wait until it has finished.
        /// </summary>
        public async ValueTask PulseGuideAsync(
            GuideDirection direction, TimeSpan duration, ITimeProvider timeProvider, CancellationToken cancellationToken)
        {
            await target.StartPulseGuideAsync(direction, duration, cancellationToken);
            await target.WaitForPulseCompleteAsync(duration, timeProvider, cancellationToken);
        }

        /// <summary>
        /// Wait for whatever pulse is currently running to finish.
        /// </summary>
        /// <remarks>
        /// <para>Coarse hop, then fine convergence: sleep through 90% of the known duration in one
        /// go, then poll. <b>The 90% is about not OVERSHOOTING, not about safety margin</b> -- a
        /// pulse's end is known to within the command latency, so sleeping the whole duration would
        /// routinely land past it and a pure poll would spend the entire pulse round-tripping
        /// <see cref="IPulseGuideTarget.IsPulseGuidingAsync"/> for an answer we can predict. This
        /// lands near the true end having asked a handful of times.</para>
        ///
        /// <para>The poll budget is bounded so a driver whose in-flight flag never clears cannot
        /// wedge the guider: it gives up and returns rather than throwing, because the caller's next
        /// measurement is a better judge of whether the mount actually moved than this loop is.</para>
        /// </remarks>
        public async ValueTask WaitForPulseCompleteAsync(
            TimeSpan pulseDuration, ITimeProvider timeProvider, CancellationToken cancellationToken)
        {
            var bulkWait = pulseDuration * 0.9;
            if (bulkWait > TimeSpan.Zero)
            {
                await timeProvider.SleepAsync(bulkWait, cancellationToken);
            }

            var pollInterval = TimeSpan.FromMilliseconds(50);
            var maxPolls = (int)(pulseDuration.TotalMilliseconds / pollInterval.TotalMilliseconds) + 20;
            while (await target.IsPulseGuidingAsync(cancellationToken) && --maxPolls > 0)
            {
                await timeProvider.SleepAsync(pollInterval, cancellationToken);
            }
        }
    }
}
