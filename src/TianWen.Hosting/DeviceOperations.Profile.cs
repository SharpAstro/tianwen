using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>
/// What the device plane writes into the active profile, as the GUI's Equipment tab does (P3 part 2 of
/// docs/plans/hardware-in-the-server.md, #930), always through the node's one profile writer (<see cref="NodeProfiles"/>),
/// so each write is pushed. The rules are Lib's, one copy for the GUI and the node: <see cref="MountSiteExtensions"/> and
/// <see cref="ProfileDataExtensions"/>.
/// </summary>
internal sealed partial class DeviceOperations
{
    /// <summary>
    /// After a connect: the active profile's mount has its site reconciled with the profile's (the mount-side half
    /// applied at once, the profile-side half written), and a camera in the active profile has its sensor geometry
    /// captured into its OTA. Answers what it did, for the job's last step, or null when it did nothing.
    /// </summary>
    private async Task<string?> WriteConnectIntoProfileAsync(DeviceBase device, CancellationToken cancellationToken)
    {
        if (hosted.ActiveProfileId is not { } profileId || await profiles.ReadAsync(profileId, cancellationToken) is not { Profile.Data: { } data })
        {
            return null;
        }

        switch (device.DeviceType)
        {
            case DeviceType.Mount when DeviceBase.SameDevice(data.Mount, device.DeviceUri)
                && hub.TryGetConnectedDriver<IMountDriver>(device.DeviceUri, out var mount) && mount is not null:
                var outcome = await mount.ReconcileSiteWithProfileAsync(data, logger, cancellationToken);
                if (outcome.ProfileChanged && outcome.Data.Site is { } adopted)
                {
                    // The site alone, onto the profile as it is at the write: anything else changed since the read stays.
                    await profiles.UpdateAsync(profileId, readAt: null,
                        current => current.Data is { } now ? current.WithData(now.WithSite(adopted)) : current, cancellationToken);
                    return "the profile took the mount's site";
                }
                return outcome.MountPushed ? "the mount took the profile's site" : null;

            case DeviceType.Camera when hub.TryGetConnectedDriver<ICameraDriver>(device.DeviceUri, out var camera) && camera is not null:
                var write = await profiles.UpdateAsync(profileId, readAt: null,
                    current => current.Data is { } now && now.CaptureSensorSpecs(device.DeviceUri, camera) is { } captured ? current.WithData(captured) : current,
                    cancellationToken);
                return write.Outcome is ProfileWriteOutcome.Written ? "its sensor recorded in the profile" : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// After an edit of a profile over the socket: when it is the active profile and its site changed, its connected mount
    /// is given the new site if the profile wins the tie, as the GUI's site edit does
    /// (<see cref="MountSiteExtensions.PushSiteToMountIfProfileWinsAsync"/>, never to a mount a run holds).
    /// </summary>
    public async Task AfterProfileEditAsync(Guid profileId, ProfileWrite write, CancellationToken cancellationToken)
    {
        if (hosted.ActiveProfileId == profileId
            && write is { Outcome: ProfileWriteOutcome.Written, Stored.Profile.Data: { } data }
            && data.Site != write.Previous?.Profile.Data?.Site)
        {
            await hub.PushSiteToMountIfProfileWinsAsync(data, logger, cancellationToken);
        }
    }
}
