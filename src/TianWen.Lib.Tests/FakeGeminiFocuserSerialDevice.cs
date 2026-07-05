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
/// In-memory <see cref="ISerialConnection"/> that simulates a Gemini Focuser Pro (myFocuserPro2) controller:
/// it parses the <c>':' … '#'</c> framed commands, holds position / target / temperature / temp-comp state,
/// answers the get commands with a <c>&lt;status-char&gt;&lt;payload&gt;#</c> reply, and treats the set
/// commands (Move/Halt) as silent. Used by the protocol and probe tests — no hardware.
/// </summary>
internal sealed class FakeGeminiFocuserSerialDevice(
    string firmwareName = "myFP2",
    int firmwareVersion = 312,
    bool present = true,
    int maxStep = 100_000,
    bool tempCompAvailable = true) : ISerialConnection
{
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly Queue<byte> _readBuffer = new();

    public int Position { get; set; } = 50_000;
    public int Target { get; private set; } = 50_000;
    public bool Moving { get; set; }
    public double Temperature { get; set; } = 12.5;
    public double StepSize { get; set; } = 5.0;
    public bool TempCompEnabled { get; private set; }
    public List<string> WrittenCommands { get; } = [];

    /// <summary>Models a dead USB bridge: <see cref="IsOpen"/> stays true but no reply is ever enqueued.</summary>
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
        var start = raw.IndexOf(':');
        var end = raw.IndexOf('#');
        if (start < 0 || end < 0 || end <= start)
        {
            return ValueTask.FromResult(false);
        }

        var body = raw.Substring(start + 1, end - start - 1);
        WrittenCommands.Add($":{body}#");
        HandleCommand(body);
        return ValueTask.FromResult(true);
    }

    private void HandleCommand(string body)
    {
        // Get commands reply "<status-char><payload>#"; set commands are silent (Move/Halt) or ack "OK"
        // (temp-comp toggle). The status char is arbitrary here (the codec strips it unconditionally).
        switch (body)
        {
            case "02": // controller present
                Enqueue(present ? "!OK#" : "!NO#");
                break;
            case "04": // firmware name + version
                Enqueue($"F{firmwareName}\r\n{firmwareVersion.ToString(CultureInfo.InvariantCulture)}#");
                break;
            case "00": // position
                Enqueue($"P{Position.ToString(CultureInfo.InvariantCulture)}#");
                break;
            case "01": // is moving
                Enqueue($"I{(Moving ? "1" : "0")}#");
                break;
            case "06": // temperature
                Enqueue($"T{Temperature.ToString("0.00", CultureInfo.InvariantCulture)}#");
                break;
            case "08": // max step
                Enqueue($"M{maxStep.ToString(CultureInfo.InvariantCulture)}#");
                break;
            case "24": // temp comp enabled
                Enqueue($"A{(TempCompEnabled ? "1" : "0")}#");
                break;
            case "25": // temp comp available
                Enqueue($"B{(tempCompAvailable ? "1" : "0")}#");
                break;
            case "33": // step size
                Enqueue($"Q{StepSize.ToString("0.0", CultureInfo.InvariantCulture)}#");
                break;
            case "27": // halt — silent
                Moving = false;
                break;
            case "230": // temp comp off — acks OK
                TempCompEnabled = false;
                Enqueue("!OK#");
                break;
            case "231": // temp comp on — acks OK
                TempCompEnabled = true;
                Enqueue("!OK#");
                break;
            default:
                if (body.StartsWith("05", StringComparison.Ordinal)
                    && int.TryParse(body.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
                {
                    // Move to absolute target — silent.
                    Target = t;
                    Moving = true;
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
