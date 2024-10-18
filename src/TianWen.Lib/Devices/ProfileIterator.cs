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
                    external.LogWarning($"Skipping invalid profile {profileId} in file {file}");
                    profile = null;
                }
            }
            catch (Exception ex)
            {
                external.LogException(ex, $"Failed to load profile {profileId} in file {file}");
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
            external.LogWarning($"Skipping profile {profileId} in file {file} as it does not have a name");
        }
        else if (dto.ProfileId != profileId)
        {
            external.LogWarning($"Skipping profile {profileId} ({dto.Name}) in file {file} as it does not match stored id {dto.ProfileId}");
        }
        else if (dto.ProfileId == Guid.Empty)
        {
            external.LogWarning($"Skipping profile {profileId} ({dto.Name}) in file {file} has an invalid uuid");
        }
        else
        {
            return new Profile(dto.ProfileId, dto.Name, dto.Values);
        }

        return null;
    }
}