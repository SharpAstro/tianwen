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

    bool TryClose();

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