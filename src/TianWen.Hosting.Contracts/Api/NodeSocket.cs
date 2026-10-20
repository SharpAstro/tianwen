using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib;

namespace TianWen.Hosting.Api;

/// <summary>
/// Where this machine's node listens: a Unix domain socket under the per-user AppData root, which is how every
/// client on the machine reaches it (docs/plans/hardware-in-the-server.md, "Transport"). One helper, shared by
/// the node and its clients, so the two can never look in different places. tianwen's names and its own default path
/// (beside the data root, or a short hashed one); the generic pieces (the address limit, the lock path, the socket's HTTP
/// handler, the listening probe) are Node.Lib's <see cref="global::Node.Lib.NodeSocketLocation"/>, shared with pilot.
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
    /// restarted after a crash finds the node that outlived it here: <c>node.sock</c> under the data root, or, where that
    /// path is too long for a socket address, the short one <see cref="DefaultPathFor"/> gives.
    /// </summary>
    public static string DefaultPath { get; } = DefaultPathFor(TianWenDataRoot.Directory.FullName,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "TianWen", ShortFolderName));

    /// <summary>The folder under the user's own TianWen data folder that holds a socket whose data root is too deep for one.</summary>
    internal const string ShortFolderName = "s";

    /// <summary>
    /// The socket for the data root <paramref name="dataRoot"/>: <c>node.sock</c> beside its data when that path fits a socket
    /// address (<see cref="MaxPathBytes"/>), which the user's own data root does. A root kept apart for a test or a screenshot
    /// can be too deep for one (a scratch folder under %TEMP% was 183 bytes against 107), so its socket goes into
    /// <paramref name="shortFolder"/>, under the user's own data folder and so behind the same ACL, named for the root by a
    /// hash: a node and its clients, given the same root, compute the same path, and two roots never share a socket. The
    /// lock and the crash journal are named after the socket and follow it there. A path that fits nowhere is returned as it
    /// is, and <see cref="TryValidate"/> refuses it by name.
    /// </summary>
    internal static string DefaultPathFor(string dataRoot, string shortFolder)
    {
        var beside = Path.Combine(dataRoot, DefaultFileName);
        if (Encoding.UTF8.GetByteCount(beside) <= MaxPathBytes)
        {
            return beside;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        // Windows names a folder case-blind, so one root written two ways is one socket.
        var key = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 6).ToLowerInvariant();
        var shortPath = Path.Combine(shortFolder, $"{hash}.sock");
        if (Encoding.UTF8.GetByteCount(shortPath) > MaxPathBytes)
        {
            return beside;
        }
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(shortFolder);
        }
        else
        {
            Directory.CreateDirectory(shortFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return shortPath;
    }

    /// <summary>
    /// The address a request over the socket carries. Only its host reaches the node, as the <c>Host</c> header;
    /// the connection itself goes to the socket (<see cref="CreateHandler"/>).
    /// </summary>
    public static Uri BaseAddress => global::Node.Lib.NodeSocketLocation.BaseAddress;

    /// <summary>
    /// The longest socket path this system can bind, in bytes: <c>sun_path</c> holds 108 on Linux and Windows and
    /// 104 on macOS and the BSDs, one of which is the terminator.
    /// </summary>
    internal static int MaxPathBytes => global::Node.Lib.NodeSocketLocation.MaxPathBytes;

    /// <summary>
    /// The lock beside a socket, which admits one node to it: <c>node.sock</c> is guarded by <c>node.lock</c>. Named
    /// after its socket, so a test's node on a socket of its own takes a lock of its own.
    /// </summary>
    public static string LockPathFor(string socketPath) => global::Node.Lib.NodeSocketLocation.LockPathFor(socketPath);

    /// <summary>
    /// Whether <paramref name="socketPath"/> fits in a socket address. A path that does not fit is refused with a
    /// message naming it, rather than bound truncated, which would put the node somewhere no client looks.
    /// </summary>
    public static bool TryValidate(string socketPath, [NotNullWhen(false)] out string? error) =>
        global::Node.Lib.NodeSocketLocation.TryValidate(socketPath, out error);

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
    public static SocketsHttpHandler CreateHandler(string socketPath) => global::Node.Lib.NodeSocketLocation.CreateHandler(socketPath);

    /// <summary>
    /// Whether anything accepts a connection on <paramref name="socketPath"/>: a node that is up, however slow to answer a
    /// request. False when there is no socket file, or only a stale one nothing listens on, which is refused at once, and
    /// for a path too long to be a socket address at all, where <see cref="UnixDomainSocketEndPoint"/> throws
    /// <see cref="ArgumentOutOfRangeException"/> (nothing can listen on an address that cannot be formed).
    /// </summary>
    public static Task<bool> IsListeningAsync(string socketPath, CancellationToken cancellationToken) =>
        global::Node.Lib.NodeSocketLocation.IsListeningAsync(socketPath, cancellationToken);
}
