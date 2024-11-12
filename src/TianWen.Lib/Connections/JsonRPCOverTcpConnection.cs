/*

MIT License

Copyright (c) 2018 Andy Galasso

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

internal class JsonRPCOverTcpConnection() : IUtf8TextBasedConnection
{
    private TcpClient? _tcpClient;
    private StreamReader? _streamReader;

    public async ValueTask ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        _tcpClient = new TcpClient();
        if (endPoint is IPEndPoint ip)
        {
            await _tcpClient.ConnectAsync(ip, cancellationToken).ConfigureAwait(false);
        }
        else if (endPoint is DnsEndPoint dns)
        {
            await _tcpClient.ConnectAsync(dns.Host, dns.Port, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentException($"{endPoint} address familiy {endPoint.AddressFamily} is not supported", nameof(endPoint));
        }

        _streamReader = new StreamReader(_tcpClient.GetStream());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        Dispose(true);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _streamReader?.Close();
            _streamReader = null;

            _tcpClient?.Close();
            _tcpClient = null;
        }
    }

    public bool IsConnected => _tcpClient?.Connected is true;

    public CommunicationProtocol HighLevelProtocol => CommunicationProtocol.JsonRPC;

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        => _streamReader is { } sr ? sr.ReadLineAsync(cancellationToken) : ValueTask.FromResult(null as string);

    static readonly ReadOnlyMemory<byte> CRLF = "\r\n"u8.ToArray();

    public async ValueTask<bool> WriteLineAsync(ReadOnlyMemory<byte> jsonlUtf8Bytes, CancellationToken cancellationToken = default)
    {
        if (_tcpClient?.GetStream() is { CanWrite: true } stream)
        {
            await stream.WriteAsync(jsonlUtf8Bytes, cancellationToken);
            await stream.WriteAsync(CRLF, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return true;
        }

        return false;
    }
}
