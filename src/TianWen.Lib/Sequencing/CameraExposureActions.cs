using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Shared camera-exposure plumbing used by the imaging session, polar
/// alignment, and the live preview button. Three call sites that all need
/// the same denorm contract: telescope name, focal length / aperture, site
/// lat/lon, focuser position, active filter, and (for FakeCameraDriver) the
/// catalog database that drives synthetic-frame star rendering. Without a
/// single source of truth the paths drift -- the polar-alignment near-pole
/// solve failures and the live-preview "fake camera renders random stars"
/// regression are both symptoms of that drift.
/// </summary>
public static class CameraExposureActions
{
    /// <summary>
    /// Stamps OTA-derived denorm fields onto <paramref name="camera"/> so the
    /// next FITS frame written by the camera driver carries accurate header
    /// values. Idempotent -- safe to call before every exposure.
    /// <list type="bullet">
    ///   <item><description>Static optics fields (Telescope, FocalLength,
    ///     Aperture, Latitude, Longitude) use null-coalescing semantics: only
    ///     written if the camera has no value yet, so explicit overrides
    ///     elsewhere in the codebase are preserved.</description></item>
    ///   <item><description>Per-exposure fields (FocusPosition, Filter,
    ///     Target) are unconditionally refreshed from the connected
    ///     focuser / filter wheel / mount so the FITS header reflects state
    ///     at exposure time.</description></item>
    ///   <item><description>FakeCameraDriver gets <paramref name="catalogDb"/>
    ///     wired so synthetic frames render real Tycho-2 stars (required for
    ///     plate solvers to match against the rendered output).</description></item>
    /// </list>
    /// All reads are best-effort: failures are logged at Debug and -1 / Unknown
    /// fallback values are written, matching <c>Session.Imaging.cs</c>'s
    /// historical behaviour.
    /// </summary>
    /// <param name="camera">Connected camera driver to stamp.</param>
    /// <param name="otaName">OTA / telescope name (FITS: TELESCOP).</param>
    /// <param name="focalLengthMm">OTA focal length in mm (FITS: FOCALLEN).</param>
    /// <param name="apertureMm">OTA aperture in mm (FITS: APTDIA). Pass null
    ///   if the OTA has no aperture configured.</param>
    /// <param name="focuser">Connected focuser, or null. Skipped when null
    ///   or disconnected; FocusPosition is left untouched in that case.</param>
    /// <param name="filterWheel">Connected filter wheel, or null.</param>
    /// <param name="mount">Connected mount, or null. When supplied, drives
    ///   site lat/lon (one-shot) and -- if <paramref name="targetName"/> is
    ///   set -- the per-exposure Target refresh from current pointing.</param>
    /// <param name="targetName">Target name to write into Camera.Target,
    ///   alongside the mount's RA/Dec. Pass null to leave Target alone --
    ///   the imaging session does this because it manages Target via
    ///   scheduled observations rather than current pointing.</param>
    /// <param name="catalogDb">Celestial-object DB for FakeCameraDriver
    ///   synthetic-star rendering. Pass null on real-camera-only setups.</param>
    /// <param name="logger">Logger for debug-level read failures.</param>
    /// <param name="ct">Cancellation.</param>
    public static async ValueTask StampDenormAsync(
        ICameraDriver camera,
        string otaName,
        int focalLengthMm,
        int? apertureMm,
        IFocuserDriver? focuser = null,
        IFilterWheelDriver? filterWheel = null,
        IMountDriver? mount = null,
        string? targetName = null,
        ICelestialObjectDB? catalogDb = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        // Static optics -- only stamp if unset so explicit overrides survive.
        if (camera.FocalLength <= 0 && focalLengthMm > 0)
        {
            camera.FocalLength = focalLengthMm;
        }
        if (camera.Aperture is null or <= 0 && apertureMm is int a and > 0)
        {
            camera.Aperture = a;
        }
        camera.Telescope ??= otaName;

        // Site lat/lon from mount -- one-shot.
        if (mount is not null && (camera.Latitude is null || camera.Longitude is null))
        {
            try
            {
                if (camera.Latitude is null)
                {
                    camera.Latitude = await mount.GetSiteLatitudeAsync(ct).ConfigureAwait(false);
                }
                if (camera.Longitude is null)
                {
                    camera.Longitude = await mount.GetSiteLongitudeAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "StampDenorm: site lat/lon read failed");
            }
        }

        // Per-exposure: focuser position.
        if (focuser is { Connected: true })
        {
            try
            {
                camera.FocusPosition = await focuser.GetPositionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "StampDenorm: focuser GetPosition failed");
                camera.FocusPosition = -1;
            }
        }

        // Per-exposure: active filter.
        if (filterWheel is { Connected: true })
        {
            try
            {
                camera.Filter = (await filterWheel.GetCurrentFilterAsync(ct).ConfigureAwait(false)).Filter;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "StampDenorm: filter wheel read failed");
                camera.Filter = Filter.Unknown;
            }
        }

        // Per-exposure: refresh Target from mount pointing (only when caller
        // supplies a target name -- imaging session manages Target via
        // scheduling and must not have it stomped here).
        if (mount is not null && targetName is not null)
        {
            try
            {
                var ra = await mount.GetRightAscensionAsync(ct).ConfigureAwait(false);
                var dec = await mount.GetDeclinationAsync(ct).ConfigureAwait(false);
                camera.Target = new Target(ra, dec, targetName, null);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "StampDenorm: mount RA/Dec read failed");
            }
        }

        // FakeCameraDriver: wire catalog DB once so synthetic-star rendering
        // can project real Tycho-2 stars at the current Target.
        if (camera is FakeCameraDriver fake && catalogDb is not null)
        {
            fake.CelestialObjectDB ??= catalogDb;
        }
    }
}
