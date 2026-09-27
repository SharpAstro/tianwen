using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.RemoteClient;

/// <summary>
/// The control a client has been granted on one node (P6b of docs/plans/hardware-in-the-server.md, #1021): the bearer
/// token its requests and its sockets present, once it has one. Over TCP a node refuses every command without it (a
/// client there sees, and commands only once granted); over the node's socket it is never needed.
/// </summary>
/// <remarks>
/// Settable, because a client asks for control after it has connected: every request reads the token as it is sent, so a
/// grant arriving mid-session applies to the next request with no new client. A socket already open was upgraded without
/// it, which is why a client granted after connecting reconnects its event stream (<see cref="TianWenEventStream.Reconnect"/>):
/// the node counts a client as able to answer a prompt by what its upgrade carried.
/// </remarks>
public sealed class NodeGrant
{
    private string? _token;

    /// <summary>A grant with <paramref name="token"/> already held, or none.</summary>
    public NodeGrant(string? token = null) => _token = token;

    /// <summary>The token, or null while nothing has been granted (or after the node refused it).</summary>
    public string? Token
    {
        get => Volatile.Read(ref _token);
        set => Volatile.Write(ref _token, value);
    }

    /// <summary>Whether a token is held.</summary>
    public bool IsHeld => Token is not null;

    /// <summary>Puts the token on a WebSocket's upgrade request, when one is held.</summary>
    internal void Present(ClientWebSocketOptions options)
    {
        if (Token is { } token)
        {
            options.SetRequestHeader("Authorization", "Bearer " + token);
        }
    }

    /// <summary>
    /// A TCP connection that puts the token on each request as it goes, when one is held. It makes its own inner handler,
    /// which it disposes with itself, so nothing disposable changes hands to build it.
    /// </summary>
    internal sealed class Handler : DelegatingHandler
    {
        private readonly NodeGrant _grant;

        public Handler(NodeGrant grant, DecompressionMethods decompression)
        {
            _grant = grant;
            InnerHandler = new SocketsHttpHandler { AutomaticDecompression = decompression };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_grant.Token is { } token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
