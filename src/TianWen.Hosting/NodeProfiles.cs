using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>What became of a write through <see cref="NodeProfiles"/>.</summary>
internal enum ProfileWriteOutcome
{
    /// <summary>Written; <see cref="ProfileWrite.Stored"/> is what the file holds now.</summary>
    Written,

    /// <summary>The change would store what the file holds already, so nothing was written or pushed.</summary>
    Unchanged,

    /// <summary>The file has moved past the revision the change was made against; <see cref="ProfileWrite.Stored"/> is what it holds.</summary>
    Stale,

    /// <summary>No such profile.</summary>
    NotFound,
}

/// <summary>
/// A write's outcome, the profile as stored after it (null when there is none), and as it was stored before (null when
/// there was none to change).
/// </summary>
internal readonly record struct ProfileWrite(ProfileWriteOutcome Outcome, StoredProfile? Stored, StoredProfile? Previous = null);

/// <summary>
/// The node's ONE profile writer (P3 part 1 of docs/plans/hardware-in-the-server.md, #930). Every profile the node
/// writes goes through here, whoever asked for it: an edit over the socket, and the node's own writes (reconcile,
/// sensor capture, the backlash mirror). Each write pushes <c>PROFILE-CHANGED</c>.
/// </summary>
/// <remarks>
/// <para><b>A write reads the file as it is NOW, inside the writer's lock, never a cached copy.</b> Until P6 the GUI, the
/// TUI and the CLI still write profile files themselves, so the node's profile registry can be behind the disk.</para>
/// <para><b>An edit names the revision it was read at</b> (<see cref="StoredProfile.Revision"/>, the hash of the file's
/// bytes), and one the file has moved past is refused rather than overwriting a change its sender never saw. A hash, not
/// a counter, so it keeps no state: it holds across a restart, and another process's write moves it too.</para>
/// <para>The lock orders this node's own writers. Against another process's write, the revision protects an edit and
/// narrows the window to the instant between the node's read and its write; that window closes at P6, when no other
/// process writes a profile.</para>
/// </remarks>
internal sealed class NodeProfiles(IExternal external, IDeviceDiscovery discovery, EventHub events, ILogger<NodeProfiles> logger)
{
    private readonly SemaphoreSlim _writing = new SemaphoreSlim(1, 1);

    /// <summary>The profile as its file holds it now, with its revision; null when there is none.</summary>
    public Task<StoredProfile?> ReadAsync(Guid profileId, CancellationToken cancellationToken)
        => Profile.TryReadStoredAsync(external, profileId, cancellationToken);

    /// <summary>
    /// Changes the profile saved as <paramref name="profileId"/>: reads it, applies <paramref name="change"/>, writes the
    /// result and pushes it, all under the writer's lock.
    /// </summary>
    /// <param name="readAt">The revision the change was made against: refused (<see cref="ProfileWriteOutcome.Stale"/>)
    /// when the file has moved past it. Null for the node's own writes, which read the profile under the lock.</param>
    public async Task<ProfileWrite> UpdateAsync(Guid profileId, string? readAt, Func<Profile, Profile> change, CancellationToken cancellationToken)
    {
        await _writing.WaitAsync(cancellationToken);
        try
        {
            if (await Profile.TryReadStoredAsync(external, profileId, cancellationToken) is not { } stored)
            {
                return new ProfileWrite(ProfileWriteOutcome.NotFound, null);
            }
            if (readAt is not null && readAt != stored.Revision)
            {
                return new ProfileWrite(ProfileWriteOutcome.Stale, stored);
            }

            var changed = change(stored.Profile);
            if (changed.ComputeRevision() == stored.Revision)
            {
                return new ProfileWrite(ProfileWriteOutcome.Unchanged, stored);
            }

            var written = await changed.SaveStoredAsync(external, cancellationToken);
            await AnnounceAsync(new ProfileChangedDto { ProfileId = profileId, Name = changed.DisplayName, Revision = written.Revision });
            return new ProfileWrite(ProfileWriteOutcome.Written, written, stored);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>
    /// Applies a discovery's rule to every stored profile (<see cref="DeviceDiscoveryExtensions.ReconcileStoredProfile"/>:
    /// device URIs that drifted, a site still on the mount's URI) and logs what moved, as the GUI's reconcile-all does at
    /// the end of its discovery. Run at the end of the node's discovery job (P3 part 2, #930). Answers how many profiles
    /// it wrote; one that is in sync is not written.
    /// </summary>
    public async Task<int> ReconcileAllAsync(CancellationToken cancellationToken)
    {
        var written = 0;
        foreach (var registered in discovery.RegisteredDevices(DeviceType.Profile).OfType<Profile>().ToList())
        {
            var write = await UpdateAsync(registered.ProfileId, readAt: null, current =>
                current.Data is { } data && discovery.ReconcileStoredProfile(data) is (var reconciled, true) ? current.WithData(reconciled) : current,
                cancellationToken);
            if (write is { Outcome: ProfileWriteOutcome.Written, Previous.Profile.Data: { } before, Stored.Profile.Data: { } after })
            {
                written++;
                foreach (var (field, from, to) in before.DiffTo(after))
                {
                    logger.LogInformation("Reconcile {Profile} {Field}: {Before} -> {After}", registered.DisplayName, field, from, to);
                }
            }
        }
        return written;
    }

    /// <summary>Creates an empty profile named <paramref name="name"/>.</summary>
    public async Task<StoredProfile> CreateAsync(string name, CancellationToken cancellationToken)
    {
        await _writing.WaitAsync(cancellationToken);
        try
        {
            var written = await new Profile(Guid.NewGuid(), name, ProfileData.Empty).SaveStoredAsync(external, cancellationToken);
            await AnnounceAsync(new ProfileChangedDto { ProfileId = written.Profile.ProfileId, Name = name, Revision = written.Revision });
            return written;
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>Deletes the profile saved as <paramref name="profileId"/>; false when there is none.</summary>
    public async Task<bool> DeleteAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await _writing.WaitAsync(cancellationToken);
        try
        {
            if (await Profile.TryReadStoredAsync(external, profileId, cancellationToken) is not { } stored)
            {
                return false;
            }

            stored.Profile.Delete(external);
            await AnnounceAsync(new ProfileChangedDto { ProfileId = profileId, Deleted = true });
            return true;
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>
    /// After a write: the registry re-read, so a listing has it, and the change pushed. Both on the node's own terms, not
    /// the request's: the file has changed, so a client that gave up waiting must still be told.
    /// </summary>
    private async Task AnnounceAsync(ProfileChangedDto change)
    {
        logger.LogInformation("Profile {ProfileId} {What}", change.ProfileId,
            change.Deleted ? "deleted" : $"written as '{change.Name}' at {change.Revision}");
        await discovery.DiscoverOnlyDeviceType(DeviceType.Profile, CancellationToken.None);
        events.Broadcast(BroadcastEvents.ProfileChanged(change));
    }
}
