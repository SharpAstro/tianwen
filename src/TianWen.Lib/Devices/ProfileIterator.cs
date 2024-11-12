using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

internal class ProfileIterator(IExternal external) : IDeviceSource<Profile>
{
    private ConcurrentBag<Profile> _profiles = [];

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Profile];

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var bag = new ConcurrentBag<Profile>();

        await Parallel.ForEachAsync(Profile.ListExistingProfiles(external.ProfileFolder), cancellationToken, async (info, cancellationToken) =>
        {
            var profileId = info.profileId;
            var file = info.file;
            try
            {
                using var stream = info.file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                if (await JsonSerializer.DeserializeAsync<ProfileDto>(stream, cancellationToken: cancellationToken) is { } dto)
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
                        bag.Add(new Profile(dto.ProfileId, dto.Name, dto.Data));
                    }
                }
                else
                {
                    external.AppLogger.LogWarning("Skipping invalid profile {ProfileId} in file {File}", profileId, file);
                }
            }
            catch (Exception ex)
            {
                external.AppLogger.LogError(ex, "Failed to load profile {ProfileId} in file {File}", profileId, file);
            }
        });

        Interlocked.Exchange(ref _profiles, bag);
    }

    public IEnumerable<Profile> RegisteredDevices(DeviceType deviceType) => deviceType == DeviceType.Profile ? _profiles : [];
}