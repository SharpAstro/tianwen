using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;

namespace TianWen.Lib.Connections;

internal sealed class SerialConnection(string portName, int baud, Encoding encoding, ILogger logger, TimeSpan? ioTimeout = null)
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

    public static string CleanupPortName(string portName)
    {
        var portNameWithoutPrefix = portName.StartsWith(ISerialConnection.SerialProto, StringComparison.Ordinal) ? portName[ISerialConnection.SerialProto.Length..] : portName;

        return portNameWithoutPrefix.StartsWith("tty", StringComparison.Ordinal) ? $"/dev/{portNameWithoutPrefix}" : portNameWithoutPrefix;
    }

    private readonly SerialPort _port =  new SerialPort(CleanupPortName(portName), baud);

    protected override Stream OpenStream()
    {
        _port.Open();
        var stream = _port.BaseStream;
        var timeoutMs = (int)Math.Round((ioTimeout ?? TimeSpan.FromMilliseconds(500)).TotalMilliseconds);
        stream.ReadTimeout = timeoutMs;
        stream.WriteTimeout = timeoutMs;
        return stream;
    }

    public override bool IsOpen => _port.IsOpen;

    public override string DisplayName => throw new NotImplementedException();

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