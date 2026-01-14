using CommunityToolkit.HighPerformance;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

internal abstract class SerialConnectionBase : ISerialConnection
{
    private readonly SemaphoreSlim _semaphore;
    private readonly Stream _stream;
    private readonly ILogger _logger;

    public SerialConnectionBase(Encoding encoding, ILogger logger)
    {
        _stream = OpenStream();
        _semaphore = new SemaphoreSlim(1, 1);

        _logger = logger;
        Encoding = encoding;
    }

    protected abstract Stream OpenStream();

    public abstract bool IsOpen { get; }

    public abstract string DisplayName { get; }

    /// <summary>
    /// Encoding used for decoding byte messages (used for display/logging only)
    /// </summary>
    public Encoding Encoding { get; }

    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    public int Release() => _semaphore.Release();

    /// <summary>
    /// Closes the serial port if it is open
    /// </summary>
    /// <returns>true if the prot is closed</returns>
    public virtual bool TryClose()
    {
        _semaphore.Dispose();
        return true;
    }

    public async ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        try
        {
            await _stream.WriteAsync(message, cancellationToken);
#if DEBUG
            _logger.LogTrace("--> {Message}", Encoding.GetString(message.Span).ReplaceNonPrintableWithHex());
#endif
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while sending message {Message} to serial device on serial port {Port}",
                Encoding.GetString(message.Span), DisplayName);

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
        int terminatorIndex;
        try
        {
            int bytesReadLast;
            do
            {
                bytesReadLast = await _stream.ReadAtLeastAsync(message[bytesRead..], 1, true, cancellationToken);
                terminatorIndex = message.Slice(bytesRead, bytesReadLast).Span.IndexOfAny(terminators.Span);

                if (terminatorIndex < 0)
                {
                    bytesRead += bytesReadLast;
                }
                else
                {
                    bytesRead += terminatorIndex;
                    break;
                }
            } while (bytesRead < message.Length);

#if DEBUG
            // output log including the terminator
            var decodedResponseMsg = Encoding.GetString(message.Span[..(bytesRead+1)]);
            _logger.LogTrace("<-- {Response}", decodedResponseMsg);
#endif
            if (terminatorIndex < 0)
            {
                _logger.LogWarning("Terminator (any of {Terminators}) not found in message from serial device on serial port {Port}",
                    Encoding.GetString(terminators.Span).ReplaceNonPrintableWithHex(),
                    DisplayName);
                return -1;
            }

            // return length without the terminator
            return bytesRead;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while reading response from serial device on serial port {Port}", DisplayName);

            return -1;
        }
    }

    public async ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            if (await TryReadExactlyRawAsync(buffer.AsMemory(0, count), cancellationToken))
            {
                return Encoding.GetString(buffer.AsSpan(0, count));
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
            _logger.LogTrace("<-- {Response} ({Length})", Encoding.GetString(message.Span), message.Length);
#endif
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while reading response from serial device on serial port {Port}", DisplayName);

            return false;
        }
    }

    public void Dispose() => _ = TryClose();
}
