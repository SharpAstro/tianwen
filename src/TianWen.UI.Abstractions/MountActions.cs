using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Pure-dependency helpers for user-initiated mount operations (ad-hoc goto from
    /// the sky-map info panel, etc). Wraps <see cref="IMountDriver"/> primitives so
    /// the signal-handler lambdas stay routing-only and the logic is directly
    /// unit-testable against <c>Substitute.For&lt;IMountDriver&gt;()</c>.
    /// </summary>
    public static class MountActions
    {
        /// <summary>
        /// Slew a connected mount to a J2000 RA/Dec catalog position. The flow:
        /// <list type="number">
        ///   <item>Connected / CanSlewAsync gates — bail cleanly if unsatisfiable.</item>
        ///   <item>Auto-unpark if <see cref="IMountDriver.CanUnpark"/> and
        ///   <see cref="IMountDriver.AtParkAsync"/>.</item>
        ///   <item>Build the J2000→native <see cref="TianWen.Lib.Astrometry.SOFA.Transform"/>
        ///   from <paramref name="profile"/> + <paramref name="timeProvider"/> (not from
        ///   the mount). This sidesteps the mount's <c>TryGetTransformAsync</c> path,
        ///   which depends on <c>UTCDate</c> being readable and can fail silently on
        ///   certain ASCOM drivers (e.g. GSServer). The Equipment-tab Connect flow
        ///   already validated site coordinates on the profile, so trusting it is safe.</item>
        ///   <item>Compute native coordinates via <see cref="IMountDriver.TryTransformJ2000ToMountNativeAsync"/>
        ///   using our transform (<c>updateTime: false</c> so the helper does not read
        ///   the mount clock).</item>
        ///   <item>Horizon check against <paramref name="minAboveHorizonDegrees"/>.</item>
        ///   <item>Destination-pier-side sanity check.</item>
        ///   <item>Tracking setup — Solar / Lunar / Sidereal depending on
        ///   <paramref name="index"/>, with graceful fallback to Sidereal on mounts
        ///   that don't advertise the preferred rate, and best-effort (swallow +
        ///   log) behaviour if the driver throws.</item>
        ///   <item><see cref="IMountDriver.BeginSlewRaDecAsync"/> — the actual commit.</item>
        /// </list>
        /// </summary>
        /// <param name="mount">A connected mount driver.</param>
        /// <param name="name">Display name for status messages.</param>
        /// <param name="raJ2000Hours">Right ascension in J2000 hours.</param>
        /// <param name="decJ2000Deg">Declination in J2000 degrees.</param>
        /// <param name="index">Catalog index, used to pick Sun/Moon tracking rate.</param>
        /// <param name="profile">Active profile (for site coordinates).</param>
        /// <param name="timeProvider">System time source (for the transform's UTC).</param>
        /// <param name="minAboveHorizonDegrees">Minimum altitude above horizon.
        /// Callers should pass <c>PlannerState.MinHeightAboveHorizon</c> (clamped ≥ 1).</param>
        /// <param name="logger">Optional logger for exceptions swallowed by the helper.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task<(SlewPostCondition Post, string StatusMessage)> SlewToJ2000Async(
            IMountDriver mount,
            string name,
            double raJ2000Hours,
            double decJ2000Deg,
            CatalogIndex? index,
            Profile profile,
            ITimeProvider timeProvider,
            int minAboveHorizonDegrees = 10,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            if (!mount.Connected)
            {
                return (SlewPostCondition.SlewNotPossible, "Mount is not connected");
            }

            if (!mount.CanSlewAsync)
            {
                return (SlewPostCondition.SlewNotPossible, "Mount does not support async slewing");
            }

            // Park handling — only meaningful when the mount supports programmatic Unpark.
            // Many ASCOM drivers throw on Tracking/Slew while parked, so we auto-unpark first.
            var wasParked = false;
            if (mount.CanUnpark)
            {
                try
                {
                    if (await mount.AtParkAsync(cancellationToken))
                    {
                        await mount.UnparkAsync(cancellationToken);
                        wasParked = true;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex,
                        "Auto-unpark failed on {Mount} during Goto {Target}", mount.Name, name);
                    return (SlewPostCondition.SlewNotPossible,
                        $"Mount is parked and auto-unpark failed: {ex.Message}");
                }
            }

            // Mounts with neither Park nor Unpark — surface as an informational suffix.
            var noParkSupportNote = (!mount.CanPark && !mount.CanUnpark)
                ? " (mount has no park/unpark support)"
                : "";

            // Build the transform from profile + wall clock. Bypasses any mount-side
            // UTCDate/Site reads that may be flaky on some drivers.
            var transform = TransformFactory.FromProfile(profile, timeProvider, out var transformError);
            if (transform is null)
            {
                logger?.LogWarning("Goto {Target}: profile-derived transform unavailable: {Reason}", name, transformError);
                return (SlewPostCondition.SlewNotPossible,
                    $"Cannot slew to {name} \u2014 {transformError ?? "profile site coordinates unavailable"}");
            }

            // Convert J2000 → mount native. updateTime: false so we don't round-trip UTC
            // through the mount; our profile-based transform already has the time set.
            if (await mount.TryTransformJ2000ToMountNativeAsync(
                    transform, raJ2000Hours, decJ2000Deg, updateTime: false, cancellationToken) is not { } native)
            {
                logger?.LogWarning(
                    "Goto {Target}: coordinate transform produced NaN. Mount={Mount} EquatorialSystem={System} SiteLat={Lat:F4} SiteLon={Lon:F4}",
                    name, mount.Name, mount.EquatorialSystem, transform.SiteLatitude, transform.SiteLongitude);
                return (SlewPostCondition.SlewNotPossible,
                    $"Cannot slew to {name} \u2014 coordinate transform failed (EquatorialSystem={mount.EquatorialSystem}){noParkSupportNote}");
            }

            // Horizon check — use the alt we just computed, not a second mount call.
            if (native.Alt < minAboveHorizonDegrees)
            {
                return (SlewPostCondition.TargetBelowHorizonLimit,
                    $"{name} is below the horizon limit ({minAboveHorizonDegrees}\u00B0){noParkSupportNote}");
            }

            // Destination-pier-side sanity (same check the session's BeginSlewToTargetAsync does).
            PointingState dsop;
            try
            {
                dsop = await mount.DestinationSideOfPierAsync(native.RaMount, native.DecMount, cancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "DestinationSideOfPier failed on {Mount} for Goto {Target}", mount.Name, name);
                return (SlewPostCondition.SlewNotPossible,
                    $"Cannot slew to {name} \u2014 mount rejected destination pier check: {ex.Message}{noParkSupportNote}");
            }
            if (dsop == PointingState.Unknown)
            {
                return (SlewPostCondition.SlewNotPossible,
                    $"Cannot slew to {name} \u2014 mount could not determine destination pier side{noParkSupportNote}");
            }

            // Tracking rate selection — Sun / Moon / Sidereal with graceful fallback.
            var preferred = index switch
            {
                CatalogIndex.Sol  => TrackingSpeed.Solar,
                CatalogIndex.Moon => TrackingSpeed.Lunar,
                _                 => TrackingSpeed.Sidereal
            };
            var actualSpeed = mount.TrackingSpeeds.Contains(preferred) ? preferred : TrackingSpeed.Sidereal;

            // Best-effort tracking setup. Tracking failures don't block pointing — the
            // subsequent BeginSlewRaDec call still works.
            var trackingFailed = false;
            try
            {
                await mount.EnsureTrackingAsync(actualSpeed, cancellationToken);
            }
            catch (Exception ex)
            {
                trackingFailed = true;
                logger?.LogWarning(ex,
                    "EnsureTracking({Speed}) failed on {Mount} during Goto {Target}; continuing with slew",
                    actualSpeed, mount.Name, name);
            }

            // Commit the slew.
            await mount.BeginSlewRaDecAsync(native.RaMount, native.DecMount, cancellationToken);

            var rateNote = trackingFailed
                ? " (tracking not set \u2014 check mount state)"
                : actualSpeed != preferred
                    ? $" (tracking at sidereal \u2014 mount does not support {preferred})"
                    : actualSpeed switch
                    {
                        TrackingSpeed.Solar => " (solar tracking)",
                        TrackingSpeed.Lunar => " (lunar tracking)",
                        _                   => ""
                    };
            var parkedNote = wasParked ? " (auto-unparked)" : "";

            return (SlewPostCondition.Slewing, $"Slewing to {name}{rateNote}{parkedNote}{noParkSupportNote}");
        }

        /// <summary>
        /// Polls <see cref="IMountDriver.IsSlewingAsync"/> until it returns false (slew complete),
        /// the timeout expires, or the token is cancelled. Returns a user-facing status string
        /// suitable for an <c>Info</c>-severity notification ("Reached M31") or a <c>Warning</c>
        /// for the timeout / fault paths. The caller is responsible for posting the notification.
        /// </summary>
        public enum SlewCompletion { Reached, TimedOut, PollFailed }

        public static async Task<(SlewCompletion Result, string StatusMessage)> AwaitSlewCompletionAsync(
            IMountDriver mount,
            string name,
            ITimeProvider timeProvider,
            TimeSpan? timeout = null,
            TimeSpan? pollInterval = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            var deadline = timeProvider.GetUtcNow() + (timeout ?? TimeSpan.FromMinutes(5));
            var interval = pollInterval ?? TimeSpan.FromMilliseconds(500);

            while (timeProvider.GetUtcNow() < deadline)
            {
                await timeProvider.SleepAsync(interval, cancellationToken);
                try
                {
                    if (!await mount.IsSlewingAsync(cancellationToken))
                    {
                        return (SlewCompletion.Reached, $"Reached {name}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Slew completion poll failed for {Target}", name);
                    return (SlewCompletion.PollFailed,
                        $"Slew status check failed for {name}: {ex.Message}");
                }
            }
            return (SlewCompletion.TimedOut,
                $"Slew to {name} did not complete within {(timeout ?? TimeSpan.FromMinutes(5)).TotalMinutes:F0} min");
        }
    }
}
