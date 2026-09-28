using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Imaging;
using TianWen.Lib.IO;

namespace TianWen.Hosting;

/// <summary>
/// Where this node makes the shared-memory sections a local client reads a stream's frames from (P4b of
/// docs/plans/hardware-in-the-server.md, #932): a prefix of its own, from the socket it holds, so two nodes on one machine
/// (a test's beside the user's) never touch each other's, and the directory a section lives in where there is no
/// <c>/dev/shm</c>.
/// </summary>
/// <remarks>
/// A host that registers none streams frames as bytes, as every node did before P4b; only a node that holds its socket's
/// lock registers one (<see cref="NodeSharedMemoryServiceCollectionExtensions.AddNodeSharedMemory"/>), and as it does it
/// removes what a node that died on the same socket left (Linux and macOS: a Windows section goes with its last holder).
/// </remarks>
public sealed class NodeSharedMemory
{
    internal NodeSharedMemory(string socketPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(socketPath)));
        Prefix = "tianwen-frames-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
        UnixDirectory = Path.GetDirectoryName(Path.GetFullPath(socketPath)) ?? Path.GetTempPath();
    }

    /// <summary>What every section this node makes is named after.</summary>
    public string Prefix { get; }

    /// <summary>Where a section lives on a system with no <c>/dev/shm</c>: the socket's own, per-user directory.</summary>
    public string UnixDirectory { get; }

    /// <summary>A stream's own writer, the stream's to dispose.</summary>
    public FrameSlotWriter CreateWriter() => new FrameSlotWriter(Prefix, UnixDirectory);
}

/// <summary>
/// The sections the node serves a source's frames from on request (an OTA's, the guider's, a planetary capture's through
/// <c>/frames/{source}/latest</c>), one per source, shared by every local client that asks for shared memory (P4b, #932).
/// A stream's frames go through a section of the stream's own instead (<see cref="NodeSharedMemory.CreateWriter"/>). The
/// sections live as long as the node, from the first client that asks for a source's frame this way.
/// </summary>
public sealed class NodeFrameSlots(NodeSharedMemory sharedMemory) : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Source> _sources =
        new System.Collections.Concurrent.ConcurrentDictionary<string, Source>(StringComparer.Ordinal);

    /// <summary>
    /// Writes <paramref name="frame"/>, the source's frame <paramref name="number"/>, into one of the source's two slots and
    /// answers where it is; a frame a slot holds already is answered from there. The caller keeps the frame, leased.
    /// </summary>
    public FrameSlot Write(string source, int number, Image frame)
    {
        var held = _sources.GetOrAdd(source, _ => new Source(sharedMemory.CreateWriter()));
        // Two local clients asking for one source's frame at once would otherwise write the same two slots together. Not
        // a CAS hand-off: a write moves the slot's generation, its length and its bytes as one. On a request's thread,
        // never a render thread, and a copy long (milliseconds for a full frame), so a Lock rather than a spin.
        lock (held.Gate)
        {
            ObjectDisposedException.ThrowIf(held.Disposed, this);
            return held.Writer.Write(number, frame);
        }
    }

    public void Dispose()
    {
        foreach (var source in _sources.Values)
        {
            lock (source.Gate)
            {
                source.Disposed = true;
                source.Writer.Dispose();
            }
        }
    }

    private sealed class Source(FrameSlotWriter writer)
    {
        public readonly System.Threading.Lock Gate = new System.Threading.Lock();
        public readonly FrameSlotWriter Writer = writer;
        public bool Disposed;
    }
}

public static class NodeSharedMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Lets this node carry a stream's frames to a client on its socket through shared memory (P4b), for a node that holds
    /// <paramref name="held"/>. Removes the sections a node that died on the same socket left: no other node writes under
    /// its prefix while this one holds the lock.
    /// </summary>
    public static IServiceCollection AddNodeSharedMemory(this IServiceCollection services, NodeLock held)
    {
        var sharedMemory = new NodeSharedMemory(held.SocketPath);
        SharedMemorySection.RemoveStale(sharedMemory.Prefix, sharedMemory.UnixDirectory);
        return services.AddSingleton(sharedMemory).AddSingleton<NodeFrameSlots>();
    }
}
