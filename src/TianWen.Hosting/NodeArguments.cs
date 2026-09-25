using System;
using System.Collections.Generic;
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
/// <param name="Keeper"><c>--keeper</c>: this process is the node's KEEPER, not the node. It starts the node, waits
/// on it, and starts it again after it crashes (<see cref="NodeKeeper"/>).</param>
/// <param name="Spawned"><c>--spawned</c>: a client started this node, through its keeper. It has no terminal to
/// write to, so it logs to its file only.</param>
/// <param name="FakeDevicesOnly"><c>--fake-devices</c>: the fake device source and no other, so the node touches no
/// hardware: a node for tests and demonstrations.</param>
public sealed record NodeArguments(string SocketPath, int Port, bool LocalOnly, bool Keeper, bool Spawned, bool FakeDevicesOnly)
{
    /// <summary>The port a node run by hand listens on for the LAN.</summary>
    public const int DefaultPort = NodeWire.LanPort;

    /// <summary>The usage line an error ends with.</summary>
    public const string Usage = "usage: tianwen-server [--socket <path>] [--port <n>] [--local-only] [--fake-devices] [--keeper]";

    public static bool TryParse(ReadOnlySpan<string> args, [NotNullWhen(true)] out NodeArguments? parsed, [NotNullWhen(false)] out string? error)
    {
        string? socket = null;
        var port = DefaultPort;
        bool localOnly = false, keeper = false, spawned = false, fakeDevicesOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--local-only":
                    localOnly = true;
                    break;

                case "--keeper":
                    keeper = true;
                    break;

                case "--spawned":
                    spawned = true;
                    break;

                case "--fake-devices":
                    fakeDevicesOnly = true;
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

        parsed = new NodeArguments(socketPath, port, localOnly, keeper, spawned, fakeDevicesOnly);
        error = null;
        return true;
    }

    /// <summary>
    /// The command line a keeper starts its node with: everything it was told itself, as the node reads it, less
    /// <c>--keeper</c> and with <c>--spawned</c>.
    /// </summary>
    public IReadOnlyList<string> ForTheNode()
    {
        var args = new List<string> { "--socket", SocketPath, "--port", Port.ToString(CultureInfo.InvariantCulture), "--spawned" };
        if (LocalOnly)
        {
            args.Add("--local-only");
        }
        if (FakeDevicesOnly)
        {
            args.Add("--fake-devices");
        }
        return args;
    }

    private static bool Fail(string why, out NodeArguments? parsed, out string error)
    {
        parsed = null;
        error = why + Environment.NewLine + Usage;
        return false;
    }
}
