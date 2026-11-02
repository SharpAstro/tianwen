using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>What <see cref="ConnectProfileWriter.WriteAsync"/> found, so a caller can tell why nothing was written.</summary>
internal enum ConnectProfileOutcome
{
    /// <summary>The node has no active profile (or its file is gone): nothing to reconcile against.</summary>
    NoActiveProfile,

    /// <summary>The active profile does not name this device as its mount, or the device is not one that is written.</summary>
    NotInProfile,

    /// <summary>The device is the profile's own and there was nothing to change.</summary>
    NothingToDo,

    /// <summary>The mount's site or the profile was written, or a camera's sensor recorded.</summary>
    Written,
}

/// <summary>What a connect wrote into the active profile: the outcome and a short phrase for a job's last step.</summary>
internal readonly record struct ConnectProfileResult(ConnectProfileOutcome Outcome, string? Note = null);

/// <summary>
/// What the node writes into the active profile after a device connects, as the GUI's Equipment tab does (P3 part 2 of
/// docs/plans/hardware-in-the-server.md, #930), always through the node's one profile writer (<see cref="NodeProfiles"/>),
/// so each write is pushed. ONE routine for the device plane's connect and the crash journal's reconnect (#1965): the
/// rules are Lib's, one copy for the GUI and the node: <see cref="MountSiteExtensions"/> and <see cref="ProfileDataExtensions"/>.
/// </summary>
internal sealed class ConnectProfileWriter(IDeviceHub hub, IHostedSession hosted, NodeProfiles profiles, ILogger<ConnectProfileWriter> logger)
{
    /// <summary>
    /// After a connect: the active profile's mount has its site reconciled with the profile's (the mount-side half
    /// applied at once, the profile-side half written), and a camera in the active profile has its sensor geometry
    /// captured into its OTA.
    /// </summary>
    public async Task<ConnectProfileResult> WriteAsync(DeviceBase device, CancellationToken cancellationToken)
    {
        if (hosted.ActiveProfileId is not { } profileId || await profiles.ReadAsync(profileId, cancellationToken) is not { Profile.Data: { } data })
        {
            return new ConnectProfileResult(ConnectProfileOutcome.NoActiveProfile);
        }

        switch (device.DeviceType)
        {
            case DeviceType.Mount when !DeviceBase.SameDevice(data.Mount, device.DeviceUri):
                return new ConnectProfileResult(ConnectProfileOutcome.NotInProfile);

            case DeviceType.Mount when hub.TryGetConnectedDriver<IMountDriver>(device.DeviceUri, out var mount) && mount is not null:
                var outcome = await mount.ReconcileSiteWithProfileAsync(data, logger, cancellationToken);
                if (outcome.ProfileChanged && outcome.Data.Site is { } adopted)
                {
                    // The site alone, onto the profile as it is at the write: anything else changed since the read stays.
                    await profiles.UpdateAsync(profileId, readAt: null,
                        current => current.Data is { } now ? current.WithData(now.WithSite(adopted)) : current, cancellationToken);
                    return new ConnectProfileResult(ConnectProfileOutcome.Written, "the profile took the mount's site");
                }
                return outcome.MountPushed
                    ? new ConnectProfileResult(ConnectProfileOutcome.Written, "the mount took the profile's site")
                    : new ConnectProfileResult(ConnectProfileOutcome.NothingToDo);

            case DeviceType.Camera when hub.TryGetConnectedDriver<ICameraDriver>(device.DeviceUri, out var camera) && camera is not null:
                var write = await profiles.UpdateAsync(profileId, readAt: null,
                    current => current.Data is { } now && now.CaptureSensorSpecs(device.DeviceUri, camera) is { } captured ? current.WithData(captured) : current,
                    cancellationToken);
                return write.Outcome is ProfileWriteOutcome.Written
                    ? new ConnectProfileResult(ConnectProfileOutcome.Written, "its sensor recorded in the profile")
                    : new ConnectProfileResult(ConnectProfileOutcome.NothingToDo);

            default:
                return new ConnectProfileResult(ConnectProfileOutcome.NotInProfile);
        }
    }
}
