using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TianWen.Lib.Devices;

internal class ProfileIterator(IExternal external) : IDeviceSource<Profile>
{
    public bool IsSupported => true;

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Profile];

    public IEnumerable<Profile> RegisteredDevices(DeviceType deviceType)
    {
        foreach (var (profileId, file) in Profile.ListExistingProfiles(external.ProfileFolder))
        {
            Profile? profile;
            try
            {
                using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                if (JsonSerializer.Deserialize<ProfileDto>(stream) is { } profileDto)
                {
                    profile = ValidateStoredProfile(profileDto, profileId, file.FullName);
                }
                else
                {
                    external.AppLogger.LogWarning("Skipping invalid profile {ProfileId} in file {File}", profileId, file);
                    profile = null;
                }
            }
            catch (Exception ex)
            {
                external.AppLogger.LogError(ex, "Failed to load profile {ProfileId} in file {File}", profileId, file);
                profile = null;
            }

            if (profile is not null)
            {
                yield return profile;
            }
        }
    }

    private Profile? ValidateStoredProfile(ProfileDto dto, Guid profileId, string file)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            external.AppLogger.LogWarning("Skipping profile {ProfileId} in file {File} as it does not have a name", profileId, file);
        }
        else if (dto.ProfileId != profileId)
        {
            external.AppLogger.LogWarning("Skipping profile {ProfileId} ({Name}) in file {File} as it does not match stored id {DtoProfileId}", profileId, dto.Name, file, dto.ProfileId);
        }
        else if (dto.ProfileId == Guid.Empty)
        {
            external.AppLogger.LogWarning("Skipping profile {ProfileId} ({Name}) in file {File} has an invalid uuid", profileId, dto.Name, file);
        }
        else
        {
            return new Profile(dto.ProfileId, dto.Name, dto.Values);
        }

        return null;
    }
}