using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;

namespace TianWen.Lib.Connections;

internal sealed class SerialConnection(string portName, int baud, Encoding encoding, ILogger logger)
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
}