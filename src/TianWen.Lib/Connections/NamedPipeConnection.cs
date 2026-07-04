using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

/// <summary>
/// An <see cref="IUtf8TextBasedConnection"/> over an already-connected duplex <see cref="PipeStream"/>
/// (a named pipe). Same line framing (one CRLF-terminated JSON message per line) as
/// <see cref="JsonRpcOverTcpConnection"/>, but over Windows local IPC — no network stack, no loopback
/// port, no firewall/AV involvement, and an ACL scoped to the current user. The out-of-process ASCOM
/// host (<c>tianwen-ascomhost</c>) serves over this. The pipe is connected by the caller (server-side
/// <see cref="PipeStream.WaitForConnection"/> or client-side <see cref="NamedPipeClientStream.Connect(int)"/>),
/// so <see cref="ConnectAsync"/> is not used.
/// </summary>
internal sealed class NamedPipeConnection(PipeStream pipe) : IUtf8TextBasedConnection
{
    private static readonly ReadOnlyMemory<byte> CRLF = "\r\n"u8.ToArray();

    private readonly StreamReader _reader = new(pipe);

    public CommunicationProtocol HighLevelProtocol => CommunicationProtocol.JsonRPC;

    public bool IsConnected => pipe.IsConnected;

    public ValueTask ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NamedPipeConnection wraps an already-connected pipe.");

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        => _reader.ReadLineAsync(cancellationToken);

    public async ValueTask<bool> WriteLineAsync(ReadOnlyMemory<byte> jsonlUtf8Bytes, CancellationToken cancellationToken = default)
    {
        if (!pipe.CanWrite)
        {
            return false;
        }

        await pipe.WriteAsync(jsonlUtf8Bytes, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(CRLF, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public void Dispose()
    {
        _reader.Dispose();
        pipe.Dispose();
    }
}
