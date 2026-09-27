using Microsoft.Extensions.Logging;
using SharpAstro.Serial;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

/// <summary>
/// <see cref="ISerialConnection"/> over a Serial.Lib port (<c>SharpAstro.Serial</c>), which owns everything a serial
/// transport has to get right: reads that never abort spuriously (the CH34x trap of <c>System.IO.Ports</c> async
/// reads), writes a dead driver cannot strand (the Bluetooth listener port), a bounded open and close, bytes after a
/// terminator kept for the next read, and a removed device told apart from a timeout (docs/plans/serial-lib.md).
/// This class only adapts: the library's typed failures become the <c>Try*</c> contract's null / -1 / false, and every
/// exchange is logged the way the probe log has always shown it.
/// </summary>
internal sealed class SerialConnection : ISerialConnection
{
    /// <summary>
    /// The longest reply <see cref="TryReadTerminatedAsync"/> accepts, terminator included: a reply whose terminator is
    /// not within this many bytes is refused (null), never truncated. 128 because that is the window this read always
    /// had in practice (it asked the pool for 100, was handed the 128-byte bucket, and passed the whole array on)
    /// until the <see cref="ArrayPoolHelper"/> migration handed on exactly the 100 requested and silently cut it.
    /// </summary>
    internal const int MaxTerminatedResponseBytes = 128;

    /// <summary>
    /// A write to a healthy port completes in milliseconds; one still pending after this is given up and the port marked
    /// (<see cref="HasAbandonedIo"/>). Same value as <see cref="TcpSerialConnection"/>'s stream timeouts.
    /// </summary>
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);

    private readonly ISerialPort _port;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    internal SerialConnection(ISerialPort port, Encoding encoding, ILogger logger)
    {
        _port = port;
        _logger = logger;
        Encoding = encoding;
    }

    /// <summary>
    /// Opens <paramref name="portName"/> (with or without the <c>serial:</c> prefix). <paramref name="assertControlLines"/>
    /// asserts DTR and RTS from the open on, for bridges that hold the MCU in reset otherwise (the Gemini FlatPanel's CH341).
    /// </summary>
    /// <exception cref="SerialException">The port is missing, busy, or did not open in time.</exception>
    public static async ValueTask<SerialConnection> OpenAsync(string portName, int baud, Encoding encoding, ILogger logger, bool assertControlLines, CancellationToken cancellationToken)
    {
        var settings = new SerialSettings(baud)
        {
            AssertDtr = assertControlLines,
            AssertRts = assertControlLines,
            // Reads are bounded by the caller's token, as ISerialConnection's always have been: a QHY filter wheel's
            // reply blocks until the wheel arrives, and every driver chooses its own budget.
            ReadTimeout = Timeout.InfiniteTimeSpan,
            WriteTimeout = WriteTimeout,
        };
        var port = await SerialPorts.OpenAsync(ISerialConnection.CleanupPortName(portName), settings, cancellationToken).ConfigureAwait(false);
        return new SerialConnection(port, encoding, logger);
    }

    /// <summary>The OS ports present now, each with the <c>serial:</c> prefix.</summary>
    public static IReadOnlyList<string> EnumerateSerialPorts()
    {
        var ports = SerialPorts.Enumerate();
        var names = new List<string>(ports.Count);
        foreach (var port in ports)
        {
            names.Add($"{ISerialConnection.SerialProto}{port.PortName}");
        }
        return names;
    }

    public bool IsOpen => _port.IsOpen;

    public string DisplayName => _port.PortName;

    public Encoding Encoding { get; }

    /// <inheritdoc />
    public bool LogVerbose { get; set; }

    /// <inheritdoc />
    public string? VerboseTag { get; set; }

    /// <inheritdoc />
    public bool HasAbandonedIo => _port.HasAbandonedIo;

    public ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken) => _semaphore.AcquireLockAsync(cancellationToken);

    public async ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await _port.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (SerialIoAbandonedException)
        {
            _logger.LogWarning("{Port} never completed the write of {Message}; the port is marked as not completing I/O.",
                DisplayName, Render(data.Span));
            return false;
        }
        catch (Exception ex) when (ex is SerialException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogError(ex, "Error while sending message {Message} to serial device on serial port {Port}", Render(data.Span), DisplayName);
            return false;
        }

        LogTraffic("-->", Render(data.Span));
        return true;
    }

    public async ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        using var buffer = ArrayPoolHelper.Rent<byte>(MaxTerminatedResponseBytes);
        var bytesRead = await TryReadTerminatedRawAsync(buffer.AsMemory(0, MaxTerminatedResponseBytes - 1), terminators, cancellationToken).ConfigureAwait(false);
        return bytesRead >= 0 ? Encoding.GetString(buffer.AsSpan(0, bytesRead)) : null;
    }

    public async ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        try
        {
            var bytesRead = await _port.ReadTerminatedAsync(message, terminators, cancellationToken).ConfigureAwait(false);
            // The terminator is consumed, not stored; shown as a marker so e.g. LX200 "On-Step#" reads as the wire did.
            LogTraffic("<--", Render(message.Span[..bytesRead]) + "<term>");
            return bytesRead;
        }
        catch (SerialFramingException ex)
        {
            _logger.LogWarning("Terminator (any of {Terminators}) not found in message from serial device on serial port {Port} ({Received})",
                Render(terminators.Span), DisplayName, Render(ex.Received.Span));
            return -1;
        }
        catch (Exception ex) when (ex is SerialException or ObjectDisposedException or OperationCanceledException)
        {
            // Try* contract: failures are the return value. A cancelled read is the caller's own budget (a probe
            // timeout, a driver's deadline); a removed device, a closed port or a driver fault is the rest.
            LogReadFailure(ex);
            _logger.LogDebug(ex, "TryReadTerminatedRawAsync failed on {Port}", DisplayName);
            return -1;
        }
    }

    public async ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        using var buffer = ArrayPoolHelper.Rent<byte>(count);
        return await TryReadExactlyRawAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false)
            ? Encoding.GetString(buffer.AsSpan(0, count))
            : null;
    }

    public async ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken)
    {
        try
        {
            await _port.ReadExactlyAsync(message, cancellationToken).ConfigureAwait(false);
            LogTraffic("<--", $"{Render(message.Span)} ({message.Length})");
            return true;
        }
        catch (Exception ex) when (ex is SerialException or ObjectDisposedException or OperationCanceledException)
        {
            LogReadFailure(ex);
            _logger.LogDebug(ex, "TryReadExactlyRawAsync failed on {Port}", DisplayName);
            return false;
        }
    }

    /// <inheritdoc />
    public void DiscardInBuffer()
    {
        // Read what is pending FIRST so the operator can see what the device actually sent: OnStep's unterminated "0"
        // on its first :GVP# is the canonical case, a prior LX200 probe's read timing out before the '#' it never sent.
        // Capped at 4 KiB so a chattering device cannot flood the log; the port discards the rest.
        Span<byte> drained = stackalloc byte[4096];
        try
        {
            var n = _port.DiscardInput(drained);
            if (n > 0 && LogVerbose)
            {
                LogTraffic("<--", $"(drained {n} byte(s): {Render(drained[..n])})");
            }
        }
        catch (Exception ex) when (ex is SerialException or ObjectDisposedException)
        {
            // A port closed or removed between probes: the next probe fails cleanly on its own write.
            _logger.LogDebug(ex, "DiscardInBuffer failed on {Port}", DisplayName);
        }
    }

    /// <summary>
    /// Closes the port within the library's close deadline. False when the close had to be abandoned: the driver still
    /// holds I/O it never completed (the Bluetooth listener port's stranded write), so the next open may find it busy.
    /// </summary>
    public async ValueTask<bool> TryCloseAsync()
    {
        var closed = await _port.CloseAsync().ConfigureAwait(false);
        // As the old transport did: a waiter on the exchange lock of a closed connection fails at once rather than
        // waiting on a port that will not answer again.
        _semaphore.Dispose();
        if (!closed)
        {
            _logger.LogWarning("{Port} did not close in time (pending I/O the driver never completed); abandoning the handle.", DisplayName);
        }
        return closed;
    }

    public async ValueTask DisposeAsync()
    {
        _ = await TryCloseAsync().ConfigureAwait(false);
    }

    private string Render(ReadOnlySpan<byte> bytes) => Encoding.GetString(bytes).ReplaceNonPrintableWithHex() ?? "";

    /// <summary>
    /// One line per exchange: at Info, tagged, while probing (<see cref="LogVerbose"/>), so the operator sees the
    /// handshake without enabling Debug; at Trace otherwise, since drivers poll dozens of times a second.
    /// </summary>
    private void LogTraffic(string direction, string rendered)
    {
        if (!LogVerbose)
        {
            _logger.LogTrace("{Direction} {Message}", direction, rendered);
        }
        else if (VerboseTag is { Length: > 0 } tag)
        {
            _logger.LogInformation("{Port} [{Tag}] {Direction} {Message}", DisplayName, tag, direction, rendered);
        }
        else
        {
            _logger.LogInformation("{Port} {Direction} {Message}", DisplayName, direction, rendered);
        }
    }

    /// <summary>
    /// The "no response" side of an exchange as one tagged Info line while probing, so every --> write has a matching
    /// &lt;-- outcome: the failure's type (a timeout, a removal, a cancel), what arrived before it, and whether the port
    /// is still open (the caller's cancel against a port closed under the read).
    /// </summary>
    private void LogReadFailure(Exception ex)
    {
        if (!LogVerbose)
        {
            return;
        }
        var received = ex is SerialTimeoutException { Received.Length: > 0 } timeout ? Render(timeout.Received.Span) : "-";
        LogTraffic("<--", $"(no response: {ex.GetType().Name}: {Sanitize(ex.Message)}; received {received}; IsOpen={IsOpen})");
    }

    // An exception message can be multi-line, which would shred the one-line-per-exchange probe log.
    private static string Sanitize(string message) => message.TrimEnd().Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
}
