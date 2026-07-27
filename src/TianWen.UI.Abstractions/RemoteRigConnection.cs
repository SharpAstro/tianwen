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
    /// One bound rig, live: resolves its address from discovery, owns the
    /// <see cref="RemoteSessionMirror"/> that mirrors its session, and hands that mirror to a
    /// <see cref="ViewContext"/> so the tabs render the rig exactly as they render a local session.
    /// <para>
    /// <b>Address is resolved per connect, never stored as identity.</b> A binding names a
    /// <see cref="RemoteRigBinding.NodeId"/>; the endpoint comes from the live peer table, falling back
    /// to the binding's <see cref="RemoteRigBinding.LastAddress"/> hint when discovery has not yet seen
    /// the rig this run. A rig that changed DHCP lease therefore reconnects without the user touching
    /// anything, and one that is genuinely off shows as offline rather than silently binding to whoever
    /// now holds its old address.
    /// </para>
    /// </summary>
    public sealed class RemoteRigConnection : IAsyncDisposable
    {
        /// <summary>The LAN.Lib service name a TianWen node announces.</summary>
        public const string NodeServiceName = "tianwen-server";

        private readonly HttpClient _http;
        private readonly TianWenNodeClient _client;
        private readonly TianWenEventStream _events;
        private readonly ILogger _logger;

        private RemoteRigConnection(
            RemoteRigBinding binding, ViewContext context, Uri address,
            HttpClient http, TianWenNodeClient client, TianWenEventStream events,
            RemoteSessionMirror mirror, ILogger logger)
        {
            Binding = binding;
            Context = context;
            Address = address;
            _http = http;
            _client = client;
            _events = events;
            Mirror = mirror;
            _logger = logger;
        }

        /// <summary>The binding this connection serves.</summary>
        public RemoteRigBinding Binding { get; }

        /// <summary>The view context whose <see cref="ViewContext.LiveSession"/> this connection feeds.</summary>
        public ViewContext Context { get; }

        /// <summary>The node root actually connected to.</summary>
        public Uri Address { get; }

        /// <summary>The live mirror. Also the control surface (start / flats / abort / prompts).</summary>
        public RemoteSessionMirror Mirror { get; }

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

            var http = new HttpClient { BaseAddress = address };
            var client = new TianWenNodeClient(http);
            var events = new TianWenEventStream(address, timeProvider, logger);
            var mirror = new RemoteSessionMirror(client, events, timeProvider, logger);

            var context = contexts.GetOrAddRemote(binding.NodeId, binding.Alias);

            // This is the whole payoff of the ISessionTelemetry split: from here the Live Session and
            // Guider tabs render the rig with no knowledge that it is remote.
            context.LiveSession.ActiveSession = mirror;

            mirror.Start(cancellationToken);
            logger.LogInformation("Mirroring rig '{Alias}' at {Address}", binding.Alias, address);

            return new RemoteRigConnection(binding, context, address, http, client, events, mirror, logger);
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
        /// The binding updated with wherever the rig was actually reached, so the next run can try it
        /// before discovery has caught up. Persist this, not the original.
        /// </summary>
        public RemoteRigBinding BindingWithCurrentAddress() =>
            Binding with { LastAddress = Address.ToString() };

        /// <summary>Pushes the rig's own site onto the context, so the planner and sky map work against
        /// the rig's horizon rather than this computer's.</summary>
        public async Task<ProfileDetailDto?> TryFetchProfileAsync(CancellationToken cancellationToken)
        {
            if (Binding.RemoteProfileId is not { } profileId)
            {
                return null;
            }

            var result = await _client.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (result is { IsSuccess: true, Value: { } profile })
            {
                return profile;
            }

            _logger.LogWarning("Could not fetch profile {ProfileId} from rig '{Alias}': {Error}",
                profileId, Binding.Alias, result.Error);
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            // Detach BEFORE tearing the mirror down: a render pass between dispose and detach would read
            // a mirror whose poll loop has already stopped, and show a frozen session as though live.
            Context.LiveSession.ActiveSession = null;

            await Mirror.DisposeAsync().ConfigureAwait(false);
            _http.Dispose();
        }
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
