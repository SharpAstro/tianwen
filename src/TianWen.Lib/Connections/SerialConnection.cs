using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

internal sealed class SerialConnection(string portName, int baud, Encoding encoding, ILogger logger, bool assertControlLines = false)
    : SerialConnectionBase(encoding, logger)
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

    private readonly SerialPort _port =  new SerialPort(ISerialConnection.CleanupPortName(portName), baud);

    protected override Stream OpenStream()
    {
        // Assert DTR + RTS before opening for bridges that hold the MCU in reset otherwise
        // (e.g. the Gemini FlatPanel's CH341). Opt-in — off for every device that doesn't need it.
        if (assertControlLines)
        {
            _port.DtrEnable = true;
            _port.RtsEnable = true;
        }

        _port.Open();
        return _port.BaseStream;
    }

    public override bool IsOpen => _port.IsOpen;

    public override string DisplayName => _port.PortName;

    /// <inheritdoc />
    public override void DiscardInBuffer()
    {
        // SerialPort.DiscardInBuffer can throw InvalidOperationException if the port
        // was closed concurrently (race with TryClose). Swallow that — best-effort.
        if (!_port.IsOpen) return;
        try
        {
            // Read any pending bytes FIRST so the operator can see what the device
            // actually sent — OnStep's unterminated "0" on its first :GVP# is the
            // canonical case: a prior LX200 probe's read times out before the '#'
            // it never sent, leaving a stray '0' in the buffer that would pollute
            // the next framed read. BytesToRead is non-blocking; BaseStream.Read
            // with a Span returns immediately when bytes are buffered.
            var pending = _port.BytesToRead;
            if (pending > 0)
            {
                Span<byte> scratch = stackalloc byte[256];
                // Cap at 4 KiB so a misbehaving device can't starve discovery with
                // an endless chatter stream — the native discard below sweeps any
                // remainder into the bit bucket.
                var drained = 0;
                while (pending > 0 && drained < 4096)
                {
                    var take = Math.Min(pending, scratch.Length);
                    var n = _port.BaseStream.Read(scratch[..take]);
                    if (n <= 0) break;
                    LogDrained(scratch[..n]);
                    drained += n;
                    pending = _port.BytesToRead;
                }
            }
            _port.DiscardInBuffer();
        }
        catch (InvalidOperationException)
        {
            // port closed between the IsOpen check and the native call — ignore.
        }
        catch (IOException ex)
        {
            // Reading the pending bytes failed (rare — driver-level). Try the
            // native discard anyway so the next probe starts clean-ish, and log
            // the failure at Debug so it isn't a silent no-op.
            _logger.LogDebug(ex, "DiscardInBuffer drain-read failed on {Port}", DisplayName);
            try { _port.DiscardInBuffer(); }
            catch (InvalidOperationException) { /* see above */ }
        }
    }

    /// <summary>
    /// Closes the serial port if it is open
    /// </summary>
    /// <returns>true if the prot is closed</returns>
    public override bool TryClose()
    {
        if (_port.IsOpen)
        {
            _port.Close();

            return base.TryClose();
        }
        return !_port.IsOpen;
    }

    // ReadTimeout slice for the synchronous read path: short enough to observe cancellation promptly,
    // long enough not to busy-spin.
    private const int SyncReadSliceMs = 200;

    /// <summary>
    /// Cancellable synchronous read loop used when <see cref="ISerialConnection.SynchronousReads"/> is set.
    /// .NET's <c>SerialStream</c> "async" is itself just a blocking read on a background thread
    /// (dotnet/runtime#28968), and its <c>BaseStream.ReadAsync</c> spuriously aborts with
    /// <c>ERROR_OPERATION_ABORTED</c> on some USB bridges (CH34x) after the first read. So here we do exactly
    /// what the runtime maintainers recommend — a blocking <c>Read</c> (honors <c>ReadTimeout</c>, immune to
    /// the abort). Cancellation is observed between <c>ReadTimeout</c> slices, so no blocked thread is
    /// abandoned. Returns bytes stored, or -1 on failure/cancellation — matching the base "Try*" contract
    /// (report failure via the return value, never throw).
    /// </summary>
    private int SyncRead(Memory<byte> message, ReadOnlyMemory<byte> terminators, bool exact, CancellationToken cancellationToken)
    {
        try
        {
            _port.ReadTimeout = SyncReadSliceMs;
            var count = 0;
            while (count < message.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int b;
                try
                {
                    b = _port.ReadByte();
                }
                catch (TimeoutException)
                {
                    continue; // slice elapsed with no byte — re-check cancellation and keep waiting
                }

                if (b < 0)
                {
                    return exact ? -1 : count; // EOF: an exact read is short (fail); terminated returns what it has
                }
                if (!exact && terminators.Span.IndexOf((byte)b) >= 0)
                {
                    return count; // terminator reached — not stored, matching the async path's contract
                }

                message.Span[count++] = (byte)b;
            }
            return count; // buffer full
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
    }

    public override async ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        if (!SynchronousReads)
        {
            return await base.TryReadTerminatedRawAsync(message, terminators, cancellationToken).ConfigureAwait(false);
        }

        // Task.Run without the token: SyncRead observes cancellation internally and returns -1, so the read
        // never surfaces an OperationCanceledException (matching the base async path, which also swallows it).
        return await Task.Run(() => SyncRead(message, terminators, exact: false, cancellationToken)).ConfigureAwait(false);
    }

    public override async ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken)
    {
        if (!SynchronousReads)
        {
            return await base.TryReadExactlyRawAsync(message, cancellationToken).ConfigureAwait(false);
        }

        return await Task.Run(() => SyncRead(message, default, exact: true, cancellationToken)).ConfigureAwait(false) == message.Length;
    }
}