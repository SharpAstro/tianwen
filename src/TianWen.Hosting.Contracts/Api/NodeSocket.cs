using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using TianWen.Lib;

namespace TianWen.Hosting.Api;

/// <summary>
/// Where this machine's node listens: a Unix domain socket under the per-user AppData root, which is how every
/// client on the machine reaches it (docs/plans/hardware-in-the-server.md, "Transport"). One helper, shared by
/// the node and its clients, so the two can never look in different places.
/// </summary>
/// <remarks>
/// Under the AppData root rather than <c>$XDG_RUNTIME_DIR</c>, which logout removes: the lock and the crash
/// journal beside the socket must outlive a reboot. The directory's ACL is the access control, since only the
/// user, SYSTEM and administrators can open it; nothing else authenticates a client on the socket.
/// </remarks>
public static class NodeSocket
{
    /// <summary>
    /// Points a client at a node instead of letting it find or start one: the value of <c>--node-socket</c> when
    /// no argument names it. A developer runs the server under the debugger and attaches a GUI this way, and a
    /// test gives a client its own node.
    /// </summary>
    public const string EnvironmentVariable = "TIANWEN_NODE_SOCKET";

    /// <summary>The socket's file name under the AppData root.</summary>
    public const string DefaultFileName = "node.sock";

    /// <summary>
    /// The socket every node binds unless <c>--socket</c> names another, and where every client looks. A GUI
    /// restarted after a crash finds the node that outlived it here.
    /// </summary>
    public static string DefaultPath { get; } = Path.Combine(TianWenDataRoot.Directory.FullName, DefaultFileName);

    /// <summary>
    /// The address a request over the socket carries. Only its host reaches the node, as the <c>Host</c> header;
    /// the connection itself goes to the socket (<see cref="CreateHandler"/>).
    /// </summary>
    public static Uri BaseAddress { get; } = new Uri("http://localhost/");

    /// <summary>
    /// The longest socket path this system can bind, in bytes: <c>sun_path</c> holds 108 on Linux and Windows and
    /// 104 on macOS and the BSDs, one of which is the terminator.
    /// </summary>
    internal static int MaxPathBytes => OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 103 : 107;

    /// <summary>
    /// The lock beside a socket, which admits one node to it: <c>node.sock</c> is guarded by <c>node.lock</c>. Named
    /// after its socket, so a test's node on a socket of its own takes a lock of its own.
    /// </summary>
    public static string LockPathFor(string socketPath) => Path.ChangeExtension(socketPath, ".lock");

    /// <summary>
    /// Whether <paramref name="socketPath"/> fits in a socket address. A path that does not fit is refused with a
    /// message naming it, rather than bound truncated, which would put the node somewhere no client looks.
    /// </summary>
    public static bool TryValidate(string socketPath, [NotNullWhen(false)] out string? error)
    {
        var bytes = Encoding.UTF8.GetByteCount(socketPath);
        if (bytes > MaxPathBytes)
        {
            error = $"The node socket path is {bytes} bytes, longer than the {MaxPathBytes} a Unix domain socket can hold here: {socketPath}";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// The socket a client uses: <paramref name="commandLineValue"/> (a <c>--node-socket</c> argument) first, then
    /// <see cref="EnvironmentVariable"/>. Named either way, the client connects there and never starts a node of
    /// its own; named neither way, it uses <see cref="DefaultPath"/> and may start one.
    /// </summary>
    /// <returns><see langword="true"/> when the socket was NAMED, which is what forbids a spawn.</returns>
    public static bool TryGetNamed(string? commandLineValue, out string socketPath)
    {
        foreach (var candidate in (ReadOnlySpan<string?>)[commandLineValue, Environment.GetEnvironmentVariable(EnvironmentVariable)])
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                socketPath = Path.GetFullPath(candidate);
                return true;
            }
        }

        socketPath = DefaultPath;
        return false;
    }

    /// <summary>
    /// An HTTP handler whose every connection goes to the node's socket: under an <see cref="HttpClient"/> with
    /// <see cref="BaseAddress"/> for requests, and under an <see cref="HttpMessageInvoker"/> for the event stream's
    /// WebSocket. The protocol above it is the native v1 API unchanged, so a local node and a remote rig are one
    /// client and one test surface.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(string socketPath) => new SocketsHttpHandler
    {
        ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}
