using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Native (ASCOM-free) driver for the Gemini FlatPanel Lite, a serial cover/calibrator. It is a
/// driver-controlled light panel with 0-255 brightness and <b>no motorised cover flap</b>, so it maps to an
/// <see cref="ICoverDriver"/> that reports <see cref="CoverStatus.NotPresent"/> and drives the calibrator over
/// the serial <see cref="GeminiFlatPanelProtocol"/>. Works on any platform with a COM port (no ASCOM Platform
/// required).
/// </summary>
internal sealed class GeminiFlatPanelDriver(GeminiDevice device, IServiceProvider serviceProvider) : ICoverDriver
{
    // Gap between turning the light on (>L#) and setting brightness (>B#), matching the vendor timing so the
    // controller reliably registers both.
    private static readonly TimeSpan CommandSettle = TimeSpan.FromMilliseconds(250);

    // The controller reboots when the port opens (the USB bridge toggles DTR/RTS) and ignores input for ~2s
    // while it boots — the vendor ASCOM driver hard-sleeps 2000ms here. We SLEEP THROUGH the boot before the
    // first handshake: writing >H# during the boot just yields dropped writes and, once it wakes, a storm of
    // duplicate replies that desync every later read. After the sleep it is booted, so a couple of clean
    // handshake attempts suffice. Reconnect goes through the liveness path (already-open conn), not here, so
    // this ~2s cost is paid only on a genuine cold open.
    private static readonly TimeSpan BootDelay = TimeSpan.FromMilliseconds(2200);
    private const int HandshakeAttempts = 3;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HandshakeRetryInterval = TimeSpan.FromMilliseconds(400);

    private volatile ISerialConnection? _conn;
    private volatile bool _connected;

    public int MaxBrightness => GeminiFlatPanelProtocol.MaxBrightness;

    // A flat light panel has no motorised cover flap.
    public ValueTask<CoverStatus> GetCoverStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CoverStatus.NotPresent);

    public ValueTask<CalibratorStatus> GetCalibratorStateAsync(CancellationToken cancellationToken = default)
        => _conn is { IsOpen: true } conn
            ? GeminiFlatPanelProtocol.GetCalibratorStateAsync(conn, cancellationToken)
            : ValueTask.FromResult(CalibratorStatus.Unknown);

    public async ValueTask<int> GetBrightnessAsync(CancellationToken cancellationToken = default)
        => _conn is { IsOpen: true } conn
            ? await GeminiFlatPanelProtocol.GetBrightnessAsync(conn, cancellationToken).ConfigureAwait(false)
            : 0;

    // No motorised flap to move.
    public Task BeginOpen(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task BeginClose(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task BeginCalibratorOn(int brightness, CancellationToken cancellationToken = default)
    {
        if (_conn is not { IsOpen: true } conn)
        {
            return;
        }

        await GeminiFlatPanelProtocol.SetLightAsync(conn, on: true, cancellationToken).ConfigureAwait(false);
        await TimeProvider.SleepAsync(CommandSettle, cancellationToken).ConfigureAwait(false);
        await GeminiFlatPanelProtocol.SetBrightnessAsync(conn, brightness, cancellationToken).ConfigureAwait(false);
    }

    public async Task BeginCalibratorOff(CancellationToken cancellationToken = default)
    {
        if (_conn is { IsOpen: true } conn)
        {
            await GeminiFlatPanelProtocol.SetLightAsync(conn, on: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Diagnostic hook (not part of <see cref="ICoverDriver"/>): pulses the panel beeper
    /// (<c>&gt;T1#</c> / <c>&gt;T0#</c>). Used by the live-hardware test for an audible confirmation.</summary>
    internal async Task SetBeeperAsync(bool on, CancellationToken cancellationToken = default)
    {
        if (_conn is { IsOpen: true } conn)
        {
            await GeminiFlatPanelProtocol.SetBeeperAsync(conn, on, cancellationToken).ConfigureAwait(false);
        }
    }

    public string Name => device.DisplayName;

    public string? Description => "Gemini FlatPanel Lite serial cover/calibrator";

    public string? DriverInfo => Description;

    public string? DriverVersion => typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "1.0";

    public bool Connected => _connected;

    public DeviceType DriverType => DeviceType.CoverCalibrator;

    public IExternal External { get; } = serviceProvider.GetRequiredService<IExternal>();

    public ILogger Logger { get; } = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(GeminiFlatPanelDriver));

    public ITimeProvider TimeProvider { get; } = serviceProvider.GetRequiredService<ITimeProvider>();

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        // SerialPort.IsOpen only records that Open() succeeded -- a dead USB bridge (unplugged CH341,
        // re-enumerated port) keeps reporting IsOpen until an actual read/write fails. Re-verify with the
        // cheap identity handshake so a reconnect (e.g. ResilientCall's fault callback, which calls
        // ConnectAsync directly) rebuilds the connection instead of no-opping against a dead handle.
        // TryClose marks the stale connection not-open, which also evicts it from IExternal's
        // per-address connection cache on the reopen below.
        if (_conn is { IsOpen: true } existing)
        {
            string? identity = null;
            try
            {
                identity = await GeminiFlatPanelProtocol.IdentifyAsync(existing, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "Gemini FlatPanel {DeviceId}: liveness handshake threw on a nominally open connection.", device.DeviceId);
            }

            if (identity == GeminiFlatPanelProtocol.Identity)
            {
                _connected = true;
                return;
            }

            Logger.LogWarning("Gemini FlatPanel {DeviceId}: open connection did not answer the identity handshake; rebuilding the connection.", device.DeviceId);
            existing.TryClose();
            _conn = null;
        }

        if (await device.ConnectSerialDeviceAsync(External, Logger, TimeProvider, GeminiFlatPanelProtocol.Baud, Encoding.ASCII, cancellationToken).ConfigureAwait(false)
            is not { IsOpen: true } conn)
        {
            throw new InvalidOperationException($"Could not open serial port for {device.DisplayName}");
        }

        try
        {
            // Sleep through the controller's power-on-reset boot before the first handshake (see BootDelay).
            await TimeProvider.SleepAsync(BootDelay, cancellationToken).ConfigureAwait(false);

            // Booted now: a couple of clean handshake attempts. Retry only on NO answer; a definitive wrong
            // identity means a different device, so fail fast. QueryAsync clears stale bytes before each read.
            string? identity = null;
            for (var attempt = 0; attempt < HandshakeAttempts; attempt++)
            {
                using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    attemptCts.CancelAfter(HandshakeTimeout);
                    try
                    {
                        identity = await GeminiFlatPanelProtocol.IdentifyAsync(conn, attemptCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        identity = null;
                    }
                }

                if (identity is not null)
                {
                    break;
                }

                await TimeProvider.SleepAsync(HandshakeRetryInterval, cancellationToken).ConfigureAwait(false);
            }

            if (identity != GeminiFlatPanelProtocol.Identity)
            {
                throw new InvalidOperationException($"Device on {device.DeviceId} is not a Gemini FlatPanel (identity: '{identity ?? "<none>"}')");
            }

            var firmware = await GeminiFlatPanelProtocol.GetFirmwareVersionAsync(conn, cancellationToken).ConfigureAwait(false);
            if (firmware is { } fw && fw < GeminiFlatPanelProtocol.MinFirmwareVersion)
            {
                Logger.LogWarning("Gemini FlatPanel firmware {Firmware} is older than the recommended minimum {Min}; consider upgrading.",
                    fw, GeminiFlatPanelProtocol.MinFirmwareVersion);
            }
        }
        catch
        {
            conn.TryClose();
            throw;
        }

        _conn = conn;
        _connected = true;
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(true));
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _conn?.TryClose();
        _conn = null;
        _connected = false;
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(false));
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _conn?.TryClose();

    public ValueTask DisposeAsync() => DisconnectAsync();
}
