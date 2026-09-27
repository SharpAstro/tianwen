using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions
{
    public partial class AppSignalHandler
    {
        // 1 once a planner start has been made, by the host's first profile or by a site edit that unblocked it: one per app.
        private int _plannerStarted;

        /// <summary>
        /// Finds this computer's node, or starts one, and connects the local view to it (P6 of
        /// docs/plans/hardware-in-the-server.md, #936): from here the node holds this computer's devices and runs its sessions,
        /// and the app reads them from it as it reads any rig's (<see cref="LocalNodeConnection"/>). The node's active profile
        /// becomes the app's; <paramref name="requestedProfile"/> (a name or an id, a host's <c>--active</c>) is made the
        /// node's first, and with none requested and none active, the only profile there is. Then the planner starts on the
        /// profile's site and its session setup is loaded, which the hosts did from a profile they found themselves before
        /// the node held it. For the host to run in the background at start: a node can take seconds to come up, and the
        /// window says so meanwhile rather than waiting to open.
        /// </summary>
        public async Task ConnectLocalNodeAsync(LocalNodeOptions options, string? requestedProfile, CancellationToken cancellationToken)
        {
            var (connection, message) = await LocalNodeConnection.FindOrStartAsync(
                _contexts, _appState, options, _timeProvider, _logger, cancellationToken).ConfigureAwait(false);
            if (connection is null)
            {
                _appState.LocalNodeProblem = message;
                Notify(NotificationSeverity.Error, $"This computer's rig cannot be used: {message}");
                return;
            }

            _appState.LocalNode = connection;
            if (connection.Outcome is LocalNodeOutcome.StartedWithTheClient)
            {
                // The launcher's words say why; what they mean for a night is worth saying once.
                Notify(NotificationSeverity.Warning, $"{message}. A session this window starts ends with it.");
            }

            await ChooseLocalProfileAsync(connection.Client, requestedProfile, cancellationToken).ConfigureAwait(false);
            await connection.MaybeRefreshProfileAsync(cancellationToken).ConfigureAwait(false);
            await MigrateLegacySiteAsync(connection, cancellationToken).ConfigureAwait(false);

            if (_appState.ActiveProfile is not { } profile)
            {
                // No profile yet: the Equipment tab's first screen, which creates one, is what the user sees.
                _appState.NeedsRedraw = true;
                return;
            }

            if (TransformFactory.FromProfile(profile, _timeProvider, out _) is { } transform)
            {
                ApplySiteFromTransform(_plannerState, transform);
                StartPlanner(transform, "Compute tonight's best targets");
            }
            else
            {
                Notify(NotificationSeverity.Warning, "Set site coordinates in Equipment tab");
            }
            await LoadSessionConfigAsync(cancellationToken).ConfigureAwait(false);
            _appState.NeedsRedraw = true;
        }

        /// <summary>
        /// Starts the planner, once per app: the first profile with a site starts it, and a site edit that gives the
        /// profile one does if nothing has yet. A second start would load the catalogue twice and race the first over the
        /// planner's lists.
        /// </summary>
        private void StartPlanner(Transform transform, string name)
        {
            if (Interlocked.CompareExchange(ref _plannerStarted, 1, 0) == 0)
            {
                _tracker.Run(() => InitializePlannerAsync(transform, _cts.Token), name);
            }
        }

        /// <summary>
        /// Makes <paramref name="requested"/> the node's active profile, or with none requested and the node running none, the
        /// only profile it has (as the GUI chose one at start when it read the profiles itself). A node already running the
        /// profile asked for is left alone, and one refusing the switch (a device connected, a run going on) keeps its own,
        /// which the note says.
        /// </summary>
        private async Task ChooseLocalProfileAsync(TianWenNodeClient client, string? requested, CancellationToken cancellationToken)
        {
            var active = await client.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
            if (requested is null && !active.IsNotFound)
            {
                return;
            }

            var listed = await client.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
            if (listed is not { IsSuccess: true, Value: { } profiles })
            {
                _logger.LogWarning("Could not list the profiles of {Node}: {Error}", LocalNodeName, listed.Error);
                return;
            }

            var choice = requested is { } name
                ? profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                    || Guid.TryParse(name, out var id) && p.ProfileId == id)
                : profiles.Length == 1 ? profiles[0] : null;
            if (choice is null)
            {
                if (requested is { } missing)
                {
                    Notify(NotificationSeverity.Warning, $"There is no profile '{missing}'");
                }
                return;
            }
            if (active is { IsSuccess: true, Value: { } current } && current.ProfileId == choice.ProfileId)
            {
                return;
            }

            var set = await client.SetActiveProfileAsync(choice.ProfileId, cancellationToken).ConfigureAwait(false);
            if (!set.IsSuccess)
            {
                Notify(NotificationSeverity.Warning, $"Could not make '{choice.Name}' the active profile: {set.Error}");
            }
        }

        /// <summary>
        /// Earlier builds kept the site on the mount's URI. It is copied into the profile's own site fields the first time
        /// such a profile is read (<see cref="ProfileData.MigrateSiteFromMountUri"/>), and written back through the node, at
        /// the revision it was read at, so the copy is made once.
        /// </summary>
        private async Task MigrateLegacySiteAsync(LocalNodeConnection connection, CancellationToken cancellationToken)
        {
            if (_appState.ActiveProfile is not { Data: { } data } profile || connection.ProfileRevision is not { } revision)
            {
                return;
            }
            var (migrated, changed) = data.MigrateSiteFromMountUri();
            if (!changed)
            {
                return;
            }

            var written = await connection.Client.UpdateProfileAsync(profile.ProfileId, migrated, revision, null, cancellationToken).ConfigureAwait(false);
            if (written is { IsSuccess: true, Value: { } detail })
            {
                connection.AdoptProfileWrite(detail);
                _logger.LogInformation("Migrated site coordinates from Mount URI query into ProfileData for profile {ProfileId}.", profile.ProfileId);
            }
            else
            {
                // Shown migrated anyway: the site is right, and the copy is made again at the next start.
                _appState.ActiveProfile = profile.WithData(migrated);
                _logger.LogWarning("Could not write the migrated site of profile {ProfileId}: {Error}", profile.ProfileId, written.Error);
            }
        }
    }
}
