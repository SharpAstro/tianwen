using LAN.Lib;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// One bound rig, live: a <see cref="NodeConnection"/> over the LAN, whose address it resolves from discovery, so the
    /// tabs render the rig exactly as they render this computer's own node.
    /// <para>
    /// <b>Address is resolved per connect, never stored as identity.</b> A binding names a
    /// <see cref="RemoteRigBinding.NodeId"/>; the endpoint comes from the live peer table, falling back
    /// to the binding's <see cref="RemoteRigBinding.LastAddress"/> hint when discovery has not yet seen
    /// the rig this run. A rig that changed DHCP lease therefore reconnects without the user touching
    /// anything, and one that is genuinely off shows as offline rather than silently binding to whoever
    /// now holds its old address.
    /// </para>
    /// </summary>
    public sealed class RemoteRigConnection : NodeConnection
    {
        /// <summary>The LAN.Lib service name a TianWen node announces.</summary>
        public const string NodeServiceName = "tianwen-server";

        private int _firstContactClaimed;

        private RemoteRigConnection(RemoteRigBinding binding, ViewContext context, Uri address, NodeTransport transport,
            ITimeProvider timeProvider, ILogger logger, CancellationToken cancellationToken)
            // A rig's question waits where the rig is shown: its prompts are not brought to the front.
            : base(context, transport, promptsApp: null, timeProvider, logger, cancellationToken)
        {
            Binding = binding;
            Address = address;
        }

        /// <summary>The binding this connection serves.</summary>
        public RemoteRigBinding Binding { get; }

        /// <summary>The node root actually connected to.</summary>
        public Uri Address { get; }

        private protected override string Name => $"rig '{Binding.Alias}'";

        /// <summary>The binding's own choice of profile, else the one the rig runs (P5b part 8).</summary>
        private protected override Guid? ProfileToPlanWith(Guid? running) => Binding.RemoteProfileId ?? running;

        private protected override void OnProfileRead(Profile profile) => Context.RigProfile = profile;

        private protected override Profile? ProfileOnView => Context.RigProfile;

        private protected override void OnDetached() => Context.RigProfile = null;

        /// <summary>
        /// Resolves <paramref name="binding"/> to an address and starts mirroring it, returning null when
        /// the rig is neither discoverable nor has a usable address hint (i.e. it is offline).
        /// </summary>
        public static RemoteRigConnection? TryConnect(
            RemoteRigBinding binding,
            ViewContexts contexts,
            IPeerTable? peers,
            ITimeProvider timeProvider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (ResolveAddress(binding, peers) is not { } address)
            {
                logger.LogInformation("Rig '{Alias}' ({NodeId}) is not reachable: no discovered address and no hint",
                    binding.Alias, binding.NodeId);
                return null;
            }

            // A rig that is switched off does not refuse the connection (that fails instantly), it black-holes the packets,
            // so NodeTransport's explicit backstop, not the 100 s default, is what notices it has gone.
            var connection = new RemoteRigConnection(binding, contexts.GetOrAddRemote(binding.NodeId, binding.Alias), address,
                NodeTransport.OverTcp(address), timeProvider, logger, cancellationToken);
            logger.LogInformation("Mirroring rig '{Alias}' at {Address}", binding.Alias, address);
            return connection;
        }

        /// <summary>
        /// The rig's current endpoint: the live peer table first, then the binding's last-known address.
        /// </summary>
        internal static Uri? ResolveAddress(RemoteRigBinding binding, IPeerTable? peers)
        {
            if (peers?.PeersOf(NodeServiceName)
                    .FirstOrDefault(p => string.Equals(p.NodeId, binding.NodeId, StringComparison.OrdinalIgnoreCase))
                is { } peer)
            {
                return new Uri($"http://{peer.EndPoint.Address}:{peer.EndPoint.Port}/");
            }

            return Uri.TryCreate(binding.LastAddress, UriKind.Absolute, out var hint) ? hint : null;
        }

        /// <summary>
        /// The binding updated with wherever the rig was actually reached and when it last answered, so
        /// the next run can try that address before discovery has caught up and can say how long a rig
        /// has been silent. Persist this, not the original.
        /// <para>
        /// The address is known the moment a connection exists; the timestamp is not.
        /// <see cref="TryConnect"/> makes <b>no HTTP call</b> -- it resolves an endpoint from the peer
        /// table or the stored hint -- so at connect time we have somewhere to talk to and no evidence
        /// anyone is listening. Stamping "seen" there would record a rig as reachable purely because it
        /// was once announced. Hence the fall back to the previous value until the mirror has actually
        /// had an answer.
        /// </para>
        /// </summary>
        public RemoteRigBinding BindingAsReached() =>
            Binding with
            {
                LastAddress = Address.ToString(),
                LastSeenUtc = Mirror.LastContactUtc ?? Binding.LastSeenUtc,
            };

        /// <summary>
        /// True exactly once per connection, on the first poll the rig actually answers -- the caller's
        /// cue to persist <see cref="BindingAsReached"/>.
        /// <para>
        /// This plus the flush on quit is what bounds writes to two per rig per run. Polling alone would
        /// leave a hard kill with no stamp at all; the quit flush alone would leave one with a stamp
        /// from the previous run.
        /// </para>
        /// </summary>
        public bool TryClaimFirstContact() =>
            Mirror.LastContactUtc is not null && Interlocked.Exchange(ref _firstContactClaimed, 1) == 0;
    }

    /// <summary>
    /// Every bound rig, and which of them are currently connected. One per app.
    /// <para>
    /// Bindings persist; connections do not. A rig that is bound but offline stays in the list with its
    /// last-known address, because "I own this rig, it is not answering" is information -- silently
    /// dropping it would look like the binding was lost.
    /// </para>
    /// </summary>
    public sealed class RemoteRigRegistry
    {
        private ImmutableArray<RemoteRigBinding> _bindings = [];
        private ImmutableDictionary<Guid, RemoteRigConnection> _connections =
            ImmutableDictionary<Guid, RemoteRigConnection>.Empty;

        /// <summary>Every binding known locally, alias-ordered. Replaced atomically.</summary>
        public ImmutableArray<RemoteRigBinding> Bindings => _bindings;

        /// <summary>Live connections by binding id. Replaced atomically.</summary>
        public ImmutableDictionary<Guid, RemoteRigConnection> Connections => _connections;

        /// <summary>Whether this binding currently has a live mirror.</summary>
        public bool IsConnected(Guid bindingId) => _connections.ContainsKey(bindingId);

        /// <summary>The connection for a binding, if any.</summary>
        public RemoteRigConnection? Find(Guid bindingId) =>
            _connections.TryGetValue(bindingId, out var connection) ? connection : null;

        /// <summary>Replaces the whole binding set (after a load from disk).</summary>
        public void SetBindings(ImmutableArray<RemoteRigBinding> bindings) =>
            ImmutableInterlocked.InterlockedExchange(ref _bindings, bindings.IsDefault ? [] : bindings);

        /// <summary>Adds or replaces one binding, keyed on <see cref="RemoteRigBinding.BindingId"/>.</summary>
        public void Upsert(RemoteRigBinding binding)
        {
            var current = _bindings;
            var index = -1;
            for (var i = 0; i < current.Length; i++)
            {
                if (current[i].BindingId == binding.BindingId)
                {
                    index = i;
                    break;
                }
            }

            ImmutableInterlocked.InterlockedExchange(
                ref _bindings, index >= 0 ? current.SetItem(index, binding) : current.Add(binding));
        }

        /// <summary>Records a live connection.</summary>
        public void Attach(RemoteRigConnection connection) =>
            ImmutableInterlocked.TryAdd(ref _connections, connection.Binding.BindingId, connection);

        /// <summary>Forgets a connection, returning it so the caller can dispose it off the render thread.</summary>
        public RemoteRigConnection? Detach(Guid bindingId) =>
            ImmutableInterlocked.TryRemove(ref _connections, bindingId, out var connection) ? connection : null;

        /// <summary>Removes a binding entirely (its connection, if any, is returned for disposal).</summary>
        public RemoteRigConnection? Remove(Guid bindingId)
        {
            var current = _bindings;
            var remaining = current.RemoveAll(b => b.BindingId == bindingId);
            ImmutableInterlocked.InterlockedExchange(ref _bindings, remaining);
            return Detach(bindingId);
        }
    }
}
