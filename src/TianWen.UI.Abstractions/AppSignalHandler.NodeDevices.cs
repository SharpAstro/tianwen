using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// This computer's rig through its node (P6 of docs/plans/hardware-in-the-server.md, #936): what the Equipment tab and the
    /// device actions share. A device action is a node job, run to its end here and noted as it ended; a profile edit is a
    /// write through the node's one profile writer, at the revision the profile was read at.
    /// </summary>
    public partial class AppSignalHandler
    {
        // One profile write at a time: two edits posted in one frame would otherwise both name the revision before either
        // was written, and the second would always be refused.
        private readonly SemaphoreSlim _profileWrites = new SemaphoreSlim(1, 1);

        /// <summary>How often an edit refused as stale is made again onto the profile as it is now, before giving up.</summary>
        internal const int ProfileWriteAttempts = 3;

        /// <summary>This computer's node, or null with a note saying why there is none to act through.</summary>
        private LocalNodeConnection? LocalNodeOrSay()
        {
            if (_appState.LocalNode is { } node)
            {
                return node;
            }
            Notify(NotificationSeverity.Warning, _appState.LocalNodeProblem is { } problem
                ? $"This computer's rig cannot be used: {problem}"
                : "This computer's node is still starting");
            return null;
        }

        /// <summary>
        /// Runs a device job the node was asked to start to its end, and reads the devices again so the row shows what it
        /// did. A refusal is the node's answer in its own words (a run holding the device, a cold camera); a failure and a
        /// cancel are noted. Answers the job as it succeeded, else null.
        /// </summary>
        private async Task<JobDto?> RunNodeJobAsync(LocalNodeConnection node, Task<NodeResult<JobDto>> starting, string what,
            CancellationToken cancellationToken)
        {
            var started = await starting.ConfigureAwait(false);
            if (started is not { IsSuccess: true, Value: { } job })
            {
                Notify(NotificationSeverity.Warning, started.Error ?? $"{what}: refused");
                return null;
            }

            var ended = await node.Client.UntilEndedAsync(job, _timeProvider, cancellationToken).ConfigureAwait(false);
            await node.RefreshDevicesNowAsync(cancellationToken).ConfigureAwait(false);
            switch (ended)
            {
                case { IsSuccess: true, Value: { State: JobState.Succeeded } done }:
                    return done;
                case { IsSuccess: true, Value: { State: JobState.Cancelled } }:
                    Notify(NotificationSeverity.Warning, $"{what}: cancelled");
                    return null;
                case { IsSuccess: true, Value: { } failed }:
                    Notify(NotificationSeverity.Error, $"{what} failed: {failed.Error}");
                    return null;
                default:
                    Notify(NotificationSeverity.Error, $"{what}: {ended.Error}");
                    return null;
            }
        }

        /// <summary>
        /// Writes an edit of this computer's active profile through its node (P3's one profile writer): shown at once, and
        /// written at the revision it was read at. When the node refuses it as made against a profile that has moved on (a
        /// 412: another client, or the node's own write after a connect or a discovery), the edit is made again onto the
        /// profile as it is now (<see cref="ProfileDataExtensions.RebasedOnto"/>), so neither change is lost; any other
        /// failure is said, and the profile read again, so the view does not show an edit that was never made.
        /// </summary>
        /// <param name="based">The profile the edit was made of: the active profile as the edit read it.</param>
        /// <param name="name">A new name, or null to keep the profile's.</param>
        private async Task WriteLocalProfileAsync(Profile based, ProfileData edited, string? name, CancellationToken cancellationToken)
        {
            if (LocalNodeOrSay() is not { } node)
            {
                return;
            }

            _appState.ActiveProfile = new Profile(based.ProfileId, name ?? based.DisplayName, edited);
            _appState.NeedsRedraw = true;

            await _profileWrites.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var madeOf = based.Data ?? ProfileData.Empty;
                var data = edited;
                var revision = node.ProfileRevision;
                for (var attempt = 0; attempt < ProfileWriteAttempts; attempt++)
                {
                    if (revision is null)
                    {
                        Notify(NotificationSeverity.Warning, "The profile has not been read from this computer's node yet; make the change again in a moment");
                        return;
                    }

                    var written = await node.Client.UpdateProfileAsync(based.ProfileId, data, revision, name, cancellationToken).ConfigureAwait(false);
                    if (written is { IsSuccess: true, Value: { } stored })
                    {
                        node.AdoptProfileWrite(stored);
                        return;
                    }
                    if (written.StatusCode != 412)
                    {
                        Notify(NotificationSeverity.Error, $"Could not save the profile: {written.Error}");
                        await node.RefreshProfileNowAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    // Moved on since the edit was read: made again onto the profile as the node holds it now.
                    var latest = await node.Client.GetProfileAsync(based.ProfileId, cancellationToken).ConfigureAwait(false);
                    if (latest is not { IsSuccess: true, Value: { Data: { } now, Revision: { } nowRevision } })
                    {
                        Notify(NotificationSeverity.Error, $"Could not read the profile again to save the change: {latest.Error}");
                        return;
                    }
                    _logger.LogInformation("Profile {ProfileId} moved on since the edit was read; making it again onto revision {Revision}",
                        based.ProfileId, nowRevision);
                    data = data.RebasedOnto(madeOf, now);
                    madeOf = now;
                    revision = nowRevision;
                }
                Notify(NotificationSeverity.Error, "Could not save the profile: it kept changing while the change was made");
                await node.RefreshProfileNowAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _profileWrites.Release();
            }
        }

        /// <summary>Reads this computer's profile again now, and does what a changed profile asks of the views.</summary>
        private async Task RefreshLocalProfileNowAsync(LocalNodeConnection node, CancellationToken cancellationToken)
        {
            var previous = _appState.ActiveProfile?.ProfileId;
            if (await node.RefreshProfileNowAsync(cancellationToken).ConfigureAwait(false))
            {
                await OnNodeProfileChangedAsync(node, previous, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// What a node's changed profile asks of the views: a replan of the view that shows it; and for this computer's, its
        /// session setup loaded again when it is another profile, the planner started if the profile has just been given its
        /// first site, and the session's camera settings laid out again.
        /// </summary>
        private async Task OnNodeProfileChangedAsync(NodeConnection connection, Guid? previousProfile, CancellationToken cancellationToken)
        {
            if (ReferenceEquals(_contexts.Active, connection.Context))
            {
                _plannerState.NeedsRecompute = true;
            }
            if (connection is LocalNodeConnection && _appState.ActiveProfile is { } profile)
            {
                if (_plannerState.ObjectDb is null && TransformFactory.FromProfile(profile, _timeProvider, out _) is { } transform)
                {
                    StartPlanner(transform, "Load catalog once the profile has a site");
                }
                if (profile.ProfileId != previousProfile)
                {
                    await LoadSessionConfigAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _sessionState.InitializeFromProfile(profile, _appState.CameraCapabilitiesOf);
                }
                _sessionState.NeedsRedraw = true;
            }
            _appState.NeedsRedraw = true;
        }

        /// <summary>
        /// The node's profiles for the profile switcher (<see cref="EquipmentTabState.AllProfiles"/>), by name. Kept as they
        /// were when the node cannot be read. An entry is a name and an id, with no data: the switch reads the profile
        /// chosen whole from the node, as it is then.
        /// </summary>
        private async Task RefreshProfileListAsync(LocalNodeConnection node, CancellationToken cancellationToken)
        {
            var listed = await node.Client.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
            if (listed is { IsSuccess: true, Value: { } profiles })
            {
                _eqState.AllProfiles = [.. profiles
                    .Select(static p => new Profile(p.ProfileId, p.Name, ProfileData.Empty))
                    .OrderBy(static p => p.DisplayName, StringComparer.OrdinalIgnoreCase)];
            }
            else
            {
                _logger.LogDebug("Could not list the profiles of {Node}: {Error}", LocalNodeName, listed.Error);
            }
        }

        /// <summary>
        /// The focuser of OTA <paramref name="otaIndex"/>, and this computer's node to move it through. Silent when the OTA has
        /// none (the jog is click-driven and self-explanatory); a panel of a rig on show acts on this computer's rig never.
        /// </summary>
        private bool TryResolveOtaFocuser(int otaIndex, [NotNullWhen(true)] out LocalNodeConnection? node, [NotNullWhen(true)] out Uri? focuserUri)
        {
            node = null;
            focuserUri = null;
            // The planetary panel's jog, beside its mount nudges, reaches here from a remote view too.
            if (!EnsureLocalContext("A focuser move")) return false;
            if (_appState.ActiveProfile?.Data is not { OTAs: var otas } || otaIndex >= otas.Length) return false;
            if (otas[otaIndex].Focuser is not { } focuser || focuser == NoneDevice.Instance.DeviceUri) return false;
            if (LocalNodeOrSay() is not { } local) return false;
            node = local;
            focuserUri = focuser;
            return true;
        }

        /// <summary>
        /// The node's solution of OTA <paramref name="otaIndex"/>'s frame, shown on this computer's view and said: from a
        /// solve, or a solve and sync, whose frame is the one the view shows.
        /// </summary>
        private async Task ShowSolutionAsync(LocalNodeConnection node, int otaIndex, CancellationToken cancellationToken)
        {
            var solution = await node.Client.GetSolutionAsync(otaIndex, cancellationToken).ConfigureAwait(false);
            if (solution is not { IsSuccess: true, Value: { } solved })
            {
                return;
            }
            LocalLiveSession.PreviewPlateSolveResult = new PlateSolveResult(solved.Solution?.ToWcs(), TimeSpan.FromSeconds(solved.ElapsedSeconds));
            LocalLiveSession.NeedsRedraw = true;
            Notify(solved.Solved ? NotificationSeverity.Info : NotificationSeverity.Warning, solved.Message);
        }

        /// <summary>
        /// After a slot of the profile was given another device: the device it had is disconnected when it is still
        /// connected and safe to (no run holds it, a camera's cooler off and idle), and otherwise left connected and said
        /// so, since a cold camera is never yanked without its warm-up.
        /// </summary>
        private async Task DisconnectOrphanAsync(LocalNodeConnection node, Uri? previous, Uri assigned, DeviceType type,
            CancellationToken cancellationToken)
        {
            if (previous is null || DeviceBase.SameDevice(previous, assigned) || !node.IsConnected(previous))
            {
                return;
            }

            var check = await node.Client.GetDisconnectSafetyAsync(previous, cancellationToken).ConfigureAwait(false);
            if (check is not { IsSuccess: true, Value: { } safety })
            {
                return;
            }
            if (safety.LeaseOwner is { } owner)
            {
                Notify(NotificationSeverity.Warning, $"Previous {type} left connected: {owner} is using it");
                return;
            }
            if (safety.Safety is not DisconnectSafety.Safe)
            {
                Notify(NotificationSeverity.Warning, $"Previous {type} left connected ({safety.Safety}). Click Off on its row to warm up.");
                return;
            }
            if (await RunNodeJobAsync(node, node.Client.DisconnectDeviceAsync(previous, skipWarmUp: false, cancellationToken),
                    $"Disconnecting the previous {type}", cancellationToken).ConfigureAwait(false) is not null)
            {
                Notify(NotificationSeverity.Info, $"Previous {type} disconnected");
            }
        }
    }
}
