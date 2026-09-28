using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LAN.Lib;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Hosting;
using TianWen.RemoteClient;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A test host's own client over loopback TCP, which a node treats as the LAN (P6b of docs/plans/hardware-in-the-server.md,
/// #1021): it holds a real grant, as a laptop the rig's owner allowed would, and another application on loopback (an Alpaca
/// or ninaAPI test's client) is allowed until the node restarts. The rule under test is the node's own, never a bypass; a
/// test of the gate itself uses a client without these.
/// </summary>
internal static class NodeAccessForTests
{
    /// <summary>
    /// A resolver that names nothing, registered by every test's node in place of DNS: a refusal from a LAN address looks its
    /// host name up, and a test sends nothing onto the LAN. A test that needs a name registers a resolver of its own.
    /// </summary>
    public sealed class NoHostNames : IHostNameResolver
    {
        public static readonly NoHostNames Instance = new NoHostNames();

        public Task<string?> ReverseAsync(IPAddress address, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<IPAddress>> ForwardAsync(string hostName, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([]);
    }

    /// <summary>A grant for the test's client, and loopback allowed for other applications.</summary>
    public static async Task<NodeGrant> TrustTheTestAsync(WebApplication app, CancellationToken cancellationToken)
    {
        var access = app.Services.GetRequiredService<NodeAccess>();
        await access.AllowAppAsync(IPAddress.Loopback, always: false, cancellationToken);
        await access.AllowAppAsync(IPAddress.IPv6Loopback, always: false, cancellationToken);
        var issued = await access.GrantAsync("The test's client", cancellationToken);
        return new NodeGrant(issued.Token);
    }
}
