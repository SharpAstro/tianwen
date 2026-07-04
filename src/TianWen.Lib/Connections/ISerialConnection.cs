using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

public interface ISerialConnection : IDisposable
{
    internal const string SerialProto = "serial:";

    public static string RemoveProtoPrrefix(string portName)
    {
        if (portName.StartsWith(SerialProto, StringComparison.Ordinal))
        {
            return portName[SerialProto.Length..];
        }

        return portName;
    }

    public static string CleanupPortName(string portName)
    {
        var portNameWithoutPrefix = RemoveProtoPrrefix(portName);

        return portNameWithoutPrefix.StartsWith("tty", StringComparison.Ordinal) ? $"/dev/{portNameWithoutPrefix}" : portNameWithoutPrefix;
    }

    bool IsOpen { get; }

    Encoding Encoding { get; }

    /// <summary>
    /// When true, each TryWrite / TryRead exchange is logged at Info level with
    /// non-printables rendered as hex. Default false (traffic logged at Trace
    /// only). <see cref="Discovery.ISerialProbeService"/> sets this while probing
    /// so the operator can see the handshake without enabling Debug.
    /// Do NOT leave on during an active session — drivers poll dozens of times
    /// per second. Default-interface setter is a no-op so in-memory fakes don't
    /// need to carry a backing field.
    /// </summary>
    bool LogVerbose { get => false; set { } }

    /// <summary>
    /// Optional label included in the verbose log lines so mixed-probe traffic on a
    /// shared handle is self-identifying (e.g. <c>COM5 [OnStep] --> :GVP#</c>). The
    /// probe service sets this to the active probe's name around each handshake and
    /// clears it afterwards. Null or empty prints the untagged format. Default-interface
    /// setter is a no-op so in-memory fakes don't need a backing field.
    /// </summary>
    string? VerboseTag { get => null; set { } }

    /// <summary>
    /// When true, reads use a blocking, timeout-sliced <em>synchronous</em> path instead of async overlapped
    /// I/O. Some USB-serial bridges (notably CH34x) spuriously abort async <c>BaseStream</c> reads with
    /// <c>ERROR_OPERATION_ABORTED</c> ("the I/O operation has been aborted…") after the first read; blocking
    /// reads are immune. Still cancellable — the token is observed between short <c>ReadTimeout</c> slices, so
    /// no thread is abandoned. Default false; default-interface no-op setter so in-memory fakes ignore it.
    /// Opt in per connection (e.g. the Gemini FlatPanel driver, whose CH341 bridge triggers the abort).
    /// </summary>
    bool SynchronousReads { get => false; set { } }

    bool TryClose();

    /// <summary>
    /// Discards any bytes sitting in the receive buffer. Called by the probe service
    /// between probes on a shared handle so one protocol's response (or timed-out
    /// partial read) cannot contaminate the next probe's read. Default-interface
    /// implementation is a no-op for fakes and connections that don't buffer.
    /// </summary>
    void DiscardInBuffer() { }

    ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken);

    ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    ValueTask<bool> TryWriteAsync(string data, CancellationToken cancellationToken) => TryWriteAsync(Encoding.GetBytes(data), cancellationToken);

    ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken);

    ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    /// <param name="terminators"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>bytes read</returns>
    ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken);

    ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken);
}