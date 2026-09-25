using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using TianWen.Hosting.Api;

namespace TianWen.Hosting;

/// <summary>
/// What <c>tianwen-server</c> is told on its command line. Parsed here rather than through the host's configuration,
/// whose command-line provider reads every <c>--key</c> as taking the NEXT argument as its value: a bare
/// <c>--local-only --socket x</c> would set <c>local-only</c> to <c>--socket</c>.
/// </summary>
/// <param name="SocketPath">The socket to listen on, as a full path: <c>--socket</c>, else <see cref="NodeSocket.DefaultPath"/>.</param>
/// <param name="Port">The TCP port for the LAN, <c>--port</c> (1888).</param>
/// <param name="LocalOnly"><c>--local-only</c>: the socket and nothing else, so no other machine can reach the node
/// and it announces nothing.</param>
public sealed record NodeArguments(string SocketPath, int Port, bool LocalOnly)
{
    /// <summary>The port a node run by hand listens on for the LAN.</summary>
    public const int DefaultPort = 1888;

    /// <summary>The usage line an error ends with.</summary>
    public const string Usage = "usage: tianwen-server [--socket <path>] [--port <n>] [--local-only]";

    public static bool TryParse(ReadOnlySpan<string> args, [NotNullWhen(true)] out NodeArguments? parsed, [NotNullWhen(false)] out string? error)
    {
        string? socket = null;
        var port = DefaultPort;
        var localOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--local-only":
                    localOnly = true;
                    break;

                case "--socket" when i + 1 < args.Length:
                    socket = args[++i];
                    break;

                case "--port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
                    {
                        return Fail($"--port takes a port number, not {args[i]}", out parsed, out error);
                    }
                    break;

                default:
                    return Fail($"Unknown or incomplete argument {args[i]}", out parsed, out error);
            }
        }

        var socketPath = Path.GetFullPath(socket ?? NodeSocket.DefaultPath);
        if (!NodeSocket.TryValidate(socketPath, out var invalid))
        {
            return Fail(invalid, out parsed, out error);
        }

        parsed = new NodeArguments(socketPath, port, localOnly);
        error = null;
        return true;
    }

    private static bool Fail(string why, out NodeArguments? parsed, out string error)
    {
        parsed = null;
        error = why + Environment.NewLine + Usage;
        return false;
    }
}
