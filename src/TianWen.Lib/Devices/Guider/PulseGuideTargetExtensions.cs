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
        /// Apply an RA correction and a Dec correction together, overlapping them when the target
        /// allows it, and return once both have finished. Either may be <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>The sequential fallback lives HERE, not in the caller.</b> Whether two axes may
        /// be in flight at once is a fact only the driver knows
        /// (<see cref="IPulseGuideTarget.CanPulseGuideSimultaneously"/>), so a guide loop that
        /// branched on it would be carrying a decision on the driver's behalf, and every future
        /// caller would carry it again.</para>
        ///
        /// <para>Both corrections come from ONE star measurement at ONE instant, which is the whole
        /// argument for overlapping: serialised, the Dec pulse begins `raMs` after the measurement
        /// it answers, and the pair costs `raMs + decMs` rather than `max(raMs, decMs)`.</para>
        ///
        /// <para>Note the wait is ONE wait, not one per axis, because
        /// <see cref="IPulseGuideTarget.IsPulseGuidingAsync"/> is a single flag covering both --
        /// which is also why the coarse hop uses the LONGER of the two durations.</para>
        /// </remarks>
        public async ValueTask PulseGuideAsync(
            GuidePulse? ra, GuidePulse? dec, ITimeProvider timeProvider, CancellationToken cancellationToken)
        {
            if (ra is { Direction: var raDir and not (GuideDirection.East or GuideDirection.West) })
            {
                throw new ArgumentException($"{raDir} is not an RA direction", nameof(ra));
            }
            if (dec is { Direction: var decDir and not (GuideDirection.North or GuideDirection.South) })
            {
                throw new ArgumentException($"{decDir} is not a Dec direction", nameof(dec));
            }

            if (ra is not { } raPulse)
            {
                if (dec is { } decOnly)
                {
                    await target.PulseGuideAsync(decOnly.Direction, decOnly.Duration, timeProvider, cancellationToken);
                }
                return;
            }

            if (dec is not { } decPulse)
            {
                await target.PulseGuideAsync(raPulse.Direction, raPulse.Duration, timeProvider, cancellationToken);
                return;
            }

            if (!target.CanPulseGuideSimultaneously)
            {
                await target.PulseGuideAsync(raPulse.Direction, raPulse.Duration, timeProvider, cancellationToken);
                await target.PulseGuideAsync(decPulse.Direction, decPulse.Duration, timeProvider, cancellationToken);
                return;
            }

            // Start both, THEN await both, and note the order: calling an async method runs it up to
            // its first await, so by the time we await either one BOTH holds are already in flight.
            // This is the allocation-free stand-in for Task.WhenAll, which cannot take a ValueTask;
            // there is no ValueTask combinator in the BCL and none in the org (DotNext had a tuple
            // WhenAll and dropped it after 4.x). Starting them separately matters most for a target
            // whose start still BLOCKS -- SkyWatcher, until its conversion -- which would otherwise
            // serialise here exactly as the caller used to.
            //
            // The try/finally is load-bearing rather than tidy: if the RA pulse faults (a rate
            // restore that would not take, say) the Dec pulse must STILL be awaited, or its own stop
            // goes unobserved and an axis is left running with nobody watching. When both fault the
            // Dec exception wins, which is an acceptable loss -- they say the same thing.
            var raStart = target.StartPulseGuideAsync(raPulse.Direction, raPulse.Duration, cancellationToken);
            var decStart = target.StartPulseGuideAsync(decPulse.Direction, decPulse.Duration, cancellationToken);
            try
            {
                await raStart;
            }
            finally
            {
                await decStart;
            }

            var longest = raPulse.Duration > decPulse.Duration ? raPulse.Duration : decPulse.Duration;
            await target.WaitForPulseCompleteAsync(longest, timeProvider, cancellationToken);
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
