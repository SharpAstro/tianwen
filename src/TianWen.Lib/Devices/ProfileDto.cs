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
    SiteTieBreaker SiteTieBreaker = SiteTieBreaker.Mount
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
    OpticalDesign OpticalDesign = OpticalDesign.Unknown
);