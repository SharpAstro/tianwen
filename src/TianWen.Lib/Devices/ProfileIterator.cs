using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.IO;

namespace TianWen.Lib.Devices;

internal class ProfileIterator(IExternal external, ILogger<ProfileIterator> logger) : IDeviceSource<Profile>
{
    private ConcurrentBag<Profile> _profiles = [];

    // The files the last discovery listed, so a listing that lacks one looks again (Profile.ListExistingProfilesAsync).
    private IReadOnlyCollection<string>? _lastListed;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Profile];

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var bag = new ConcurrentBag<Profile>();

        var listed = await Profile.ListExistingProfilesAsync(external.ProfileFolder, Volatile.Read(ref _lastListed), cancellationToken);
        Volatile.Write(ref _lastListed, [.. listed.Select(static profile => profile.file.FullName)]);

        await Parallel.ForEachAsync(listed, cancellationToken, async (info, cancellationToken) =>
        {
            var profileId = info.profileId;
            var file = info.file;
            try
            {
                // Shared, since another process may be replacing the file right now (SharedFile).
                await using var stream = await SharedFile.OpenReadAsync(info.file.FullName, cancellationToken);
                if (await JsonSerializer.DeserializeAsync(stream, Profile.ProfileJsonSerializerContextIndented.ProfileDto, cancellationToken: cancellationToken) is { } dto)
                {
                    if (string.IsNullOrWhiteSpace(dto.Name))
                    {
                        logger.LogWarning("Skipping profile {ProfileId} in file {File} as it does not have a name", profileId, file);
                    }
                    else if (dto.ProfileId != profileId)
                    {
                        logger.LogWarning("Skipping profile {ProfileId} ({Name}) in file {File} as it does not match stored id {DtoProfileId}", profileId, dto.Name, file, dto.ProfileId);
                    }
                    else if (dto.ProfileId == Guid.Empty)
                    {
                        logger.LogWarning("Skipping profile {ProfileId} ({Name}) in file {File} has an invalid uuid", profileId, dto.Name, file);
                    }
                    else
                    {
                        bag.Add(new Profile(dto.ProfileId, dto.Name, dto.Data));
                    }
                }
                else
                {
                    logger.LogWarning("Skipping invalid profile {ProfileId} in file {File}", profileId, file);
                }
            }
            catch (FileNotFoundException)
            {
                // Deleted since it was listed; the next discovery does not list it.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load profile {ProfileId} in file {File}", profileId, file);
            }
        });

        Interlocked.Exchange(ref _profiles, bag);
    }

    public IEnumerable<Profile> RegisteredDevices(DeviceType deviceType) => deviceType == DeviceType.Profile ? _profiles : [];
}