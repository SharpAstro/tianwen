using DotNext.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Tests;

/// <summary>
/// In-memory <see cref="ISerialConnection"/> that simulates a Gemini FlatPanel Lite controller: it parses
/// the <c>'&gt;' … '#'</c> framed commands, holds light on/off + brightness state, and answers the
/// <c>H</c>/<c>V</c>/<c>S</c>/<c>J</c> queries. Used by the protocol, probe, and driver tests — no hardware.
/// </summary>
internal sealed class FakeGeminiFlatPanelSerialDevice(string identity = "GeminiFlatPanelLite", int firmware = 205) : ISerialConnection
{
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly Queue<byte> _readBuffer = new();

    public bool LightOn { get; private set; }
    public int Brightness { get; private set; }
    public List<string> WrittenCommands { get; } = [];

    /// <summary>
    /// Models a dead USB bridge (unplugged CH341): <see cref="IsOpen"/> keeps reading true but the
    /// controller stops answering -- writes are accepted into the OS buffer, no reply is ever enqueued.
    /// </summary>
    public bool Dead { get; set; }

    public bool IsOpen { get; private set; } = true;
    public Encoding Encoding => Encoding.ASCII;
    public bool TryClose() { IsOpen = false; return true; }
    public void Dispose() => TryClose();

    public ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken) => _sem.AcquireLockAsync(cancellationToken);

    public ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (Dead)
        {
            return ValueTask.FromResult(true);
        }

        var raw = Encoding.GetString(data.Span);

        // Extract the framed command body between '>' and '#' (tolerant of any trailing newline).
        var start = raw.IndexOf('>');
        var end = raw.IndexOf('#');
        if (start < 0 || end < 0 || end <= start)
        {
            return ValueTask.FromResult(false);
        }

        var body = raw.Substring(start + 1, end - start - 1);
        WrittenCommands.Add($">{body}#");
        HandleCommand(body);
        return ValueTask.FromResult(true);
    }

    private void HandleCommand(string body)
    {
        switch (body)
        {
            case "H":
                Enqueue($">H{identity}#");
                break;
            case "V":
                Enqueue($">V{firmware.ToString(CultureInfo.InvariantCulture)}#");
                break;
            case "S":
                Enqueue($">S{(LightOn ? 1 : 0)}#");
                break;
            case "J":
                Enqueue($">J{Brightness.ToString(CultureInfo.InvariantCulture)}#");
                break;
            case "L":
                LightOn = true;
                break;
            case "D":
                LightOn = false;
                break;
            default:
                // >B<n># set brightness; >Y#/>T#/>X# accepted with no reply.
                if (body.StartsWith('B') && int.TryParse(body.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                {
                    Brightness = Math.Clamp(b, 0, 255);
                }
                break;
        }
    }

    private void Enqueue(string framed)
    {
        foreach (var ch in framed)
        {
            _readBuffer.Enqueue((byte)ch);
        }
    }

    public ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        while (_readBuffer.Count > 0)
        {
            var by = _readBuffer.Dequeue();
            foreach (var t in terminators.Span)
            {
                if (by == t)
                {
                    return ValueTask.FromResult<string?>(sb.ToString());
                }
            }
            sb.Append((char)by);
        }

        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken) => throw new NotSupportedException();
}
