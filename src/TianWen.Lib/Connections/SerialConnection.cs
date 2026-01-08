using CommunityToolkit.HighPerformance;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

internal sealed class SerialConnection : ISerialConnection
{
    public static IReadOnlyList<string> EnumerateSerialPorts()
    {
        var portNames = SerialPort.GetPortNames();

        var prefixedPortNames = new List<string>(portNames.Length);
        for (var i = 0; i < portNames.Length; i++)
        {
            prefixedPortNames.Add($"{ISerialConnection.SerialProto}{portNames[i]}");
        }

        return prefixedPortNames;
    }

    public static string CleanupPortName(string portName)
    {
        var portNameWithoutPrefix = portName.StartsWith(ISerialConnection.SerialProto, StringComparison.Ordinal) ? portName[ISerialConnection.SerialProto.Length..] : portName;

        return portNameWithoutPrefix.StartsWith("tty", StringComparison.Ordinal) ? $"/dev/{portNameWithoutPrefix}" : portNameWithoutPrefix;
    }

    private readonly SerialPort _port;
    private readonly Stream _stream;
    private readonly ILogger _logger;

    public SerialConnection(string portName, int baud, ILogger logger, Encoding encoding, TimeSpan? ioTimeout = null)
    {
        _port = new SerialPort(CleanupPortName(portName), baud);
        _port.Open();

        var timeoutMs = (int)Math.Round((ioTimeout ?? TimeSpan.FromMilliseconds(500)).TotalMilliseconds);
        _stream = _port.BaseStream;
        _stream.ReadTimeout = timeoutMs;
        _stream.WriteTimeout = timeoutMs;

        _logger = logger;
        Encoding = encoding;
    }

    public bool IsOpen => _port.IsOpen;

    /// <summary>
    /// Encoding used for decoding byte messages (used for display/logging only)
    /// </summary>
    public Encoding Encoding { get; }

    /// <summary>
    /// Closes the serial port if it is open
    /// </summary>
    /// <returns>true if the prot is closed</returns>
    public bool TryClose()
    {
        if (_port.IsOpen)
        {
            _port.Close();
        }
        return !_port.IsOpen;
    }

    public async ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        try
        {
            await _stream.WriteAsync(message, cancellationToken);
#if DEBUG
            _logger.LogDebug("--> {Message}", Encoding.GetString(message.Span));
#endif
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while sending message {Message} to serial device on port {Port}",
                Encoding.GetString(message.Span), _port.PortName);

            return false;
        }

        return true;
    }

    public async ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(100);
        try
        {
            var bytesRead = await TryReadTerminatedRawAsync(buffer, terminators, cancellationToken);
            if (bytesRead >= 0)
            {
                var message = Encoding.GetString(buffer.AsSpan(0, bytesRead));

                return message;
            }
            else
            {
                return null;
            }
        }   
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        int bytesRead = 0;
        try
        {
            int bytesReadLast;
            do
            {
                bytesReadLast = await _stream.ReadAtLeastAsync(message[bytesRead..], 1, true, cancellationToken);
                bytesRead += bytesReadLast;
            } while (!ContainsTerminator(message.Slice(bytesRead - bytesReadLast, bytesRead).Span));

#if DEBUG
            _logger.LogTrace("<-- (terminated by any of {Terminators}): {Response}", Encoding.GetString(terminators.Span), message);
#endif
            // return length without the terminator
            return bytesRead - bytesReadLast - 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while reading response from serial device on port {Port}", _port.PortName);

            return -1;
        }

        bool ContainsTerminator(ReadOnlySpan<byte> haystack)
        {
            if (terminators.Span.Length is 1)
            {
                return haystack.Contains(terminators.Span[0]);
            }

            foreach (var terminator in terminators.Span)
            {
                if (haystack.Contains(terminator))
                {
                    return true;
                }
            }
            return false;
        }
    }

    public async ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            if (await TryReadExactlyRawAsync(buffer, cancellationToken))
            {

                var message = Encoding.GetString(buffer);

                return message;
            }
            else
            {
                return null;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken)
    {
        try
        {
            await _stream.ReadExactlyAsync(message, cancellationToken);
#if DEBUG
            _logger.LogTrace("<-- (exactly {Count}): {Response}", message.Length, Encoding.GetString(message.Span));
#endif
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while reading response from serial device on port {Port}", _port.PortName);

            return false;
        }
    }

    public void Dispose() => _ = TryClose();
}