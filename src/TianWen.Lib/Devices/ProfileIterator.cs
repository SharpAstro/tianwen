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
    // What the newest discovery to finish found, with the generation it STARTED as. Discoveries overlap (the mount limit watcher
    // looks every 5 s while the node writes and clients ask), and the one that started first can finish last: published as it
    // finished, a listing taken before a profile was saved replaced the listing that has it, and the profile was gone until the
    // next look (#2000: AlpacaServerRoundTripTests' active profile served "No telescope with device number 0"). A result is
    // published only over an older one, so a stale listing never replaces a fresher one.
    private sealed record Published(long Generation, ConcurrentBag<Profile> Profiles);

    private Published _published = new Published(0, []);
    private long _started;

    /// <summary>Runs between a discovery's listing of the folder and its publishing, for a test to hold one there.</summary>
    internal Func<CancellationToken, Task>? AfterListing { get; set; }

    // The files the last discovery listed, so a listing that lacks one looks again (Profile.ListExistingProfilesAsync).
    private IReadOnlyCollection<string>? _lastListed;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Profile];

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var bag = new ConcurrentBag<Profile>();
        var generation = Interlocked.Increment(ref _started);

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

        if (AfterListing is { } afterListing)
        {
            await afterListing(cancellationToken);
        }

        var mine = new Published(generation, bag);
        var current = Volatile.Read(ref _published);
        while (current.Generation < generation)
        {
            var seen = Interlocked.CompareExchange(ref _published, mine, current);
            if (ReferenceEquals(seen, current))
            {
                break;
            }
            current = seen;
        }
    }

    public IEnumerable<Profile> RegisteredDevices(DeviceType deviceType)
        => deviceType == DeviceType.Profile ? Volatile.Read(ref _published).Profiles : [];
}