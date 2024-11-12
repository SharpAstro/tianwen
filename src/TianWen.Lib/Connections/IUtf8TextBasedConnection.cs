using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

public interface IUtf8TextBasedConnection : IDisposable
{
    CommunicationProtocol HighLevelProtocol { get; }

    ValueTask ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default);

    bool IsConnected { get; }

    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> WriteLineAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);
}
