using Microsoft.Extensions.Logging;
using System.IO;
using System.Text;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Tests;

/// <summary>
/// Simple serial class allows testing of the main serial code without actually having to open a terminal.
/// </summary>
/// <param name="stream"></param>
/// <param name="encoding"></param>
/// <param name="name"></param>
/// <param name="logger"></param>
internal class StreamSerialConnection(Stream stream, Encoding encoding, string name, ILogger logger)
    : SerialConnectionBase(encoding, logger)
{
    private bool _isOpen;

    public override bool IsOpen => _isOpen;

    public override string DisplayName => name;

    protected override Stream OpenStream()
    {
        _isOpen = true;

        return stream;
    }
}