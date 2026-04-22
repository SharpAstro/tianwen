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

    /// <inheritdoc />
    public bool LogVerbose { get; set; }

    /// <inheritdoc />
    public string? VerboseTag { get; set; }

    public ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken) => _semaphore.AcquireLockAsync(cancellationToken);

    /// <summary>
    /// Closes the serial port if it is open
    /// </summary>
    /// <returns>true if the prot is closed</returns>
    public virtual bool TryClose()
    {
        _semaphore.Dispose();
        return true;
    }

    /// <summary>
    /// Base no-op; concrete serial transports (e.g. <see cref="SerialConnection"/>)
    /// override this to discard the native receive buffer before the next probe
    /// sends a command on the shared handle.
    /// </summary>
    public virtual void DiscardInBuffer() { }

    public async ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        try
        {
            await _stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            if (LogVerbose)
            {
                var rendered = Encoding.GetString(message.Span).ReplaceNonPrintableWithHex();
                var tag = VerboseTag;
                if (!string.IsNullOrEmpty(tag))
                {
                    _logger.LogInformation("{Port} [{Tag}] --> {Message}", DisplayName, tag, rendered);
                }
                else
                {
                    _logger.LogInformation("{Port} --> {Message}", DisplayName, rendered);
                }
            }
            else
            {
                _logger.LogTrace("--> {Message}", Encoding.GetString(message.Span).ReplaceNonPrintableWithHex());
            }
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

            // Log with the terminator so e.g. LX200 "On-Step#" reads as the wire bytes.
            var responseForLog = terminatorIndex >= 0
                ? Encoding.GetString(message.Span[..(bytesRead + 1)])
                : Encoding.GetString(message.Span[..bytesRead]);
            if (LogVerbose)
            {
                var rendered = responseForLog.ReplaceNonPrintableWithHex();
                var tag = VerboseTag;
                if (!string.IsNullOrEmpty(tag))
                {
                    _logger.LogInformation("{Port} [{Tag}] <-- {Response}", DisplayName, tag, rendered);
                }
                else
                {
                    _logger.LogInformation("{Port} <-- {Response}", DisplayName, rendered);
                }
            }
            else
            {
                _logger.LogTrace("<-- {Response}", responseForLog);
            }
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
            // Try* contract: failures are signalled via the return value. The
            // catch-all here is dominated by "I/O aborted" from SerialStream.EndRead
            // when the port is closed mid-read (normal probe-timeout cleanup), and
            // by the caller's own cancellation. Keep the diagnostic at Debug so
            // logs stay readable during discovery, but when verbose probing is on
            // also emit a tagged Info line so the operator sees the "no response"
            // side of each handshake (otherwise the log shows only --> writes).
            if (LogVerbose)
            {
                var tag = VerboseTag;
                var reason = SanitizeReason(ex);
                if (!string.IsNullOrEmpty(tag))
                {
                    _logger.LogInformation("{Port} [{Tag}] <-- (no response: {ExceptionType}: {Reason})", DisplayName, tag, ex.GetType().Name, reason);
                }
                else
                {
                    _logger.LogInformation("{Port} <-- (no response: {ExceptionType}: {Reason})", DisplayName, ex.GetType().Name, reason);
                }
            }
            _logger.LogDebug(ex, "TryReadTerminatedRawAsync failed on {Port}", DisplayName);

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
            if (LogVerbose)
            {
                var rendered = Encoding.GetString(message.Span).ReplaceNonPrintableWithHex();
                var tag = VerboseTag;
                if (!string.IsNullOrEmpty(tag))
                {
                    _logger.LogInformation("{Port} [{Tag}] <-- {Response} ({Length})", DisplayName, tag, rendered, message.Length);
                }
                else
                {
                    _logger.LogInformation("{Port} <-- {Response} ({Length})", DisplayName, rendered, message.Length);
                }
            }
            else
            {
                _logger.LogTrace("<-- {Response} ({Length})", Encoding.GetString(message.Span), message.Length);
            }
            return true;
        }
        catch (Exception ex)
        {
            // See TryReadTerminatedRawAsync: Try* contract + normal probe-close semantics
            // means we report failure via the bool return; log body stays at Debug. When
            // verbose probing is on, also emit a tagged Info line so each --> write has
            // a matching <-- outcome in the operator log.
            if (LogVerbose)
            {
                var tag = VerboseTag;
                var reason = SanitizeReason(ex);
                if (!string.IsNullOrEmpty(tag))
                {
                    _logger.LogInformation("{Port} [{Tag}] <-- (no response: {ExceptionType}: {Reason})", DisplayName, tag, ex.GetType().Name, reason);
                }
                else
                {
                    _logger.LogInformation("{Port} <-- (no response: {ExceptionType}: {Reason})", DisplayName, ex.GetType().Name, reason);
                }
            }
            _logger.LogDebug(ex, "TryReadExactlyRawAsync failed on {Port}", DisplayName);

            return false;
        }
    }

    public void Dispose() => _ = TryClose();

    // Squash newlines/trailing whitespace — IOException.Message from SerialStream is
    // often multi-line ("The I/O operation has been aborted...\r\n"), which shreds
    // the one-line-per-exchange probe log.
    private static string SanitizeReason(Exception ex)
        => ex.Message.TrimEnd().Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
}
