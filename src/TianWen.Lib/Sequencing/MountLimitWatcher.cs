using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// P3 of <c>docs/plans/mount-safety-limits.md</c>: enforces <see cref="MountLimitConfiguration"/>
/// against any connected mount, whether or not a <see cref="Session"/> owns it. <see cref="Session"/>
/// already enforces its own rig's limits every poll (<c>EnforceMountLimitsAsync</c>) -- this covers
/// the half that leaves open: a manual slew/jog/track from the GUI, the hosted API, or an unattended
/// idle rig with no run in progress. The two never fight, because this watcher checks the hub lease
/// first and steps back the instant a session owns the mount.
/// </summary>
/// <remarks>
/// <para><b>Host-agnostic by construction.</b> It depends only on <see cref="IDeviceHub"/> (which
/// mount is connected, and whether a run has leased it) and <see cref="IDeviceDiscovery"/> (which
/// profile configured limits for that mount's URI) -- both already singletons in every host
/// (CLI, GUI, Server). <see cref="RunAsync"/> is a plain loop, not an
/// <c>IHostedService</c>/<c>BackgroundService</c>, since <c>TianWen.Lib</c> takes no dependency on
/// ASP.NET/Generic Host: a host wires it up as it sees fit (a <c>BackgroundService</c> in
/// <c>TianWen.Hosting</c>, a fire-and-forget <c>Task.Run</c> in the GUI's own composition root).</para>
///
/// <para><b>MountLimitConfiguration is looked up by matching the mount's connected URI against
/// every known profile's <see cref="ProfileData.Mount"/>,</b> not from any single "active profile"
/// concept -- there isn't a uniform one across hosts (the GUI, the hosted server, and the CLI each
/// track "which profile" differently). Re-discovering profiles every tick is deliberate: it is what
/// lets a limit just enabled in the profile editor take effect without a process restart, and
/// re-scanning a handful of small JSON files every 5 s costs nothing worth avoiding.</para>
///
/// <para><b>The per-entry latch is per MOUNT URI, not a single field</b> (unlike <see cref="Session"/>,
/// which only ever drives one rig): the hub can have more than one mount connected across profiles,
/// and each needs its own "have I already acted since the last clear verdict" memory.</para>
/// </remarks>
public sealed class MountLimitWatcher(
    IDeviceHub hub,
    IDeviceDiscovery deviceDiscovery,
    ITimeProvider timeProvider,
    ILogger<MountLimitWatcher> logger)
{
    /// <summary>
    /// Poll cadence for mounts nobody is imaging with. Slower than a session's own poll (which runs
    /// on every slew wait and imaging tick) because nothing here is time-critical the way a running
    /// exposure is -- 5 s is the plan's own figure, fast enough to catch a manual slew well before it
    /// reaches a pier, slow enough to cost nothing on an idle rig.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // Per-mount "have I already acted since the last clear verdict" latch -- see MountLimits.Evaluate's
    // alreadyActed parameter. A set (ConcurrentDictionary<Uri, byte>, per CLAUDE.md's per-key
    // in-flight-set convention), keyed on device URI because more than one mount can be connected to
    // the hub at once (across profiles); a single field, as Session uses for its own one rig, would
    // let one mount's latch mask another's.
    private readonly ConcurrentDictionary<Uri, byte> _acted = new();

    // The latest verdict per mount this watcher evaluated on its last tick, keyed by the hub's identity rule
    // (Uri.DeviceKey) so a profile URI whose query has drifted still finds it. Entries for mounts the tick
    // skipped (disconnected, leased by a session, no limits configured) are dropped, so a stale verdict can
    // never outlive the situation that produced it.
    private readonly ConcurrentDictionary<string, MountLimitVerdict> _verdicts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The verdict from the last tick for the mount at <paramref name="mountUri"/> (query ignored), or
    /// <see cref="MountLimitVerdict.Clear"/> when this watcher is not evaluating that mount. This is the
    /// surfacing seam for a host with NO session: the GUI/TUI feed the local rig's verdict from here when
    /// nothing else owns the mount, so a manual slew a limit stops shows on the Home card and in the feed
    /// instead of only in the log -- which is exactly how it presented the first time it ran live.
    /// </summary>
    public MountLimitVerdict VerdictFor(Uri mountUri)
        => _verdicts.TryGetValue(mountUri.DeviceKey, out var verdict) ? verdict : MountLimitVerdict.Clear;

    /// <summary>
    /// Runs until <paramref name="cancellationToken"/> is cancelled, ticking every <see cref="PollInterval"/>.
    /// Never throws for a transient per-mount failure: a single mount's bad read is logged and skipped,
    /// not allowed to end the watcher for every other connected rig.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Mount limit watcher started (poll interval {Interval}).", PollInterval);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Best-effort: a bug here must not silence the one thing standing between an
                // unattended manual slew and a pier strike. Log and keep polling.
                logger.LogError(ex, "Mount limit watcher tick failed; will retry at the next poll.");
            }

            await timeProvider.SleepAsync(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("Mount limit watcher stopped.");
    }

    /// <summary>
    /// One pass over every connected mount. Internal (not private) so tests can drive a single tick
    /// deterministically instead of racing <see cref="RunAsync"/>'s own sleep loop.
    /// </summary>
    internal async ValueTask TickAsync(CancellationToken cancellationToken)
    {
        // Re-discover profiles every tick: a limit just enabled in the editor must take effect without
        // a restart, and this is a handful of small JSON files, not a cost worth caching around.
        await deviceDiscovery.DiscoverOnlyDeviceType(DeviceType.Profile, cancellationToken).ConfigureAwait(false);

        var evaluated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (deviceUri, driver) in hub.ConnectedDevices)
        {
            if (driver is not IMountDriver mount)
            {
                continue;
            }

            // A run owns this mount: its own Session.EnforceMountLimitsAsync already evaluates and
            // acts on every poll. Acting here too would be two actors racing the same axis.
            if (hub.TryGetLease(deviceUri, out _))
            {
                continue;
            }

            if (FindConfiguration(deviceUri) is not { Config.Enabled: true } found)
            {
                continue;
            }

            evaluated.Add(deviceUri.DeviceKey);
            await EvaluateAndActAsync(deviceUri, mount, found.Config, found.SiteLatitude, cancellationToken)
                .ConfigureAwait(false);
        }

        // Forget every mount this tick did not evaluate (see _verdicts).
        foreach (var key in _verdicts.Keys)
        {
            if (!evaluated.Contains(key))
            {
                _verdicts.TryRemove(key, out _);
            }
        }
    }

    /// <summary>The driver's null ("no axis model") carried as NaN, the way the session's <c>MountState</c> does.</summary>
    private static async ValueTask<double> ReadPrimaryAxisAngleAsync(IMountDriver mount, CancellationToken cancellationToken)
        => await mount.GetAxisAngleAsync(TelescopeAxis.Primary, cancellationToken).ConfigureAwait(false) ?? double.NaN;

    private (MountLimitConfiguration Config, double? SiteLatitude)? FindConfiguration(Uri mountUri)
    {
        foreach (var device in deviceDiscovery.RegisteredDevices(DeviceType.Profile))
        {
            // The hub's own identity rule (scheme + host + path): a profile whose mount query has drifted
            // from the connected URI (re-discovery, a reconciled setting) is still this mount's profile.
            if (device is Profile { Data: { } data }
                && data.Mount is { } profileMount
                && string.Equals(profileMount.DeviceKey, mountUri.DeviceKey, StringComparison.OrdinalIgnoreCase)
                && data.MountLimits is { } limits)
            {
                return (limits, data.SiteLatitude);
            }
        }

        return null;
    }

    private async ValueTask EvaluateAndActAsync(
        Uri mountUri,
        IMountDriver mount,
        MountLimitConfiguration config,
        double? siteLatitude,
        CancellationToken cancellationToken)
    {
        var hourAngle = await mount.Logger.CatchAsync(mount.GetHourAngleAsync, cancellationToken, double.NaN).ConfigureAwait(false);
        // Unknown on a failed read, never Normal: Normal is the FLIPPED state, in which the meridian
        // limit is silent, so defaulting there would switch the limit off on the mounts it reads worst.
        var pointingState = MountLimits.TrustedPointingState(
            mount.PointingStateSource,
            await mount.Logger.CatchAsync(mount.GetSideOfPierAsync, cancellationToken, PointingState.Unknown).ConfigureAwait(false));
        // The mechanical tier where the driver has one; null (the interface default) elsewhere, and NaN
        // on a failed read too -- an unreadable axis must fall back to the estimate, not fire.
        var primaryAxisAngleDeg = await mount.Logger.CatchAsync(
            ct => ReadPrimaryAxisAngleAsync(mount, ct), cancellationToken, double.NaN).ConfigureAwait(false);
        var declination = await mount.Logger.CatchAsync(mount.GetDeclinationAsync, cancellationToken, double.NaN).ConfigureAwait(false);
        var isTracking = await mount.Logger.CatchAsync(mount.IsTrackingAsync, cancellationToken).ConfigureAwait(false);

        // No site latitude on this profile: the horizon test declines rather than guesses (NaN
        // altitude), exactly as it does for a real driver read failure. The meridian test needs no
        // site at all, so it still runs. The static overload needs only latitude (altitude from
        // HA + Dec + Lat has no dependence on longitude or the clock), so there is no ITimeProvider
        // read on this path at all.
        var altitude = siteLatitude is { } lat
            ? SiteContext.AltitudeDegrees(lat, hourAngle, declination)
            : double.NaN;

        var alreadyActed = _acted.ContainsKey(mountUri);
        var verdict = MountLimits.Evaluate(hourAngle, pointingState, primaryAxisAngleDeg, altitude, isTracking, alreadyActed, config);
        _verdicts[mountUri.DeviceKey] = verdict;

        if (!verdict.IsBreached)
        {
            if (_acted.TryRemove(mountUri, out _))
            {
                logger.LogInformation("{Mount} is clear of its safety limits again.", mount.Name);
            }
            return;
        }

        if (verdict.IsWarningOnly || verdict.Response is MountLimitResponse.Warn)
        {
            logger.LogWarning("{Mount} mount safety limit: {Verdict}", mount.Name, verdict.Describe());
            return;
        }

        // Describe() is a full sentence already; no separator before the next one.
        logger.LogError("{Mount} mount safety limit: {Verdict} Responding with {Response} (no session owns this mount).",
            mount.Name, verdict.Describe(), verdict.Response);
        _acted[mountUri] = 0;

        // Stop tracking first in BOTH responses -- see Session.EnforceMountLimitsAsync's remarks: a
        // park is motion across a path nothing has checked, so the axis should not still be driving
        // toward the limit while it is under way.
        await ResilientCall.InvokeAsync(
            mount, ct => mount.SetTrackingAsync(false, ct),
            ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);

        if (verdict.Response is MountLimitResponse.Park && mount.CanPark)
        {
            await ResilientCall.InvokeAsync(
                mount, mount.ParkAsync, ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);
        }
        else if (verdict.Response is MountLimitResponse.Park)
        {
            logger.LogWarning("{Mount} mount safety limit asked for a park, but this mount cannot park; tracking was stopped instead.", mount.Name);
        }
    }
}
