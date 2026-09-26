using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.DAL;

namespace TianWen.Lib.Devices;

/// <summary>
/// The mount nudge: a pulse-guide by an ANGLE in a cardinal direction, the one actuator the planetary recenter loop's coarse
/// fallback and the manual nudge buttons share, in the GUI and the node alike. Moved here from the GUI's
/// <c>MountActions</c> (P5 part 5 of docs/plans/hardware-in-the-server.md, #934).
/// </summary>
public static class MountNudge
{
    /// <summary>
    /// Pulse-guides <paramref name="mount"/> by <paramref name="arcsec"/> towards <paramref name="direction"/>. The pulse
    /// is sized from that axis's guide rate (RA for East and West, Dec for North and South),
    /// <c>ms = arcsec / (rateDegPerSec * 3600) * 1000</c>, then clamped to [1 ms, <paramref name="maxPulse"/>], so a large
    /// request never issues an unbounded pulse. Best effort: nothing is issued when the mount is disconnected, cannot
    /// pulse-guide, the request is not positive, or the guide rate is unknown or zero, since no pulse can be sized then.
    /// </summary>
    /// <param name="maxPulse">Upper bound on the pulse duration (default 2 s).</param>
    /// <param name="logger">Optional logger for the sizing breadcrumb.</param>
    public static async Task PulseArcsecAsync(
        IMountDriver mount,
        GuideDirection direction,
        double arcsec,
        TimeSpan? maxPulse = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
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
            logger?.LogDebug("Mount nudge skipped: {Dir} guide rate is {Rate} deg/s.", direction, rateDegPerSec);
            return;
        }

        var ms = arcsec / arcsecPerSec * 1000.0;
        var cap = (maxPulse ?? TimeSpan.FromSeconds(2)).TotalMilliseconds;
        ms = Math.Clamp(ms, 1.0, cap);

        logger?.LogDebug(
            "Mount nudge {Dir} {Arcsec:F1} arcsec -> {Ms:F0} ms (guide rate {Rate:F4} deg/s).",
            direction, arcsec, ms, rateDegPerSec);

        await mount.StartPulseGuideAsync(direction, TimeSpan.FromMilliseconds(ms), cancellationToken);
    }
}
