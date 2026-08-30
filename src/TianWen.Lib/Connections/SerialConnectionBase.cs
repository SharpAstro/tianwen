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
    protected readonly ILogger _logger;

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

    /// <inheritdoc />
    public bool SynchronousReads { get; set; }

    private int _abandonedIo;

    /// <inheritdoc />
    public bool HasAbandonedIo => Volatile.Read(ref _abandonedIo) != 0;

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
    /// sends a command on the shared handle. Overrides should read any pending
    /// bytes first (via <see cref="LogDrained"/>) so the operator can see what
    /// the device actually sent, e.g. OnStep's unterminated "0" left over from
    /// a prior LX200 probe's timeout, before the bytes are discarded.
    /// </summary>
    public virtual void DiscardInBuffer() { }

    /// <summary>
    /// Emits a one-line-per-drain Info entry describing bytes pulled from the
    /// receive buffer by <see cref="DiscardInBuffer"/>. Called by concrete
    /// transports before they issue the native discard. Silent when the buffer
    /// is empty or verbose probe logging is off.
    /// </summary>
    protected void LogDrained(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || !LogVerbose) return;

        var rendered = Encoding.GetString(bytes).ReplaceNonPrintableWithHex();
        var tag = VerboseTag;
        if (!string.IsNullOrEmpty(tag))
        {
            _logger.LogInformation("{Port} [{Tag}] <-- (drained {Count} byte(s): {Bytes})", DisplayName, tag, bytes.Length, rendered);
        }
        else
        {
            _logger.LogInformation("{Port} <-- (drained {Count} byte(s): {Bytes})", DisplayName, bytes.Length, rendered);
        }
    }

    /// <summary>
    /// A write to a healthy port completes in milliseconds (handshaking is never enabled, so the driver's
    /// buffer absorbs a command outright), yet <c>SerialPort.WriteTimeout</c> defaults to infinite, and on a
    /// "Standard Serial over Bluetooth link" port with nobody on the far end (the INCOMING port Windows
    /// creates for every paired device that advertises the Serial Port Profile, headphones included) the
    /// overlapped <c>WriteFile</c> simply never completes. <c>SerialStream.WriteAsync</c> does not observe its
    /// cancellation token, so no probe budget could end it either: discovery wedged there for good, and a
    /// <c>dotnet-stack</c> dump showed NO thread in serial I/O at all (a pending overlapped write is invisible
    /// to a stack dump; the first probe on that port never logged its <c>--&gt;</c> line, which is written
    /// after the await). Bounded twice, both with this value: the port's own <c>WriteTimeout</c>
    /// (<c>SerialConnection.OpenStream</c>) for drivers that honour <c>COMMTIMEOUTS</c>, and a task-level
    /// deadline in <see cref="TryWriteAsync"/> for the ones that do not -- <c>bthmodem.sys</c> ignored the
    /// former. Same value as <c>TcpSerialConnection</c>'s stream timeouts. A timeout surfaces as a failed
    /// <c>Try*</c> write, which a probe reads as no-match and a session driver as a fault for
    /// <c>ResilientCall</c> to reconnect through.
    /// </summary>
    internal const int WriteTimeoutMs = 2000;

    public async ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        // Started outside the try so the catch can tell a write that FAILED from one that is still PENDING when
        // the deadline or the token fires; only the latter marks the port (see HasAbandonedIo). An abandoned
        // write stays pending on the handle until a (bounded) close releases it; see WriteTimeoutMs.
        var write = _stream.WriteAsync(message, cancellationToken).AsTask();
        try
        {
            await write.WaitAsync(TimeSpan.FromMilliseconds(WriteTimeoutMs), cancellationToken).ConfigureAwait(false);
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
            if (!write.IsCompleted)
            {
                Volatile.Write(ref _abandonedIo, 1);
                // Observe whatever the abandoned write eventually does (an I/O abort once the handle closes).
                _ = write.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                _logger.LogWarning("{Port} never completed the write of {Message}; the port is marked as not completing I/O.",
                    DisplayName, Encoding.GetString(message.Span).ReplaceNonPrintableWithHex());
                return false;
            }

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

    public virtual async ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
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
            LogReadFailure(ex);
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

    public virtual async ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken)
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
            LogReadFailure(ex);
            _logger.LogDebug(ex, "TryReadExactlyRawAsync failed on {Port}", DisplayName);

            return false;
        }
    }

    public void Dispose() => _ = TryClose();

    /// <summary>
    /// Formats a read failure as a single tagged "no response" Info line when verbose
    /// logging is on. Carries HResult (Win32 error, e.g. 0x800703E3 =
    /// ERROR_OPERATION_ABORTED from a cancelled overlapped SerialStream read),
    /// current IsOpen state (distinguishes "CTS cancelled our read" from "port was
    /// closed under us"), and inner-exception type+message so caller cancellation
    /// vs driver-level abort is decidable from the log alone.
    /// </summary>
    private void LogReadFailure(Exception ex)
    {
        if (!LogVerbose) return;

        var tag = VerboseTag;
        var reason = SanitizeReason(ex);
        var inner = ex.InnerException is { } ie
            ? $"{ie.GetType().Name}: {SanitizeReasonString(ie.Message)}"
            : "-";
        var portIsOpen = IsOpen;

        if (!string.IsNullOrEmpty(tag))
        {
            _logger.LogInformation(
                "{Port} [{Tag}] <-- (no response: {ExceptionType}: {Reason}; HResult=0x{HResult:X8}; IsOpen={IsOpen}; Inner={Inner})",
                DisplayName, tag, ex.GetType().Name, reason, ex.HResult, portIsOpen, inner);
        }
        else
        {
            _logger.LogInformation(
                "{Port} <-- (no response: {ExceptionType}: {Reason}; HResult=0x{HResult:X8}; IsOpen={IsOpen}; Inner={Inner})",
                DisplayName, ex.GetType().Name, reason, ex.HResult, portIsOpen, inner);
        }
    }

    // Squash newlines/trailing whitespace: IOException.Message from SerialStream is
    // often multi-line ("The I/O operation has been aborted...\r\n"), which shreds
    // the one-line-per-exchange probe log.
    private static string SanitizeReason(Exception ex) => SanitizeReasonString(ex.Message);

    private static string SanitizeReasonString(string message)
        => message.TrimEnd().Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
}
