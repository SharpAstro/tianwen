using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Native (ASCOM-free) driver for the Gemini Power&amp;Data Hub Advanced 3: its 12 V and USB outputs, its two dew heaters and
/// what it measures, as an <see cref="ISwitchDriver"/> over the <see cref="GeminiPowerBoxProtocol"/> codec.
/// </summary>
/// <remarks>
/// <para><b>The box speaks first.</b> Once asked for status it pushes a status frame every 3 s, so a reader loop takes each
/// one as it comes and every read is answered from the latest. That loop is the port's ONLY reader once connected, and it
/// never gives a read up early: Serial.Lib drops the bytes a failed read consumed, so a read cut off by a short deadline
/// lost the frame arriving at that moment (a frame comes in pieces over about 30 ms; one run on the bench box lost one).
/// Serial.Lib allows one reader and one writer at a time, so a command only writes, under the connection's lock, beside
/// the reader. A frame older than <see cref="StatusStaleAfter"/> is not answered from: the box has gone quiet, which is a
/// fault, not a value.</para>
/// <para><b>A set is confirmed only by a later frame</b>: the box answers no set command. So a set is written, status asked
/// for at once, and the call returns when a frame read after the write shows the new value.</para>
/// <para><b>The dew heaters' modes are the box's</b>: the vendor driver writes a mode on every connect, this driver never
/// does, and a dew heater's power is set only where the box has it in Manual mode.</para>
/// </remarks>
internal sealed class GeminiPowerBoxDriver(GeminiPowerBoxDevice device, IServiceProvider serviceProvider) : ISwitchDriver
{
    /// <summary>The vendor driver waits this long after opening before its first command.</summary>
    private static readonly TimeSpan BootDelay = TimeSpan.FromSeconds(2);
    private const int HandshakeAttempts = 3;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the reader waits for a frame before asking for status again (the box pushes every 3 s). A read given up
    /// here loses nothing, since nothing was arriving.
    /// </summary>
    private static readonly TimeSpan RequestAgainAfter = TimeSpan.FromSeconds(6);

    /// <summary>The pause after a read that failed for a reason other than silence.</summary>
    private static readonly TimeSpan RetryAfterFault = TimeSpan.FromMilliseconds(500);

    /// <summary>A frame older than this is not answered from.</summary>
    internal static readonly TimeSpan StatusStaleAfter = TimeSpan.FromSeconds(12);

    /// <summary>How long a set waits for a frame showing it, and a connect for the first frame; a test shortens it.</summary>
    internal TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(8);

    // Channel indices. DC1 first, then DC2..DC5, USB A..F, the two dew heaters and the readings.
    private const int Dc1 = 0;
    private const int FirstSwitchedDc = 1;
    private const int FirstUsb = 5;
    private const int Dew6 = 11;
    private const int Dew7 = 12;
    private const int Voltage = 13;
    private const int Current = 14;
    private const int Power = 15;
    private const int AmbientTemperature = 16;
    private const int Humidity = 17;
    private const int DewPoint = 18;
    private const int LensTemperature = 19;

    private static readonly IReadOnlyList<SwitchChannel> AllChannels = BuildChannels();

    private volatile ISerialConnection? _conn;
    private volatile bool _connected;
    private CancellationTokenSource? _readerStop;
    private Task? _reader;
    private StatusAt? _latest;

    // Completed by the reader each time it publishes a frame and replaced at once: a waiter takes the current one BEFORE it
    // looks at the latest frame, so a frame published between the look and the wait still wakes it.
    private TaskCompletionSource _nextFrame = NewSignal();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A status frame, when it was read, and its number in this connection's sequence of frames.</summary>
    private sealed record StatusAt(GeminiPowerBoxStatus Status, DateTimeOffset At, long Sequence);

    public IReadOnlyList<SwitchChannel> Channels => AllChannels;

    /// <summary>The latest frame and when it was read, for the tests and the node's telemetry; null before the first.</summary>
    internal (GeminiPowerBoxStatus Status, DateTimeOffset At)? LatestStatus => Volatile.Read(ref _latest) is { } latest ? (latest.Status, latest.At) : null;

    public ValueTask<double> GetValueAsync(int channel, CancellationToken cancellationToken = default)
    {
        Validate(channel);
        var status = Fresh();
        return ValueTask.FromResult(channel switch
        {
            Dc1 => 1d,
            < FirstUsb => On(status.DcOn[channel - FirstSwitchedDc]),
            < Dew6 => On(status.UsbOn[channel - FirstUsb]),
            Dew6 => DewLevel(status.Dew6Mode, status.Dew6Enabled, status.Dew6Power),
            Dew7 => DewLevel(status.Dew7Mode, status.Dew7Enabled, status.Dew7Power),
            Voltage => status.Voltage,
            Current => status.Current,
            Power => status.Power,
            AmbientTemperature => status.HasAmbientSensor ? status.AmbientTemperature : double.NaN,
            Humidity => status.HasAmbientSensor ? status.Humidity : double.NaN,
            DewPoint => status.HasAmbientSensor ? status.DewPoint : double.NaN,
            _ => status.HasLensProbe ? status.LensTemperature : double.NaN,
        });

        static double On(bool on) => on ? 1d : 0d;

        // Manual: the power set. Switch: full or nothing. Auto: the box decides from the dew point and does not say what
        // power it chose, so the level is unknown.
        static double DewLevel(GeminiPowerBoxDewMode mode, bool enabled, double power) => mode switch
        {
            GeminiPowerBoxDewMode.Manual => power,
            GeminiPowerBoxDewMode.Switch => enabled ? 100d : 0d,
            _ => double.NaN,
        };
    }

    public async ValueTask SetValueAsync(int channel, double value, CancellationToken cancellationToken = default)
    {
        Validate(channel);
        var spec = AllChannels[channel];
        if (!spec.CanWrite)
        {
            throw new InvalidOperationException($"{Name}: {spec.Name} cannot be written");
        }

        if (value < spec.Min || value > spec.Max || double.IsNaN(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"{spec.Name} takes {spec.Min} to {spec.Max}");
        }

        if (_conn is not { IsOpen: true } conn)
        {
            throw new InvalidOperationException($"{Name} is not connected");
        }

        bool written;
        Func<GeminiPowerBoxStatus, bool> shows;
        if (spec.Kind is SwitchChannelKind.Output)
        {
            var on = value >= 0.5;
            var output = channel < FirstUsb ? channel + 1 : GeminiPowerBoxProtocol.UsbOutputNumber(channel - FirstUsb);
            written = await GeminiPowerBoxProtocol.SwitchAsync(conn, output, on, cancellationToken).ConfigureAwait(false);
            shows = channel < FirstUsb
                ? status => status.DcOn[channel - FirstSwitchedDc] == on
                : status => status.UsbOn[channel - FirstUsb] == on;
        }
        else
        {
            var dew = channel == Dew6 ? 6 : 7;
            var mode = channel == Dew6 ? Fresh().Dew6Mode : Fresh().Dew7Mode;
            if (mode is not GeminiPowerBoxDewMode.Manual)
            {
                throw new InvalidOperationException($"{Name}: DEW{dew} is in {mode} mode on the box; its power is set only in Manual mode");
            }

            var percent = (int)Math.Round(value);
            written = await GeminiPowerBoxProtocol.SetDewPowerAsync(conn, dew, percent, cancellationToken).ConfigureAwait(false);
            shows = channel == Dew6 ? status => (int)Math.Round(status.Dew6Power) == percent : status => (int)Math.Round(status.Dew7Power) == percent;
        }

        if (!written)
        {
            throw new IOException($"{Name}: could not write {spec.Name}");
        }

        await ConfirmAsync(conn, spec.Name, value, shows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks for status at once and waits for a frame published after the write that shows the new value.</summary>
    private async ValueTask ConfirmAsync(ISerialConnection conn, string what, double value, Func<GeminiPowerBoxStatus, bool> shows, CancellationToken cancellationToken)
    {
        var before = Volatile.Read(ref _latest)?.Sequence ?? 0;
        _ = await GeminiPowerBoxProtocol.RequestStatusAsync(conn, cancellationToken).ConfigureAwait(false);
        if (!await WaitForFrameAsync(latest => latest.Sequence > before && shows(latest.Status), cancellationToken).ConfigureAwait(false))
        {
            throw new IOException($"{Name}: no status showed {what} at {value} within {ConfirmTimeout.TotalSeconds} s");
        }
    }

    /// <summary>
    /// Waits for the reader to publish a frame <paramref name="accepts"/> takes, up to <see cref="ConfirmTimeout"/> (a
    /// wall-clock bound, as the reads' own are); false when none came.
    /// </summary>
    private async ValueTask<bool> WaitForFrameAsync(Func<StatusAt, bool> accepts, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ConfirmTimeout);
        try
        {
            while (true)
            {
                var signal = Volatile.Read(ref _nextFrame).Task;
                if (Volatile.Read(ref _latest) is { } latest && accepts(latest))
                {
                    return true;
                }

                await signal.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void Validate(int channel)
    {
        if (channel < 0 || channel >= AllChannels.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"0 to {AllChannels.Count - 1}");
        }
    }

    private GeminiPowerBoxStatus Fresh()
        => Volatile.Read(ref _latest) is { } latest && TimeProvider.GetUtcNow() - latest.At <= StatusStaleAfter
            ? latest.Status
            : throw new IOException($"{Name}: no status from the box in the last {StatusStaleAfter.TotalSeconds} s");

    public string Name => device.DisplayName;

    public string? Description => "Gemini Power&Data Hub Advanced 3 power box";

    public string? DriverInfo => Description;

    public string? DriverVersion => typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "1.0";

    public bool Connected => _connected;

    public DeviceType DriverType => DeviceType.Switch;

    public IExternal External { get; } = serviceProvider.GetRequiredService<IExternal>();

    public ILogger Logger { get; } = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(GeminiPowerBoxDriver));

    public ITimeProvider TimeProvider { get; } = serviceProvider.GetRequiredService<ITimeProvider>();

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_conn is { IsOpen: true } && _connected)
        {
            return;
        }

        if (await device.ConnectSerialDeviceAsync(External, Logger, TimeProvider, GeminiPowerBoxProtocol.Baud, Encoding.ASCII, cancellationToken).ConfigureAwait(false)
            is not { IsOpen: true } conn)
        {
            throw new InvalidOperationException($"Could not open serial port for {device.DisplayName}");
        }

        try
        {
            await TimeProvider.SleepAsync(BootDelay, cancellationToken).ConfigureAwait(false);

            string? identity = null;
            for (var attempt = 0; attempt < HandshakeAttempts && identity is null; attempt++)
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(HandshakeTimeout);
                identity = await GeminiPowerBoxProtocol.IdentifyAsync(conn, attemptCts.Token).ConfigureAwait(false);
            }

            if (identity != GeminiPowerBoxProtocol.Identity)
            {
                throw new InvalidOperationException($"Device on {device.DeviceId} is not a Gemini Power&Data Hub Advanced 3 (identity: '{identity ?? "<none>"}')");
            }

            using (var firmwareCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                firmwareCts.CancelAfter(HandshakeTimeout);
                var firmware = await GeminiPowerBoxProtocol.GetFirmwareAsync(conn, firmwareCts.Token).ConfigureAwait(false);
                if (firmware is < GeminiPowerBoxProtocol.MinimumFirmware)
                {
                    throw new InvalidOperationException($"{device.DisplayName} runs firmware {firmware}; the protocol here is that of {GeminiPowerBoxProtocol.MinimumFirmware} and later");
                }

                Logger.LogInformation("Gemini power box {DeviceId}: firmware {Firmware}.", device.DeviceId, firmware?.ToString() ?? "?");
            }

            _conn = conn;
            var stop = new CancellationTokenSource();
            _readerStop = stop;
            _reader = Task.Run(() => ReadStatusAsync(conn, stop.Token), CancellationToken.None);

            _ = await GeminiPowerBoxProtocol.RequestStatusAsync(conn, cancellationToken).ConfigureAwait(false);
            if (!await WaitForFrameAsync(static _ => true, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"{device.DisplayName} answered its identity but sent no status");
            }
        }
        catch
        {
            await StopReaderAsync().ConfigureAwait(false);
            _conn = null;
            await conn.TryCloseAsync().ConfigureAwait(false);
            throw;
        }

        _connected = true;
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(true));
    }

    /// <summary>
    /// Takes every frame the box sends and keeps the latest status, as the port's only reader (see the remarks), and asks
    /// for status again when nothing has come for <see cref="RequestAgainAfter"/>.
    /// </summary>
    private async Task ReadStatusAsync(ISerialConnection conn, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                (char Letter, string Payload)? frame;
                using (var silence = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    silence.CancelAfter(RequestAgainAfter);
                    frame = await GeminiPowerBoxProtocol.ReadFrameAsync(conn, silence.Token).ConfigureAwait(false);
                }

                if (frame is { Letter: 'G' } status && GeminiPowerBoxProtocol.ParseStatus(status.Payload) is { } parsed)
                {
                    Volatile.Write(ref _latest, new StatusAt(parsed, TimeProvider.GetUtcNow(), (Volatile.Read(ref _latest)?.Sequence ?? 0) + 1));
                    Interlocked.Exchange(ref _nextFrame, NewSignal()).TrySetResult();
                }
                else if (frame is null && !stop.IsCancellationRequested)
                {
                    // Quiet for RequestAgainAfter, or a read that failed at once (bytes with no '#' in reach): a pause, so a
                    // failing read cannot spin the loop, then ask again.
                    await TimeProvider.SleepAsync(RetryAfterFault, stop).ConfigureAwait(false);
                    _ = await GeminiPowerBoxProtocol.RequestStatusAsync(conn, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Gemini power box {DeviceId}: status read failed; trying again.", device.DeviceId);
                await TimeProvider.SleepAsync(RetryAfterFault, stop).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask StopReaderAsync()
    {
        if (Interlocked.Exchange(ref _readerStop, null) is { } stop)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _reader, null) is { } reader)
            {
                try
                {
                    await reader.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            stop.Dispose();
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await StopReaderAsync().ConfigureAwait(false);
        var conn = _conn;
        _conn = null;
        if (conn is not null)
        {
            await conn.TryCloseAsync().ConfigureAwait(false);
        }

        Volatile.Write(ref _latest, null);
        _connected = false;
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(false));
    }

    // A synchronous Dispose cannot await the close: it stops the reader and starts the close, and DisposeAsync waits.
    public void Dispose()
    {
        _readerStop?.Cancel();
        _conn?.CloseInBackground(Logger);
    }

    public ValueTask DisposeAsync() => DisconnectAsync();

    private static List<SwitchChannel> BuildChannels()
    {
        var channels = new List<SwitchChannel>
        {
            new(Dc1, "DC1", "12 V DC output, always on", SwitchChannelKind.Output, 0, 1, 1, "", CanWrite: false),
        };

        for (var dc = 2; dc <= 5; dc++)
        {
            channels.Add(new(channels.Count, $"DC{dc}", "12 V DC output", SwitchChannelKind.Output, 0, 1, 1, "", CanWrite: true));
        }

        foreach (var usb in "ABCDEF")
        {
            channels.Add(new(channels.Count, $"USB {usb}", usb is 'A' or 'B' ? "USB 3.2 port" : "USB 2.0 port", SwitchChannelKind.Output, 0, 1, 1, "", CanWrite: true));
        }

        channels.Add(new(Dew6, "DEW6", "Dew heater, manual power when the box has it in Manual mode", SwitchChannelKind.Level, 0, 100, 1, "%", CanWrite: true));
        channels.Add(new(Dew7, "DEW7", "Dew heater, manual power when the box has it in Manual mode", SwitchChannelKind.Level, 0, 100, 1, "%", CanWrite: true));
        channels.Add(new(Voltage, "Voltage", "Input voltage", SwitchChannelKind.Reading, 0, 19, 0.1, "V", CanWrite: false));
        channels.Add(new(Current, "Current", "Total current of the 12 V outputs", SwitchChannelKind.Reading, 0, 15, 0.01, "A", CanWrite: false));
        channels.Add(new(Power, "Power", "Total power of the 12 V outputs", SwitchChannelKind.Reading, 0, 180, 0.01, "W", CanWrite: false));
        channels.Add(new(AmbientTemperature, "Ambient temperature", "The box's temperature sensor (AHT20)", SwitchChannelKind.Reading, -40, 85, 0.01, "°C", CanWrite: false));
        channels.Add(new(Humidity, "Humidity", "The box's humidity sensor (AHT20)", SwitchChannelKind.Reading, 0, 100, 0.01, "%", CanWrite: false));
        channels.Add(new(DewPoint, "Dew point", "Dew point, as the box computes it", SwitchChannelKind.Reading, -60, 60, 0.01, "°C", CanWrite: false));
        channels.Add(new(LensTemperature, "Lens temperature", "The lens probe (DS18B20)", SwitchChannelKind.Reading, -55, 125, 0.01, "°C", CanWrite: false));
        return channels;
    }
}
