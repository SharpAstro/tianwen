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

    public static ProfileDetailDto FromProfile(Profile profile)
    {
        var data = profile.Data ?? ProfileData.Empty;
        return new ProfileDetailDto
        {
            ProfileId = profile.ProfileId,
            Name = profile.DisplayName,
            Equipment = ProfileEquipmentDto.FromData(data),
        };
    }
}

public sealed class ProfileEquipmentDto
{
    public required string Mount { get; init; }
    public required string Guider { get; init; }
    public required string? GuiderCamera { get; init; }
    public required string? Weather { get; init; }
    public required int? GuiderFocalLength { get; init; }
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
    public required int? Aperture { get; init; }
    public required string OpticalDesign { get; init; }
    public required string Camera { get; init; }
    public required string? Focuser { get; init; }
    public required string? FilterWheel { get; init; }
    public required string? Cover { get; init; }
}
