using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
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
    /// Commits one of a device's settings, as the Equipment tab's text field does (P3 part 4, #930), by the rule the GUI
    /// applies (<see cref="DeviceSettingHelper.Commit"/>): a masked setting goes into the node's credential store, keyed by
    /// device and never echoed back; any other goes onto the device's URI, written into the named profile through the one
    /// profile writer.
    /// </summary>
    public async Task<ResponseEnvelope<DeviceSettingDto>> CommitSettingAsync(DeviceSettingRequestDto request, CancellationToken cancellationToken)
    {
        if (!TryParse(request.DeviceUri, out var uri, out var refused))
        {
            return refused.Value.As<DeviceSettingDto>();
        }
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return ResponseEnvelope<DeviceSettingDto>.Fail("Name the setting's key");
        }

        var device = hub.TryGetDeviceFromUri(uri, out var known) ? known : null;
        var commit = DeviceSettingHelper.Commit(device, uri, request.Key, request.Value, credentials);
        if (commit is not { Kind: DeviceSettingCommitKind.UriParam, NewUri: { } newUri })
        {
            return ResponseEnvelope<DeviceSettingDto>.Ok(new DeviceSettingDto { Secret = true, DeviceUri = uri.ToString() });
        }
        if (request.ProfileId is not { } profileId)
        {
            return ResponseEnvelope<DeviceSettingDto>.Ok(new DeviceSettingDto { Secret = false, DeviceUri = newUri.ToString() });
        }

        var write = await profiles.UpdateAsync(profileId, readAt: null,
            current => current.Data is { } data ? current.WithData(data.ReplaceDeviceUri(uri, newUri)) : current, cancellationToken);
        return write.Stored is { } stored
            ? ResponseEnvelope<DeviceSettingDto>.Ok(new DeviceSettingDto { Secret = false, DeviceUri = newUri.ToString(), Revision = stored.Revision })
            : ResponseEnvelope<DeviceSettingDto>.NotFound($"Profile {profileId} not found");
    }

    /// <summary>Whether the device's masked setting <paramref name="key"/> has a value in the node's credential store.</summary>
    public ResponseEnvelope<DeviceSecretDto> SecretIsSet(string deviceUri, string key)
    {
        if (!TryParse(deviceUri, out var uri, out var refused))
        {
            return refused.Value.As<DeviceSecretDto>();
        }
        if (!hub.TryGetDeviceFromUri(uri, out var device) || !DeviceSettingHelper.IsSecret(device, key))
        {
            return ResponseEnvelope<DeviceSecretDto>.NotFound($"{key} is not a masked setting of {uri}");
        }
        return ResponseEnvelope<DeviceSecretDto>.Ok(new DeviceSecretDto { IsSet = credentials.Get(ICredentialStore.KeyFor(device.DeviceId, key)) is { Length: > 0 } });
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
