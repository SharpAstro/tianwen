using LAN.Lib;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Binding and connecting to remote rigs -- the business logic behind
    /// <see cref="SelectRemoteRigSignal"/>, kept out of the subscribe lambda per the
    /// route-don't-implement rule (CLAUDE.md, "Signal Handler Pattern").
    /// </summary>
    public static class RemoteRigActions
    {
        /// <summary>What a select attempt produced, ready for the notification feed.</summary>
        public readonly record struct SelectOutcome(NotificationSeverity Severity, string Message);

        /// <summary>
        /// Binds (on first use) and connects to the rig announced as <paramref name="displayName"/>, then
        /// puts it on screen.
        /// <para>
        /// <b>Binding happens by looking, not by a setup step.</b> Selecting a discovered rig writes a
        /// <see cref="RemoteRigBinding"/> keyed on its stable node id, so the rig survives a rename or a
        /// new DHCP lease and reappears in the picker next run even if it is offline then. Re-selecting a
        /// rig already on screen is a no-op rather than a reconnect -- clicking the current context
        /// should never cost a poll cycle.
        /// </para>
        /// <para>
        /// The binding defaults to <see cref="RemoteRigBinding.RemoteProfileId"/> <c>null</c> = "mirror
        /// whatever it runs", which is the right default for a rig that plans its own nights: pinning a
        /// profile id here would make the binding wrong the moment the rig switched profiles itself.
        /// Choosing a specific profile is the drive-mode step, taken deliberately afterwards.
        /// </para>
        /// </summary>
        public static async Task<SelectOutcome> SelectAsync(
            string displayName,
            RemoteRigRegistry rigs,
            ViewContexts contexts,
            GuiAppState appState,
            IExternal external,
            ITimeProvider timeProvider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var binding = FindBinding(rigs, appState.PeerTable, displayName);
            if (binding is null)
            {
                return new SelectOutcome(NotificationSeverity.Warning,
                    $"'{displayName}' is no longer being announced on the network.");
            }

            // Already connected: just look at it.
            if (rigs.Find(binding.BindingId) is { } existing)
            {
                contexts.Activate(existing.Context);
                return new SelectOutcome(NotificationSeverity.Info, $"Watching {binding.Alias}");
            }

            var connection = RemoteRigConnection.TryConnect(
                binding, contexts, appState.PeerTable, timeProvider, logger, cancellationToken);

            if (connection is null)
            {
                // Keep the binding: "I own this rig and it is not answering" is information, and dropping
                // it would look like the binding was lost rather than the rig being off.
                rigs.Upsert(binding);
                await RemoteRigPersistence.SaveAsync(binding, external, cancellationToken).ConfigureAwait(false);
                return new SelectOutcome(NotificationSeverity.Warning,
                    $"{binding.Alias} is offline{(binding.LastAddress is { } a ? $" (last seen at {a})" : "")}.");
            }

            rigs.Attach(connection);

            // Persist with wherever it was actually reached, so the next run can try that address before
            // discovery has caught up.
            var reached = connection.BindingWithCurrentAddress();
            rigs.Upsert(reached);
            await RemoteRigPersistence.SaveAsync(reached, external, cancellationToken).ConfigureAwait(false);

            contexts.Activate(connection.Context);
            return new SelectOutcome(NotificationSeverity.Info, $"Watching {binding.Alias} at {connection.Address}");
        }

        /// <summary>
        /// The binding for a rig announced under <paramref name="displayName"/>: an existing one when the
        /// node id already has a record, otherwise a fresh binding minted from the announcement.
        /// <para>
        /// Matched on <b>node id</b> via the peer table, never on the display name -- two rigs can
        /// legitimately announce the same name, and a renamed rig must keep its binding.
        /// </para>
        /// </summary>
        private static RemoteRigBinding? FindBinding(RemoteRigRegistry rigs, IPeerTable? peers, string displayName)
        {
            var candidates = peers?.PeersOf(RemoteRigConnection.NodeServiceName);
            if (candidates is null || candidates.Count == 0)
            {
                // Not announcing right now -- fall back to a binding whose alias matches, so an offline
                // rig can still be selected (and reported as offline) rather than vanishing.
                return rigs.Bindings.FirstOrDefault(b =>
                    string.Equals(b.Alias, displayName, StringComparison.OrdinalIgnoreCase));
            }

            // Same labelling the picker used, so the string the user clicked maps back to the right peer
            // even when two rigs share a name (LanPeer.ResolveLabels disambiguates by machine, then PID).
            var labels = LanPeer.ResolveLabels(candidates);
            var index = Array.FindIndex(labels, l => string.Equals(l, displayName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return rigs.Bindings.FirstOrDefault(b =>
                    string.Equals(b.Alias, displayName, StringComparison.OrdinalIgnoreCase));
            }

            var peer = candidates[index];
            return rigs.Bindings.FirstOrDefault(b =>
                       string.Equals(b.NodeId, peer.NodeId, StringComparison.OrdinalIgnoreCase))
                ?? new RemoteRigBinding
                {
                    BindingId = Guid.NewGuid(),
                    NodeId = peer.NodeId,
                    RemoteProfileId = null,
                    Alias = labels[index],
                    LastAddress = $"http://{peer.EndPoint.Address}:{peer.EndPoint.Port}/",
                };
        }
    }
}
