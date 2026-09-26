using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Lib.Devices;
using TianWen.Lib.IO;

namespace TianWen.Hosting;

/// <summary>
/// The node's machine settings, kept in the data root and read at every start: today only "Share this rig on the
/// LAN" (docs/plans/hardware-in-the-server.md, decision 3). A MACHINE setting, not a profile's or a session's, since it
/// decides whether the machine can be reached, and which user profile is active has no bearing on that.
/// </summary>
public sealed record NodeSettings(bool ShareOnLan)
{
    public const string FileName = "node-settings.json";

    public static NodeSettings Default { get; } = new NodeSettings(ShareOnLan: false);

    public static string PathIn(DirectoryInfo dataRoot) => Path.Combine(dataRoot.FullName, FileName);

    /// <summary>The settings under <paramref name="dataRoot"/>, or the defaults when there are none or they cannot be read.</summary>
    public static async Task<NodeSettings> LoadAsync(DirectoryInfo dataRoot, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await SharedFile.TryOpenReadAsync(PathIn(dataRoot), cancellationToken).ConfigureAwait(false);
            return stream is null
                ? Default
                : await JsonSerializer.DeserializeAsync(stream, NodeSettingsJsonContext.Default.NodeSettings, cancellationToken).ConfigureAwait(false) ?? Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }
}

/// <summary>
/// The node's settings as they stand, read at start and changed over the socket: written through
/// <see cref="IExternal.AtomicWriteJsonAsync"/>, since every file in the data root has more than one process on it.
/// </summary>
public sealed class NodeSettingsStore(IExternal external, NodeSettings initial)
{
    private NodeSettings _current = initial;

    public NodeSettings Current => Volatile.Read(ref _current);

    public async Task SaveAsync(NodeSettings settings, CancellationToken cancellationToken)
    {
        await external.AtomicWriteJsonAsync(NodeSettings.PathIn(external.AppDataFolder), settings, NodeSettingsJsonContext.Default.NodeSettings, cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(ref _current, settings);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(NodeSettings))]
internal partial class NodeSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// How this node was started, and how it will end: a node a client started (through its keeper) applies the share
/// setting and, when idle, restarts to apply a change of it; <see cref="ExitCode"/> is what the process returns.
/// </summary>
public sealed class NodeRole(bool spawned)
{
    public bool Spawned { get; } = spawned;

    /// <summary>What the process exits with once the host has stopped: <see cref="NodeExitCodes.Stopped"/> unless something asked for a restart.</summary>
    public int ExitCode { get; set; } = NodeExitCodes.Stopped;
}

/// <summary>Where the node listens, decided from how it was started and the machine's settings.</summary>
public static class NodeListeningDecision
{
    /// <summary>
    /// The socket always. TCP (the LAN) unless <c>--local-only</c>: a node run by hand keeps it, as a mini PC's does;
    /// a node a client started takes it only while the rig is shared (decision 3), so a GUI on a laptop never opens a
    /// port or announces a rig nobody asked to share.
    /// </summary>
    public static NodeListening For(NodeArguments node, NodeSettings settings) =>
        new NodeListening(node.SocketPath, node.LocalOnly || (node.Spawned && !settings.ShareOnLan) ? null : node.Port);
}
