using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

public interface ISerialConnection : IDisposable
{
    internal const string SerialProto = "serial:";

    bool IsOpen { get; }

    Encoding Encoding { get; }

    bool TryClose();

    Task WaitAsync(CancellationToken cancellationToken);

    int Release();

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