using System.IO;
using System.Reflection;
using LAN.Lib;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>
/// Which node this is: the stable id minted once per machine and user, and the build. The id is the one the LAN
/// announcement carries (<see cref="IdFileName"/>, shared with LAN.Lib), so the Local rig reached over the socket
/// and the same rig reached over the LAN are one rig, with one binding.
/// </summary>
public sealed class NodeIdentity(string nodeId, string version)
{
    /// <summary>The id's file under the AppData root, which the LAN announcement reads as well.</summary>
    public const string IdFileName = "lan-node-id.txt";

    public string NodeId { get; } = nodeId;

    /// <summary>The node's informational version (the build it is), for display. Compatibility is the wire version.</summary>
    public string Version { get; } = version;

    /// <summary>Where the id is kept: the AppData root this node runs under, a test's temp folder in a test.</summary>
    public static string IdFilePath(IExternal external) => Path.Combine(external.AppDataFolder.FullName, IdFileName);

    /// <summary>
    /// Loads the node's id, minting and keeping it on the first start. LAN.Lib's rule, not a copy of it: the
    /// announcement then reads the file this wrote and carries the same id.
    /// </summary>
    public static NodeIdentity Load(IExternal external) => new NodeIdentity(
        LanIdentity.Create(IdFilePath(external)).NodeId,
        typeof(NodeIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
}

/// <summary>
/// Where the node listens: its socket, and a TCP port when it is reachable from the LAN. Registered by whatever
/// hosts the node; a host that registers none listens on neither as far as <c>GET /api/v1/node</c> is concerned.
/// </summary>
public sealed record NodeListening(string? SocketPath, int? LanPort)
{
    /// <summary>Whether another machine can reach the node.</summary>
    public bool IsShared => LanPort is not null;
}
