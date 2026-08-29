using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Devices;

internal record ProfileDto(Guid ProfileId, string Name, ProfileData Data);

/// <summary>
/// Tie-breaker for reconciling site coordinates between the mount hardware and
/// the stored profile when both report a value on mount connect.
/// </summary>
public enum SiteTieBreaker
{
    /// <summary>Mount wins when both are set; profile is updated from the mount.</summary>
    Mount = 0,
    /// <summary>Profile wins when both are set; mount is updated (if it supports writes).</summary>
    Profile = 1,
}

public readonly record struct ProfileData(
    Uri Mount,
    Uri Guider,
    ImmutableArray<OTAData> OTAs,
    Uri? GuiderCamera = null,
    Uri? GuiderFocuser = null,
    int? OAG_OTA_Index = null,
    int? GuiderFocalLength = null,
    Uri? Weather = null,
    double? SiteLatitude = null,
    double? SiteLongitude = null,
    double? SiteElevation = null,
    SiteTieBreaker SiteTieBreaker = SiteTieBreaker.Mount,

    /// <summary>
    /// Mechanical safety limits for this rig, or null for the shipped defaults (disabled).
    /// </summary>
    /// <remarks>
    /// <para><b>On the PROFILE, not <c>SessionConfiguration</c>, and the distinction is not
    /// bookkeeping.</b> Where the tube meets the pier is a static fact about this rig -- the mount's
    /// geometry AND the tube bolted to it -- exactly like <see cref="OTAData"/>, so it does not vary
    /// run to run, and it has to apply to a manual slew with no session in sight. A per-run home
    /// would give the right answer for the imaging loop and no answer at all for the case where
    /// somebody drives the mount by hand at 2am.</para>
    ///
    /// <para><b>It is the TUBE that collides, not the counterweight.</b> Tracking past the meridian
    /// on a GEM swings the counterweight UP, above the OTA, and the tube DOWN toward the pier and
    /// tripod -- so the margin is set by the optics, not the ballast: a long refractor or Newtonian
    /// runs out of room far sooner than a short lens. It also varies with DECLINATION, since a tube
    /// near the pole lies along the RA axis and barely sweeps while one near the equator sweeps the
    /// widest arc. A single hour-angle threshold is therefore a conservative APPROXIMATION of a
    /// three-variable envelope, and must be set for the worst case the rig actually images -- the
    /// lowest declination, with the longest tube and whatever hangs off the back of it.</para>
    ///
    /// <para>Null rather than a default instance so an older profile deserialises unchanged and
    /// reads as "never configured", which the shipped defaults answer as disabled. Opt-in is the
    /// point: a limit that fires on a rig nobody measured is worse than no limit.</para>
    /// </remarks>
    Sequencing.MountLimitConfiguration? MountLimits = null
)
{
    public static readonly ProfileData Empty = new ProfileData(NoneDevice.Instance.DeviceUri, NoneDevice.Instance.DeviceUri, []);
}

public readonly record struct OTAData(
    string Name,
    int FocalLength,
    Uri Camera,
    Uri? Cover,
    Uri? Focuser,
    Uri? FilterWheel,
    bool? PreferOutwardFocus,
    bool? OutwardIsPositive,
    int? Aperture = null,
    OpticalDesign OpticalDesign = OpticalDesign.Unknown,
    // Camera sensor geometry, auto-captured the first time the camera connects (see
    // EquipmentActions.CaptureSensorSpecs). Persisted here -- NOT on the camera URI, which the
    // discovery reconcile can replace -- so the planner can compute the sensor FOV (and therefore
    // smart framing groups) offline, before any device is connected. Null until first capture.
    double? CameraPixelSizeUm = null,
    int? CameraSensorWidthPx = null,
    int? CameraSensorHeightPx = null
);