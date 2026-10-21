using DotNext.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Tests;

/// <summary>
/// In-memory <see cref="ISerialConnection"/> that behaves as the bench Gemini Power&amp;Data Hub Advanced 3 did (2026-10-21):
/// it answers <c>&gt;H#</c> and <c>&gt;V#</c>, answers <c>&gt;G#</c> with a status frame in the box's exact layout, takes
/// <c>&gt;O</c>/<c>&gt;C</c>/<c>&gt;X</c>/<c>&gt;Y</c> silently, and lets a test PUSH a frame as the box does every 3 s. A read
/// waits for bytes as a port does, so the driver's reader can sit in one while a command is written beside it.
/// </summary>
internal sealed class FakeGeminiPowerBoxSerialDevice : ISerialConnection
{
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly Channel<byte> _incoming = Channel.CreateUnbounded<byte>();
    // The driver's reader thread and a test both reach the box's state.
    private readonly System.Threading.Lock _stateLock = new();

    public FakeGeminiPowerBoxSerialDevice(string identity = "GeminiPowerBoxPlusAdv3", int firmware = 309)
    {
        Identity = identity;
        Firmware = firmware;
    }

    public string Identity { get; }
    public int Firmware { get; }

    public bool[] DcOn { get; } = [true, true, true, true];
    public bool[] UsbOn { get; } = [true, true, true, true, true, true];
    public bool HasAmbientSensor { get; set; } = true;
    public bool HasLensProbe { get; set; } = true;
    public int Dew6Mode { get; set; } = 1;
    public int Dew7Mode { get; set; } = 1;
    public int Dew6Power { get; set; } = 100;
    public int Dew7Power { get; set; } = 100;
    public double LensTemperature { get; set; } = 24.81;
    public double AmbientTemperature { get; set; } = 26.87;
    public double Humidity { get; set; } = 56.00;
    public double DewPoint { get; set; } = 17.35;
    public double Voltage { get; set; } = 12.9;
    public double Current { get; set; } = 0.16;
    public double Power { get; set; } = 2.06;

    /// <summary>Models a box that hears a set and does not act on it: no frame will ever show it.</summary>
    public bool IgnoreSets { get; set; }

    /// <summary>Models a box that answers no status request.</summary>
    public bool NoStatus { get; set; }

    public List<string> WrittenCommands { get; } = [];

    public bool IsOpen { get; private set; } = true;
    public Encoding Encoding => Encoding.ASCII;
    public ValueTask<bool> TryCloseAsync() { Unplug(); return ValueTask.FromResult(true); }
    public ValueTask DisposeAsync() { Unplug(); return ValueTask.CompletedTask; }

    /// <summary>
    /// The port goes, as the box's does with its 12 V or its cable: closed, a read waiting in it ends empty, and every
    /// later read and write fails.
    /// </summary>
    public void Unplug()
    {
        IsOpen = false;
        _incoming.Writer.TryComplete();
    }

    private int _writesAfterClose;

    /// <summary>Writes tried on the port after it closed: a reader the driver left running.</summary>
    public int WritesAfterClose => Volatile.Read(ref _writesAfterClose);

    /// <summary>Puts bytes on the wire as they are, for a fragment no whole frame would be.</summary>
    public void EnqueueRaw(string bytes) => Enqueue(bytes);

    public ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken) => _sem.AcquireLockAsync(cancellationToken);

    /// <summary>The status frame as the box writes it now, <c>*G...P#</c>.</summary>
    public string StatusFrame()
    {
        static string Bits(bool[] bits) => string.Concat(Array.ConvertAll(bits, b => b ? "1" : "0"));
        static string F(double v, int width, string format) => v.ToString(format, CultureInfo.InvariantCulture).PadLeft(width);

        lock (_stateLock)
        {
            return $"*G{Bits(DcOn)}D{Bits(UsbOn)}U{(HasAmbientSensor ? 1 : 0)}A{(HasLensProbe ? 1 : 0)}T1A{Dew6Mode}M1B{Dew7Mode}M"
                + $"{Dew6Power}C{Dew7Power}C{F(LensTemperature, 6, "0.00")}S{F(AmbientTemperature, 6, "0.00")}T{F(Humidity, 6, "0.00")}H"
                + $"{F(DewPoint, 6, "0.00")}D{F(Voltage, 4, "0.0")}V{F(Current, 5, "0.00")}C{F(Power, 6, "0.00")}P#";
        }
    }

    /// <summary>Sends a status frame unasked, as the box does every 3 s once anything asked it for status.</summary>
    public void Push() => Enqueue(StatusFrame());

    public ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            Interlocked.Increment(ref _writesAfterClose);
            return ValueTask.FromResult(false);
        }

        var raw = Encoding.GetString(data.Span);
        var start = raw.IndexOf('>');
        var end = raw.IndexOf('#');
        if (start < 0 || end <= start + 1)
        {
            return ValueTask.FromResult(false);
        }

        var command = raw[(start + 1)..end];
        lock (_stateLock)
        {
            WrittenCommands.Add($">{command}#");
        }

        Handle(command[0], command[1..]);
        return ValueTask.FromResult(true);
    }

    private void Handle(char letter, string argument)
    {
        switch (letter)
        {
            case 'H':
                Enqueue($"*H{Identity}#");
                return;
            case 'V':
                Enqueue($"*V{Firmware.ToString(CultureInfo.InvariantCulture)}#");
                return;
            case 'G':
                if (!NoStatus)
                {
                    Push();
                }

                return;
        }

        if (IgnoreSets || !int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            return;
        }

        lock (_stateLock)
        {
            switch (letter)
            {
                case 'O' or 'C' when n is >= 2 and <= 5:
                    DcOn[n - 2] = letter == 'O';
                    break;
                case 'O' or 'C' when n is >= 6 and <= 11:
                    UsbOn[n - 6] = letter == 'O';
                    break;
                case 'X':
                    Dew6Power = n;
                    break;
                case 'Y':
                    Dew7Power = n;
                    break;
            }
        }
    }

    private void Enqueue(string frame)
    {
        foreach (var ch in frame)
        {
            _incoming.Writer.TryWrite((byte)ch);
        }
    }

    /// <summary>Waits for bytes up to a terminator, as a port does; a cancelled read gives up the bytes it took, as Serial.Lib's does.</summary>
    public async ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                var by = await _incoming.Reader.ReadAsync(cancellationToken);
                if (terminators.Span.IndexOf(by) >= 0)
                {
                    return sb.ToString();
                }

                sb.Append((char)by);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken) => throw new NotSupportedException();
}
