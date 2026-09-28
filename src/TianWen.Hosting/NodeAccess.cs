using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LAN.Lib;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>What the node keeps of a request for control while it waits: who asked, from where, and the hash of the secret its asker polls with.</summary>
internal sealed record ControlAsker(string Label, string? Address, string SecretHash);

/// <summary>
/// Who may command this node (P6b of docs/plans/hardware-in-the-server.md, decision 13, #1021). Over TCP a client sees,
/// and commands only with a grant the rig's machine gave it; another application, which cannot ask, only from an address
/// allowed until the node restarts or a host name always allowed. A client of this machine, over the node's socket, may
/// always command it. <see cref="NodeAccessGate"/> asks this before every command.
/// </summary>
/// <remarks>
/// <para><b>A TianWen client asks, and the rig's machine answers</b>, by LAN.Lib's rules (<see cref="LanInvites{T}"/>):
/// one request waits at a time, it lives only while its asker polls, and an answer names the request it answers. The
/// grant is minted when the asker next polls after an Allow, never at the Allow itself, so an asker that has gone by then
/// leaves no grant behind for nobody.</para>
/// <para><b>A grant is remembered until revoked</b> (<see cref="LanGrants"/>, <see cref="GrantsFileName"/>): its token is
/// kept only as its hash, so a copy of the file grants nothing.</para>
/// <para><b>Another application's refused command is its request.</b> One record per address, a retry updating it, with
/// its forward-confirmed host name (<see cref="LanHostNames"/>) looked up once, after the refusal, so the refusal itself
/// never waits on a resolver.</para>
/// </remarks>
internal sealed class NodeAccess
{
    /// <summary>Where the node keeps its grants, beside its settings in the data root.</summary>
    public const string GrantsFileName = "node-grants.json";

    private readonly NodeSettingsStore _settings;
    private readonly EventHub _events;
    private readonly ITimeProvider _timeProvider;
    private readonly ILogger<NodeAccess> _logger;
    private readonly LanGrants _grants;
    private readonly LanInvites<ControlAsker> _requests;
    private readonly LanHostNames _hostNames;
    private readonly Lazy<Task> _loaded;

    // The addresses another application may command from, until the node restarts.
    private readonly ConcurrentDictionary<IPAddress, byte> _appsAllowed = new ConcurrentDictionary<IPAddress, byte>();

    // Another application's refused commands, one record per address.
    private readonly ConcurrentDictionary<IPAddress, RefusedAppDto> _refused = new ConcurrentDictionary<IPAddress, RefusedAppDto>();

    // The secret of each request made, by its id, kept past its answer so its asker can still collect the outcome.
    private readonly ConcurrentDictionary<string, ControlAsker> _tickets = new ConcurrentDictionary<string, ControlAsker>();

    // Requests whose grant has been handed to their asker: a second poll must not mint a second grant.
    private readonly ConcurrentDictionary<string, byte> _handedOver = new ConcurrentDictionary<string, byte>();

    public NodeAccess(IExternal external, NodeSettingsStore settings, EventHub events, ITimeProvider timeProvider,
        IHostNameResolver hostNameResolver, ILogger<NodeAccess> logger)
    {
        _settings = settings;
        _events = events;
        _timeProvider = timeProvider;
        _logger = logger;
        _grants = new LanGrants(new GrantFile(external), timeProvider.System);
        _requests = new LanInvites<ControlAsker>(timeProvider.System, NodeWire.ControlRequestLapse);
        _hostNames = new LanHostNames(hostNameResolver, timeProvider.System);
        _loaded = new Lazy<Task>(() => _grants.LoadAsync(CancellationToken.None).AsTask());
    }

    /// <summary>The address a request came from, an IPv4 address seen through an IPv6 socket as the IPv4 address; null over the node's socket.</summary>
    public static IPAddress? AddressOf(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address) : null;

    /// <summary>The bearer token a request presented, if any.</summary>
    public static string? BearerOf(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) && header.Length > scheme.Length
            ? header[scheme.Length..].Trim()
            : null;
    }

    /// <summary>The grant the request presented, or null: none, or one the node does not hold (never given, or revoked).</summary>
    public async ValueTask<LanGrant?> GrantOfAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (BearerOf(context) is not { } token)
        {
            return null;
        }
        await _loaded.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return _grants.TryVerify(token, out var grant) ? grant : null;
    }

    /// <summary>
    /// Whether the request may command the node and manage who else may: it came over the node's socket, or presented a
    /// grant the node holds.
    /// </summary>
    public async ValueTask<bool> MayCommandAsync(HttpContext context, CancellationToken cancellationToken) =>
        NodeEndpoints.CameOverTheSocket(context) || await GrantOfAsync(context, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// For an event socket being opened: whether its client may command the node, asked again at every look, so a grant
    /// revoked while the socket is open stops counting at once.
    /// </summary>
    public async ValueTask<Func<bool>> MayCommandOverTimeAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (NodeEndpoints.CameOverTheSocket(context))
        {
            return static () => true;
        }
        if (await GrantOfAsync(context, cancellationToken).ConfigureAwait(false) is not { } grant)
        {
            return static () => false;
        }
        var id = grant.Id;
        return () => _grants.All.Any(held => held.Id == id);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // A TianWen client asks for control
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Offers a request for control from <paramref name="label"/> at <paramref name="address"/>: its ticket, or null when
    /// another request is waiting (a person answers one at a time).
    /// </summary>
    public ControlRequestTicketDto? Request(string label, IPAddress? address)
    {
        // Forget the tickets whose outcomes are no longer kept, so an asker that never came back leaves nothing behind.
        foreach (var known in _tickets.Keys)
        {
            if (_requests.OutcomeOf(known) is LanInviteOutcome.Unknown)
            {
                _tickets.TryRemove(known, out _);
                _handedOver.TryRemove(known, out _);
            }
        }

        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var asker = new ControlAsker(string.IsNullOrWhiteSpace(label) ? "A client" : label.Trim(), address?.ToString(), HashOf(secret));
        if (!_requests.TryOffer(asker.Label, asker, out var invite))
        {
            return null;
        }
        _tickets[invite.Id] = asker;
        _logger.LogInformation("{Label} ({Address}) asks to control this node: request {Id}", asker.Label, asker.Address ?? "?", invite.Id);
        PushAccessChanged();
        return new ControlRequestTicketDto { Id = invite.Id, Secret = secret };
    }

    /// <summary>
    /// Where request <paramref name="id"/> stands, for its asker, whose poll keeps it alive: once it is allowed, the first
    /// poll after mints the grant and hands over its token, and every later one says only that it was granted.
    /// </summary>
    public async Task<ControlRequestOutcomeDto> PollAsync(string id, string secret, CancellationToken cancellationToken)
    {
        if (!_tickets.TryGetValue(id, out var asker)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(asker.SecretHash), Encoding.ASCII.GetBytes(HashOf(secret))))
        {
            return new ControlRequestOutcomeDto { State = ControlRequestState.Unknown };
        }

        var waiting = _requests.Seen(id);
        switch (_requests.OutcomeOf(id))
        {
            case LanInviteOutcome.Pending when waiting:
                return new ControlRequestOutcomeDto { State = ControlRequestState.Pending };
            case LanInviteOutcome.Declined:
                return new ControlRequestOutcomeDto { State = ControlRequestState.Declined };
            case LanInviteOutcome.Accepted:
                if (!_handedOver.TryAdd(id, 0))
                {
                    return new ControlRequestOutcomeDto { State = ControlRequestState.Granted };
                }
                try
                {
                    await _loaded.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var issued = await _grants.GrantAsync(asker.Label, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("{Label} ({Address}) was granted control of this node (grant {Grant})", asker.Label, asker.Address ?? "?", issued.Grant.Id);
                    PushAccessChanged();
                    return new ControlRequestOutcomeDto { State = ControlRequestState.Granted, Token = issued.Token };
                }
                catch
                {
                    // Not handed over after all: the asker's next poll tries again.
                    _handedOver.TryRemove(id, out _);
                    throw;
                }
            case LanInviteOutcome.Withdrawn:
                return new ControlRequestOutcomeDto { State = ControlRequestState.Withdrawn };
            default:
                // Forgotten (its outcome kept past its time, or it lapsed unanswered): so is its ticket.
                _tickets.TryRemove(id, out _);
                _handedOver.TryRemove(id, out _);
                return new ControlRequestOutcomeDto { State = ControlRequestState.Unknown };
        }
    }

    /// <summary>Answers request <paramref name="id"/>: true when it was the one waiting.</summary>
    public bool Answer(string id, bool allow)
    {
        if (_requests.Answer(id, allow) is not { } answered)
        {
            return false;
        }
        _logger.LogInformation("The request {Id} from {Label} to control this node was {Answer}", id, answered.Label, allow ? "allowed" : "declined");
        PushAccessChanged();
        return true;
    }

    /// <summary>Revokes grant <paramref name="id"/>: true when there was one. Its token is refused from the next request on.</summary>
    public async Task<bool> RevokeAsync(string id, CancellationToken cancellationToken)
    {
        await _loaded.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!await _grants.RevokeAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        _logger.LogInformation("Grant {Id} was revoked", id);
        PushAccessChanged();
        return true;
    }

    /// <summary>A grant made without asking, for a host that composes its own client (a test's).</summary>
    internal async Task<LanGrantIssued> GrantAsync(string label, CancellationToken cancellationToken)
    {
        await _loaded.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _grants.GrantAsync(label, cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Another application
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether another application at <paramref name="address"/> may command: its address is allowed until the node
    /// restarts, or its forward-confirmed host name is always allowed.
    /// </summary>
    public async ValueTask<bool> AppMayCommandAsync(IPAddress address, CancellationToken cancellationToken)
    {
        if (_appsAllowed.ContainsKey(address))
        {
            return true;
        }
        var hosts = _settings.Current.AlwaysAllowedHosts;
        if (hosts is not { Count: > 0 })
        {
            return false;
        }
        return await _hostNames.ConfirmedNameOfAsync(address, cancellationToken).ConfigureAwait(false) is { } name
            && hosts.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records another application's refused command: its request. The first refusal from an address pushes
    /// <see cref="NodeWire.AccessChangedEvent"/> and looks its host name up, and pushes again once it has.
    /// </summary>
    public void RecordRefusal(IPAddress address, AppProtocol protocol, string? userAgent, string? clientId, string what)
    {
        var now = _timeProvider.GetUtcNow();
        var first = false;
        _refused.AddOrUpdate(address,
            _ =>
            {
                first = true;
                return new RefusedAppDto
                {
                    Address = address.ToString(), UserAgent = userAgent, ClientId = clientId, Protocol = protocol, What = what,
                    FirstAt = now, LastAt = now, Attempts = 1,
                };
            },
            (_, known) => new RefusedAppDto
            {
                Address = known.Address, Host = known.Host, UserAgent = userAgent ?? known.UserAgent, ClientId = clientId ?? known.ClientId,
                Protocol = protocol, What = what, FirstAt = known.FirstAt, LastAt = now, Attempts = known.Attempts + 1,
            });
        if (!first)
        {
            return;
        }
        _logger.LogInformation("{Protocol} client at {Address} ({UserAgent}) was refused {What}: this rig has not allowed it", protocol, address, userAgent ?? "?", what);
        PushAccessChanged();
        _ = NameRefusedAsync(address);
    }

    private async Task NameRefusedAsync(IPAddress address)
    {
        // Bounded by the lookup's own budget; never the refusal's, which has been answered already.
        if (await _hostNames.ConfirmedNameOfAsync(address, CancellationToken.None).ConfigureAwait(false) is not { } host)
        {
            return;
        }
        while (_refused.TryGetValue(address, out var known) && known.Host is null)
        {
            var named = new RefusedAppDto
            {
                Address = known.Address, Host = host, UserAgent = known.UserAgent, ClientId = known.ClientId, Protocol = known.Protocol,
                What = known.What, FirstAt = known.FirstAt, LastAt = known.LastAt, Attempts = known.Attempts,
            };
            if (_refused.TryUpdate(address, named, known))
            {
                PushAccessChanged();
                return;
            }
        }
    }

    /// <summary>What <see cref="AllowAppAsync"/> did.</summary>
    public enum AppAllowOutcome
    {
        Allowed,

        /// <summary>Always allow was asked for an address with no forward-confirmed host name to remember.</summary>
        NoConfirmedName,
    }

    /// <summary>
    /// Lets another application at <paramref name="address"/> command: until the node restarts, and with
    /// <paramref name="always"/> by its forward-confirmed host name from then on too.
    /// </summary>
    public async Task<AppAllowOutcome> AllowAppAsync(IPAddress address, bool always, CancellationToken cancellationToken)
    {
        if (always)
        {
            if (await _hostNames.ConfirmedNameOfAsync(address, cancellationToken).ConfigureAwait(false) is not { } host)
            {
                return AppAllowOutcome.NoConfirmedName;
            }
            await _settings.UpdateAsync(settings => settings with
            {
                AlwaysAllowedHosts = [.. (settings.AlwaysAllowedHosts ?? []).Where(known => !string.Equals(known, host, StringComparison.OrdinalIgnoreCase)), host],
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Other applications on {Host} are always allowed to command this node", host);
        }
        _appsAllowed[address] = 0;
        _refused.TryRemove(address, out _);
        _logger.LogInformation("Other applications at {Address} may command this node until it restarts", address);
        PushAccessChanged();
        return AppAllowOutcome.Allowed;
    }

    /// <summary>Ends <paramref name="address"/>'s allowance until restart: true when it had one.</summary>
    public bool RevokeApp(IPAddress address)
    {
        if (!_appsAllowed.TryRemove(address, out _))
        {
            return false;
        }
        PushAccessChanged();
        return true;
    }

    /// <summary>Forgets <paramref name="host"/> as always allowed: true when it was.</summary>
    public async Task<bool> RevokeHostAsync(string host, CancellationToken cancellationToken)
    {
        var removed = false;
        await _settings.UpdateAsync(settings =>
        {
            var kept = (settings.AlwaysAllowedHosts ?? []).Where(known => !string.Equals(known, host, StringComparison.OrdinalIgnoreCase)).ToArray();
            removed = kept.Length != (settings.AlwaysAllowedHosts?.Count ?? 0);
            return removed ? settings with { AlwaysAllowedHosts = kept } : settings;
        }, cancellationToken).ConfigureAwait(false);
        if (removed)
        {
            PushAccessChanged();
        }
        return removed;
    }

    /// <summary>Drops the refusal record of <paramref name="address"/> (Ignore): true when there was one. Its next refusal records it again.</summary>
    public bool IgnoreRefusal(IPAddress address)
    {
        if (!_refused.TryRemove(address, out _))
        {
            return false;
        }
        PushAccessChanged();
        return true;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // The Sharing panel's read
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>Everything about who may command the node, for the Sharing panel.</summary>
    public async Task<NodeAccessDto> DescribeAsync(NodeListening listening, CancellationToken cancellationToken)
    {
        await _loaded.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pending = _requests.Pending;
        var allowed = new List<AllowedAppDto>();
        foreach (var (address, _) in _appsAllowed)
        {
            allowed.Add(new AllowedAppDto { Address = address.ToString(), Host = await _hostNames.ConfirmedNameOfAsync(address, cancellationToken).ConfigureAwait(false) });
        }
        return new NodeAccessDto
        {
            Shared = _settings.Current.ShareOnLan,
            Listening = listening.LanPort is not null,
            Pending = pending is null ? null : new PendingControlRequestDto
            {
                Id = pending.Id, Label = pending.Label, Address = pending.Payload.Address, At = pending.OfferedAt,
            },
            Grants = [.. _grants.All.Select(grant => new GrantDto { Id = grant.Id, Label = grant.Label, GrantedAt = grant.GrantedAt })],
            AppsAllowed = [.. allowed.OrderBy(app => app.Address, StringComparer.Ordinal)],
            HostsAlwaysAllowed = [.. _settings.Current.AlwaysAllowedHosts ?? []],
            Refused = [.. _refused.Values.OrderByDescending(refused => refused.LastAt)],
        };
    }

    private void PushAccessChanged() => _events.Broadcast(BroadcastEvents.AccessChanged());

    private static string HashOf(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>The grants, kept in the data root through the atomic writer, since every file there has more than one process on it.</summary>
    private sealed class GrantFile(IExternal external) : ILanGrantStore
    {
        private string PathOf => Path.Combine(external.AppDataFolder.FullName, GrantsFileName);

        public async ValueTask<IReadOnlyList<LanGrant>> LoadAsync(CancellationToken cancellationToken) =>
            await external.TryReadJsonAsync(PathOf, NodeAccessJsonContext.Default.LanGrantArray, ct: cancellationToken).ConfigureAwait(false) ?? [];

        public async ValueTask SaveAsync(IReadOnlyList<LanGrant> grants, CancellationToken cancellationToken) =>
            await external.AtomicWriteJsonAsync(PathOf, grants.ToArray(), NodeAccessJsonContext.Default.LanGrantArray, cancellationToken).ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(LanGrant[]))]
internal partial class NodeAccessJsonContext : JsonSerializerContext;
