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
        /// How many device reads in a row this computer's node may leave unanswered (one every two seconds) before the app
        /// takes it as gone and finds or starts it again (<see cref="ReconnectLocalNodeAsync"/>).
        /// </summary>
        internal const int LocalNodeGoneAfterUnansweredReads = 3;

        /// <summary>How often finding the node again is retried while it cannot be found or started.</summary>
        internal static readonly TimeSpan LocalNodeRetryInterval = TimeSpan.FromSeconds(10);

        // What the first connect was asked with, for finding the node again; null until ConnectLocalNodeAsync has run.
        private LocalNodeOptions? _localNodeOptions;
        private bool _localNodeIncludeFake;
        // 1 while a reconnect runs: one at a time.
        private int _localNodeReconnecting;
        // Timestamp of the last failed reconnect while the node is lost, 0 while it is not lost.
        private long _localNodeLostRetryTicks;

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
        /// <param name="includeFake">Whether the first discovery lists the fake devices (the TUI's <c>--fake</c>); it does
        /// anyway when the profile names one, so a profile set up with fake devices can connect them at once.</param>
        public async Task ConnectLocalNodeAsync(LocalNodeOptions options, string? requestedProfile, bool includeFake,
            CancellationToken cancellationToken)
        {
            _localNodeOptions = options;
            _localNodeIncludeFake = includeFake;
            var (connection, message) = await LocalNodeConnection.FindOrStartAsync(
                _contexts, _appState, options, _appState.NodeGrants, _timeProvider, _logger, cancellationToken).ConfigureAwait(false);
            if (connection is null)
            {
                _appState.LocalNodeProblem = message;
                Notify(NotificationSeverity.Error, $"This computer's rig cannot be used: {message}");
                return;
            }

            if (connection.Outcome is LocalNodeOutcome.StartedWithTheClient)
            {
                // The launcher's words say why; what they mean for a night is worth saying once.
                Notify(NotificationSeverity.Warning, $"{message}. A session this window starts ends with it.");
            }
            if (connection.ModeNote is { } note)
            {
                // Used in the other mode (local-only or not) because it could not be replaced: what it binds, and so which
                // devices and rigs it reaches, is not what this window would start, and only the log said so.
                Notify(NotificationSeverity.Warning, $"This computer's node is used as it is: {note}.");
            }

            // The connection is published only once its profile is chosen and read. Published first, the frames' profile poll
            // (RefreshNodeProfiles) took the first read itself, before the profile was chosen, and this one was then skipped as
            // already in flight or not due: the profile was counted known while still null, and the Equipment tab said "No
            // equipment profile configured" at start on a node that runs one (LocalProfileAtConnectTests).
            await ChooseLocalProfileAsync(connection.Client, requestedProfile, cancellationToken).ConfigureAwait(false);
            await connection.MaybeRefreshProfileAsync(cancellationToken).ConfigureAwait(false);
            _appState.LocalNode = connection;
            _appState.LocalProfileKnown = true;
            _appState.NeedsRedraw = true;

            // The first discovery, so the Equipment tab lists what the node can reach, and the profile's devices resolve.
            _bus.Post(new DiscoverDevicesSignal(IncludeFake: includeFake || _appState.ActiveProfile?.Data is { ReferencesAnyFakeDevice: true }));

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
        /// Finds this computer's node again, or starts one, once the node it was connected to stops answering: a node that
        /// crashed, or one that exited idle across a sleep (2026-10-31: the window then polled a node that was gone until it
        /// was closed). For the frame poll to call (<see cref="PollNodeDevices"/>), one at a time. The lost connection is let
        /// go first, since the new one takes over the local view; the profile on show is made the new node's, and a node that
        /// cannot be found or started is retried every <see cref="LocalNodeRetryInterval"/>, the window saying why meanwhile.
        /// </summary>
        private void MaybeReconnectLocalNode()
        {
            if (_localNodeOptions is not { } options)
            {
                return;
            }
            var local = _appState.LocalNode;
            var lostTicks = Volatile.Read(ref _localNodeLostRetryTicks);
            var due = local is { UnansweredDeviceReads: >= LocalNodeGoneAfterUnansweredReads }
                || local is null && lostTicks != 0 && _timeProvider.GetElapsedTime(lostTicks) >= LocalNodeRetryInterval;
            if (!due || Interlocked.CompareExchange(ref _localNodeReconnecting, 1, 0) != 0)
            {
                return;
            }
            RunTracked("ReconnectLocalNode", "Could not reach this computer's node again",
                ct => ReconnectLocalNodeAsync(local, options, ct),
                onFinally: () => Volatile.Write(ref _localNodeReconnecting, 0));
        }

        private async Task ReconnectLocalNodeAsync(LocalNodeConnection? lost, LocalNodeOptions options, CancellationToken cancellationToken)
        {
            var profileId = _appState.ActiveProfile?.ProfileId;
            if (lost is not null)
            {
                _logger.LogWarning("This computer's node {NodeId} stopped answering; finding or starting it again", lost.Node.NodeId);
                _appState.LocalNode = null;
                await lost.DisposeAsync().ConfigureAwait(false);
                Notify(NotificationSeverity.Warning, "This computer's node stopped answering; finding or starting it again");
            }

            var (connection, message) = await LocalNodeConnection.FindOrStartAsync(
                _contexts, _appState, options, _appState.NodeGrants, _timeProvider, _logger, cancellationToken).ConfigureAwait(false);
            if (connection is null)
            {
                Volatile.Write(ref _localNodeLostRetryTicks, _timeProvider.GetTimestamp());
                _appState.LocalNodeProblem = message;
                _appState.NeedsRedraw = true;
                if (lost is not null)
                {
                    Notify(NotificationSeverity.Error, $"This computer's rig cannot be used: {message}");
                }
                return;
            }

            await ChooseLocalProfileAsync(connection.Client, profileId?.ToString(), cancellationToken).ConfigureAwait(false);
            await connection.MaybeRefreshProfileAsync(cancellationToken).ConfigureAwait(false);
            _appState.LocalNode = connection;
            _appState.LocalNodeProblem = null;
            Volatile.Write(ref _localNodeLostRetryTicks, 0);
            _appState.NeedsRedraw = true;
            Notify(NotificationSeverity.Info, $"This computer's node answers again: {message}");

            // The node may be a new one, which has discovered nothing yet: what the Equipment tab lists comes from this.
            _bus.Post(new DiscoverDevicesSignal(IncludeFake: _localNodeIncludeFake || _appState.ActiveProfile?.Data is { ReferencesAnyFakeDevice: true }));
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
    }
}
