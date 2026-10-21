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
    /// here loses nothing, since nothing was arriving. A test that moves the fake clock lengthens it, so the real clock
    /// cannot ask for a frame in the middle.
    /// </summary>
    internal TimeSpan RequestAgainAfter { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Requests the reader may fail to write in a row before it takes the port for gone: the box's serial port leaves with
    /// its 12 V or its cable, and then every write fails.
    /// </summary>
    private const int FailedRequestsBeforeLost = 3;

    /// <summary>How long a disconnect waits for the reader once the port is closed under it.</summary>
    private static readonly TimeSpan ReaderStopTimeout = TimeSpan.FromSeconds(3);

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

    // Numbers every frame this driver ever read, across connections, so a set waiting on a frame "after" its write cannot
    // be fooled by a reconnect restarting the count.
    private long _sequence;

    // Completed by the reader each time it publishes a frame and replaced at once: a waiter takes the current one BEFORE it
    // looks at the latest frame, so a frame published between the look and the wait still wakes it.
    private TaskCompletionSource _nextFrame = NewSignal();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A status frame, when it was read, and its number in the driver's sequence of frames.</summary>
    private sealed record StatusAt(GeminiPowerBoxStatus Status, DateTimeOffset At, long Sequence);

    public IReadOnlyList<SwitchChannel> Channels => AllChannels;

    /// <summary>The latest frame and when it was read, for the tests and the node's telemetry; null before the first.</summary>
    internal (GeminiPowerBoxStatus Status, DateTimeOffset At)? LatestStatus => Volatile.Read(ref _latest) is { } latest ? (latest.Status, latest.At) : null;

    public ValueTask<double> GetValueAsync(int channel, CancellationToken cancellationToken = default)
    {
        Validate(channel);
        if (!_connected)
        {
            throw new InvalidOperationException($"{Name} is not connected");
        }

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

        if (!_connected || _conn is not { IsOpen: true } conn)
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
        var before = Interlocked.Read(ref _sequence);
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
        if (_connected && _conn is { IsOpen: true })
        {
            return;
        }

        // Whatever an earlier connection left (a port that died under it, a reader still running) goes first, so a
        // reconnect without a disconnect neither leaks the old reader nor answers from the old connection's frame.
        await TearDownAsync().ConfigureAwait(false);

        if (await device.ConnectSerialDeviceAsync(External, Logger, TimeProvider, GeminiPowerBoxProtocol.Baud, Encoding.ASCII, cancellationToken).ConfigureAwait(false)
            is not { IsOpen: true } conn)
        {
            throw new InvalidOperationException($"Could not open serial port for {device.DisplayName}");
        }

        try
        {
            await TimeProvider.SleepAsync(BootDelay, cancellationToken).ConfigureAwait(false);

            var identity = await AskAsync(conn, GeminiPowerBoxProtocol.IdentifyAsync, cancellationToken).ConfigureAwait(false);
            if (identity != GeminiPowerBoxProtocol.Identity)
            {
                throw new InvalidOperationException($"Device on {device.DeviceId} is not a Gemini Power&Data Hub Advanced 3 (identity: '{identity ?? "<none>"}')");
            }

            var firmware = await AskAsync(conn, GeminiPowerBoxProtocol.GetFirmwareAsync, cancellationToken).ConfigureAwait(false);
            if (firmware is not >= GeminiPowerBoxProtocol.MinimumFirmware)
            {
                throw new InvalidOperationException(firmware is { } fw
                    ? $"{device.DisplayName} runs firmware {fw}; the protocol here is that of {GeminiPowerBoxProtocol.MinimumFirmware} and later"
                    : $"{device.DisplayName} did not say its firmware");
            }

            Logger.LogInformation("Gemini power box {DeviceId}: firmware {Firmware}.", device.DeviceId, firmware);

            _conn = conn;
            var stop = new CancellationTokenSource();
            _readerStop = stop;
            _reader = Task.Run(() => ReadStatusAsync(conn, stop.Token), CancellationToken.None);

            var before = Interlocked.Read(ref _sequence);
            _ = await GeminiPowerBoxProtocol.RequestStatusAsync(conn, cancellationToken).ConfigureAwait(false);
            if (!await WaitForFrameAsync(latest => latest.Sequence > before, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"{device.DisplayName} answered its identity but sent no status");
            }
        }
        catch
        {
            await TearDownAsync(conn).ConfigureAwait(false);
            throw;
        }

        _connected = true;
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(true));
    }

    /// <summary>Asks a query up to <see cref="HandshakeAttempts"/> times, <see cref="HandshakeTimeout"/> each, until it answers.</summary>
    private static async ValueTask<string?> AskAsync(ISerialConnection conn, Func<ISerialConnection, CancellationToken, ValueTask<string?>> query, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < HandshakeAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(HandshakeTimeout);
            if (await query(conn, attemptCts.Token).ConfigureAwait(false) is { } answer)
            {
                return answer;
            }
        }

        return null;
    }

    /// <inheritdoc cref="AskAsync(ISerialConnection, Func{ISerialConnection, CancellationToken, ValueTask{string?}}, CancellationToken)"/>
    private static async ValueTask<int?> AskAsync(ISerialConnection conn, Func<ISerialConnection, CancellationToken, ValueTask<int?>> query, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < HandshakeAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(HandshakeTimeout);
            if (await query(conn, attemptCts.Token).ConfigureAwait(false) is { } answer)
            {
                return answer;
            }
        }

        return null;
    }

    /// <summary>
    /// Takes every frame the box sends and keeps the latest status, as the port's only reader (see the remarks), and asks
    /// for status again when nothing has come for <see cref="RequestAgainAfter"/>. A port that has gone (closed under the
    /// reader, or refusing <see cref="FailedRequestsBeforeLost"/> requests in a row) ends the loop and the connection.
    /// </summary>
    private async Task ReadStatusAsync(ISerialConnection conn, CancellationToken stop)
    {
        var failedRequests = 0;
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
                    failedRequests = 0;
                    Volatile.Write(ref _latest, new StatusAt(parsed, TimeProvider.GetUtcNow(), Interlocked.Increment(ref _sequence)));
                    Interlocked.Exchange(ref _nextFrame, NewSignal()).TrySetResult();
                }
                else if (frame is null && !stop.IsCancellationRequested)
                {
                    if (!conn.IsOpen)
                    {
                        Lost("its port closed");
                        return;
                    }

                    // Quiet for RequestAgainAfter, or a read that failed at once (bytes with no '#' in reach): a pause, so a
                    // failing read cannot spin the loop, then ask again.
                    await TimeProvider.SleepAsync(RetryAfterFault, stop).ConfigureAwait(false);
                    if (await GeminiPowerBoxProtocol.RequestStatusAsync(conn, stop).ConfigureAwait(false))
                    {
                        failedRequests = 0;
                    }
                    else if (++failedRequests >= FailedRequestsBeforeLost)
                    {
                        Lost($"{failedRequests} status requests in a row could not be written");
                        return;
                    }
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

    /// <summary>The port went away under a connection: say so once, and stop answering as connected.</summary>
    private void Lost(string why)
    {
        Logger.LogWarning("Gemini power box {DeviceId}: lost, {Why}; reconnect to use it again.", device.DeviceId, why);
        if (_connected)
        {
            _connected = false;
            DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(false));
        }
    }

    /// <summary>
    /// Stops the reader and closes the port: the reader is cancelled, the port closed (which ends any read that does not
    /// heed its token), and only then is the reader awaited, for at most <see cref="ReaderStopTimeout"/>. Forgets the
    /// connection's frames.
    /// </summary>
    private async ValueTask TearDownAsync(ISerialConnection? failed = null)
    {
        var stop = Interlocked.Exchange(ref _readerStop, null);
        var reader = Interlocked.Exchange(ref _reader, null);
        if (stop is not null)
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }

        var conn = _conn ?? failed;
        _conn = null;
        if (conn is not null)
        {
            await conn.TryCloseAsync().ConfigureAwait(false);
        }

        if (reader is not null)
        {
            try
            {
                await reader.WaitAsync(ReaderStopTimeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Cancelled as asked, or still inside a read the closed port has not ended: it ends with that read.
            }
        }

        stop?.Dispose();
        Volatile.Write(ref _latest, null);
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await TearDownAsync().ConfigureAwait(false);
        _connected = false;
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(false));
    }

    // A synchronous Dispose cannot await the close: it stops the reader and starts the close, and DisposeAsync waits.
    public void Dispose()
    {
        Interlocked.Exchange(ref _readerStop, null)?.Cancel();
        var conn = _conn;
        _conn = null;
        _connected = false;
        conn?.CloseInBackground(Logger);
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
