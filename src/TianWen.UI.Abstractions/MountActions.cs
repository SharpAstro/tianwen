using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Pure-dependency helpers for user-initiated mount operations the GUI alone runs: the nudge. Wraps
    /// <see cref="IMountDriver"/> primitives so the signal-handler lambdas stay routing-only and the logic is directly
    /// unit-testable against <c>Substitute.For&lt;IMountDriver&gt;()</c>. The goto is <see cref="MountGoto"/> and solve
    /// and sync <see cref="MountSolveSync"/>, in Lib, where the node runs them too.
    /// </summary>
    public static class MountActions
    {
        /// <summary>
        /// Pulse-guides the mount by an angular amount in a cardinal guide <paramref name="direction"/> -- the
        /// single mount-nudge actuator shared by the planetary COM recenter loop (the coarse, edge-blocked
        /// fallback) and the manual nudge buttons. The pulse duration is derived from the axis's guide rate
        /// (RA rate for East/West, Dec rate for North/South): <c>ms = arcsec / (rateDegPerSec * 3600) * 1000</c>,
        /// then clamped to [1 ms, <paramref name="maxPulse"/>] so a large requested move never issues an
        /// unbounded pulse. Best-effort: no-op when the mount is disconnected, can't pulse-guide, the request is
        /// non-positive, or the guide rate is unknown/zero (we can't size a pulse without it).
        /// </summary>
        /// <param name="mount">A connected mount supporting pulse-guide.</param>
        /// <param name="direction">Cardinal guide direction to pulse.</param>
        /// <param name="arcsec">Magnitude of the nudge in arcseconds (absolute value).</param>
        /// <param name="timeProvider">Time source (unused today; kept for symmetry + future settle waits).</param>
        /// <param name="maxPulse">Upper bound on the pulse duration (default 2 s).</param>
        /// <param name="logger">Optional logger for the sizing breadcrumb.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task PulseGuideArcsecAsync(
            IMountDriver mount,
            GuideDirection direction,
            double arcsec,
            ITimeProvider timeProvider,
            TimeSpan? maxPulse = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            _ = timeProvider;
            if (!mount.Connected || !mount.CanPulseGuide || !(arcsec > 0.0))
            {
                return;
            }

            var rateDegPerSec = direction is GuideDirection.East or GuideDirection.West
                ? await mount.GetGuideRateRightAscensionAsync(cancellationToken)
                : await mount.GetGuideRateDeclinationAsync(cancellationToken);

            var arcsecPerSec = rateDegPerSec * 3600.0;
            if (!(arcsecPerSec > 0.0))
            {
                logger?.LogDebug("Recenter mount pulse skipped: {Dir} guide rate is {Rate} deg/s.", direction, rateDegPerSec);
                return;
            }

            var ms = arcsec / arcsecPerSec * 1000.0;
            var cap = (maxPulse ?? TimeSpan.FromSeconds(2)).TotalMilliseconds;
            ms = Math.Clamp(ms, 1.0, cap);

            logger?.LogDebug(
                "Recenter mount pulse {Dir} {Arcsec:F1} arcsec -> {Ms:F0} ms (guide rate {Rate:F4} deg/s).",
                direction, arcsec, ms, rateDegPerSec);

            await mount.StartPulseGuideAsync(direction, TimeSpan.FromMilliseconds(ms), cancellationToken);
        }
    }
}
