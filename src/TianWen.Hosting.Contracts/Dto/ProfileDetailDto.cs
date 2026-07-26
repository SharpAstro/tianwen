using System;
using System.Collections.Immutable;
using TianWen.Lib.Devices;

namespace TianWen.Hosting.Dto;

/// <summary>
/// Profile detail DTO for the REST API. Exposes the profile's identity and equipment configuration.
/// </summary>
public sealed class ProfileDetailDto
{
    public required Guid ProfileId { get; init; }
    public required string Name { get; init; }
    public required ProfileEquipmentDto Equipment { get; init; }

    /// <summary>
    /// Observing site, null when the profile defers to the mount's own configured site.
    /// <para>
    /// <b>Site lives here rather than on the session state</b>, even though a remote client needs it to
    /// draw alt/az and the twilight strip. It is a property of the profile, not of a run, so putting it
    /// on both would create two sources that can disagree. Everything a client wants from it -- horizon
    /// coordinates for the current pointing, the astro-dark window for tonight -- is a pure function of
    /// (site, time, RA/Dec), so the client computes it exactly as the local GUI already does instead of
    /// the node shipping derived values that would need their own consistency rules.
    /// </para>
    /// </summary>
    public double? SiteLatitude { get; init; }

    /// <inheritdoc cref="SiteLatitude"/>
    public double? SiteLongitude { get; init; }

    /// <inheritdoc cref="SiteLatitude"/>
    public double? SiteElevation { get; init; }

    public static ProfileDetailDto FromProfile(Profile profile)
    {
        var data = profile.Data ?? ProfileData.Empty;
        return new ProfileDetailDto
        {
            ProfileId = profile.ProfileId,
            Name = profile.DisplayName,
            Equipment = ProfileEquipmentDto.FromData(data),
            SiteLatitude = data.SiteLatitude is { } lat ? JsonNumber.ForWire(lat) : null,
            SiteLongitude = data.SiteLongitude is { } lon ? JsonNumber.ForWire(lon) : null,
            SiteElevation = data.SiteElevation is { } elev ? JsonNumber.ForWire(elev) : null,
        };
    }
}

public sealed class ProfileEquipmentDto
{
    public required string Mount { get; init; }
    public required string Guider { get; init; }
    public string? GuiderCamera { get; init; }
    public string? Weather { get; init; }
    public int? GuiderFocalLength { get; init; }
    public required ImmutableArray<ProfileOtaDto> OTAs { get; init; }

    public static ProfileEquipmentDto FromData(ProfileData data)
    {
        var otas = ImmutableArray.CreateBuilder<ProfileOtaDto>(data.OTAs.Length);
        foreach (var ota in data.OTAs)
        {
            otas.Add(new ProfileOtaDto
            {
                Name = ota.Name,
                FocalLength = ota.FocalLength,
                Aperture = ota.Aperture,
                OpticalDesign = ota.OpticalDesign.ToString(),
                Camera = ota.Camera.ToString(),
                Focuser = ota.Focuser?.ToString(),
                FilterWheel = ota.FilterWheel?.ToString(),
                Cover = ota.Cover?.ToString(),
            });
        }

        return new ProfileEquipmentDto
        {
            Mount = data.Mount.ToString(),
            Guider = data.Guider.ToString(),
            GuiderCamera = data.GuiderCamera?.ToString(),
            Weather = data.Weather?.ToString(),
            GuiderFocalLength = data.GuiderFocalLength,
            OTAs = otas.MoveToImmutable(),
        };
    }
}

public sealed class ProfileOtaDto
{
    public required string Name { get; init; }
    public required int FocalLength { get; init; }
    public int? Aperture { get; init; }
    public required string OpticalDesign { get; init; }
    public required string Camera { get; init; }
    public string? Focuser { get; init; }
    public string? FilterWheel { get; init; }
    public string? Cover { get; init; }
}
