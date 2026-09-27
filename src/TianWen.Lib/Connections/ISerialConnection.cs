using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

public interface ISerialConnection : IAsyncDisposable
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
    /// Do NOT leave on during an active session: drivers poll dozens of times
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
    /// True once an I/O on this connection had to be abandoned: a deadline or token fired while the operation
    /// was still pending, so the DRIVER never completed it -- cancellation was observed by the caller, not by
    /// the port. That is the difference between a device that did not answer (retry it, try the next
    /// protocol) and a port that does not complete I/O at all (give it up: every further exchange on it
    /// costs a full budget and every close of it a stuck thread). The Bluetooth listener port Windows creates
    /// for any paired device advertising the Serial Port Profile is the case that found this. Default false;
    /// in-memory fakes never abandon anything.
    /// </summary>
    bool HasAbandonedIo => false;

    /// <summary>
    /// Closes the connection. Asynchronous because a close can have to wait for the driver to give up I/O it is
    /// holding; bounded by the transport, so it always returns. False when the close had to be abandoned (the next
    /// open of the port may find it busy).
    /// </summary>
    ValueTask<bool> TryCloseAsync();

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