using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Skywatcher;

/// <summary>
/// ISerialConnection wrapping a UdpClient for Skywatcher WiFi mounts (UDP port 11880).
/// One datagram per command, one datagram per response.
/// </summary>
internal sealed class SkywatcherUdpConnection : ISerialConnection
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public SkywatcherUdpConnection(string host, int port, Encoding encoding, ILogger logger)
    {
        Encoding = encoding;
        _logger = logger;
        _remoteEndPoint = new IPEndPoint(IPAddress.Parse(host), port);
        _client = new UdpClient();
        _client.Connect(_remoteEndPoint);
        _client.Client.ReceiveTimeout = 2000;
        IsOpen = true;
    }

    public bool IsOpen { get; private set; }
    public Encoding Encoding { get; }

    public bool TryClose()
    {
        IsOpen = false;
        _client.Dispose();
        return true;
    }

    public void Dispose() => TryClose();

    public ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken) => _semaphore.AcquireLockAsync(cancellationToken);

    public async ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!IsOpen) return false;
        try
        {
            await _client.SendAsync(data, cancellationToken);
            return true;
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "UDP write failed to {EndPoint}", _remoteEndPoint);
            return false;
        }
    }

    public async ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        if (!IsOpen) return null;
        try
        {
            var result = await _client.ReceiveAsync(cancellationToken);
            var response = Encoding.GetString(result.Buffer);
            // Strip terminator if present
            var terminatorChars = Encoding.GetString(terminators.Span);
            var endIdx = response.IndexOfAny(terminatorChars.ToCharArray());
            return endIdx >= 0 ? response[..endIdx] : response;
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "UDP read failed from {EndPoint}", _remoteEndPoint);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        if (!IsOpen) return null;
        try
        {
            var result = await _client.ReceiveAsync(cancellationToken);
            var response = Encoding.GetString(result.Buffer);
            if (response.Length < count)
            {
                _logger.LogWarning("UDP read: expected {Expected} chars, got {Actual} from {EndPoint}", count, response.Length, _remoteEndPoint);
                return null;
            }
            return response[..count];
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "UDP read failed from {EndPoint}", _remoteEndPoint);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        var str = await TryReadTerminatedAsync(terminators, cancellationToken);
        if (str is null) return -1;
        return Encoding.GetBytes(str, message.Span);
    }

    public async ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken)
    {
        var str = await TryReadExactlyAsync(message.Length, cancellationToken);
        if (str is null) return false;
        return Encoding.GetBytes(str, message.Span) == message.Length;
    }
}
