using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib;

namespace TianWen.Lib.Connections;

/// <summary>
/// <see cref="ISerialConnection"/> wrapping a TCP socket. Used for WiFi/Ethernet
/// devices that expose a "transparent serial" TCP port (OnStep ESP32 SmartHand
/// Controller on TCP/9999, network-attached focusers, etc.).
///
/// Unlike <see cref="Skywatcher.SkywatcherUdpConnection"/> which gets one message
/// per datagram for free, TCP is a byte stream — we buffer until we hit a
/// terminator or read the requested exact byte count.
///
/// Construct via <see cref="CreateAsync"/> so the TCP connect is truly async
/// and cancellable — no thread pool thread is blocked during the ~100 ms–3 s
/// handshake.
/// </summary>
internal sealed class TcpSerialConnection : ISerialConnection
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(3);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // Read buffer for terminator search. Sized for typical LX200 responses
    // (longest seen ~30 bytes for :GU# status word). Grows on demand.
    private byte[] _readBuffer = new byte[256];
    private int _readBufferStart;
    private int _readBufferEnd;

    private TcpSerialConnection(TcpClient client, NetworkStream stream, IPEndPoint remoteEndPoint, Encoding encoding, ILogger logger)
    {
        _client = client;
        _stream = stream;
        _remoteEndPoint = remoteEndPoint;
        Encoding = encoding;
        _logger = logger;
        IsOpen = true;
    }

    /// <summary>
    /// Opens a TCP connection asynchronously. Resolves DNS names (e.g. <c>onstep.local</c>)
    /// cooperatively, awaits <see cref="TcpClient.ConnectAsync(IPAddress, int)"/> with a
    /// cancellable timeout, and never blocks a thread pool thread.
    /// </summary>
    public static async ValueTask<TcpSerialConnection> CreateAsync(
        string host,
        int port,
        Encoding encoding,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        IPAddress ipAddress;
        if (IPAddress.TryParse(host, out var parsed))
        {
            ipAddress = parsed;
        }
        else
        {
            // Cooperative DNS resolution for mDNS hostnames / user-supplied names.
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            if (addresses.Length is 0)
            {
                throw new InvalidOperationException($"Failed to resolve host '{host}'");
            }
            ipAddress = addresses[0];
        }

        var remoteEndPoint = new IPEndPoint(ipAddress, port);
        var client = new TcpClient { NoDelay = true };

        try
        {
            using var timeoutCts = new CancellationTokenSource(DefaultConnectTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await client.ConnectAsync(remoteEndPoint.Address, remoteEndPoint.Port, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout (not caller cancellation) — surface as a clearer error.
            client.Dispose();
            throw new TimeoutException($"TCP connect to {remoteEndPoint} timed out after {DefaultConnectTimeout.TotalSeconds:F0}s");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var stream = client.GetStream();
        stream.ReadTimeout = 2000;
        stream.WriteTimeout = 2000;
        return new TcpSerialConnection(client, stream, remoteEndPoint, encoding, logger);
    }

    public bool IsOpen { get; private set; }

    public Encoding Encoding { get; }

    public bool TryClose()
    {
        IsOpen = false;
        try
        {
            _stream.Dispose();
            _client.Dispose();
        }
        catch
        {
            // best-effort close — caller doesn't care about cleanup faults
        }
        return true;
    }

    public void Dispose() => TryClose();

    public ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken) => _semaphore.AcquireLockAsync(cancellationToken);

    public async ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            return false;
        }

        try
        {
            await _stream.WriteAsync(data, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "TCP write failed to {EndPoint}", _remoteEndPoint);
            IsOpen = false;
            return false;
        }
    }

    public async ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            return null;
        }

        try
        {
            // Walk the buffer for any terminator; fill from the socket as needed.
            while (true)
            {
                for (var i = _readBufferStart; i < _readBufferEnd; i++)
                {
                    if (terminators.Span.IndexOf(_readBuffer[i]) >= 0)
                    {
                        var len = i - _readBufferStart;
                        var result = Encoding.GetString(_readBuffer, _readBufferStart, len);
                        _readBufferStart = i + 1; // consume terminator
                        CompactBufferIfEmpty();
                        return result;
                    }
                }

                if (!await FillBufferAsync(cancellationToken))
                {
                    return null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "TCP read (terminated) failed from {EndPoint}", _remoteEndPoint);
            IsOpen = false;
            return null;
        }
    }

    public async ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        var str = await TryReadTerminatedAsync(terminators, cancellationToken);
        if (str is null)
        {
            return -1;
        }
        return Encoding.GetBytes(str, message.Span);
    }

    public async ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            return null;
        }

        try
        {
            // Make sure the buffer has enough capacity, then fill until we have `count` bytes available.
            EnsureBufferCapacity(count);

            while (_readBufferEnd - _readBufferStart < count)
            {
                if (!await FillBufferAsync(cancellationToken))
                {
                    return null;
                }
            }

            var result = Encoding.GetString(_readBuffer, _readBufferStart, count);
            _readBufferStart += count;
            CompactBufferIfEmpty();
            return result;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "TCP read (exact) failed from {EndPoint}", _remoteEndPoint);
            IsOpen = false;
            return null;
        }
    }

    public async ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken)
    {
        var str = await TryReadExactlyAsync(message.Length, cancellationToken);
        if (str is null)
        {
            return false;
        }
        return Encoding.GetBytes(str, message.Span) == message.Length;
    }

    /// <summary>
    /// Reads more bytes from the socket into the buffer. Compacts first if the
    /// unread region has slid up against the buffer end.
    /// </summary>
    private async ValueTask<bool> FillBufferAsync(CancellationToken cancellationToken)
    {
        // If we've consumed the front of the buffer, compact to make room.
        if (_readBufferStart > 0)
        {
            var unread = _readBufferEnd - _readBufferStart;
            if (unread > 0)
            {
                Buffer.BlockCopy(_readBuffer, _readBufferStart, _readBuffer, 0, unread);
            }
            _readBufferStart = 0;
            _readBufferEnd = unread;
        }

        // Grow if we're already at capacity (rare — only for huge unparsed responses).
        if (_readBufferEnd == _readBuffer.Length)
        {
            Array.Resize(ref _readBuffer, _readBuffer.Length * 2);
        }

        var n = await _stream.ReadAsync(_readBuffer.AsMemory(_readBufferEnd), cancellationToken);
        if (n <= 0)
        {
            // Peer closed the connection.
            IsOpen = false;
            return false;
        }

        _readBufferEnd += n;
        return true;
    }

    private void EnsureBufferCapacity(int required)
    {
        if (required > _readBuffer.Length)
        {
            // Grow to next power of two ≥ required, capped at 64 KiB to avoid runaway.
            var newSize = _readBuffer.Length;
            while (newSize < required && newSize < 65536)
            {
                newSize *= 2;
            }
            Array.Resize(ref _readBuffer, Math.Max(newSize, required));
        }
    }

    private void CompactBufferIfEmpty()
    {
        if (_readBufferStart == _readBufferEnd)
        {
            _readBufferStart = 0;
            _readBufferEnd = 0;
        }
    }
}
