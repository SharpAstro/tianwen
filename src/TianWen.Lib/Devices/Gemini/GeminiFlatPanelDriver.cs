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
        if (_conn is { IsOpen: true })
        {
            _connected = true;
            return;
        }

        if (await device.ConnectSerialDeviceAsync(External, Logger, TimeProvider, GeminiFlatPanelProtocol.Baud, Encoding.ASCII, cancellationToken).ConfigureAwait(false)
            is not { IsOpen: true } conn)
        {
            throw new InvalidOperationException($"Could not open serial port for {device.DisplayName}");
        }

        try
        {
            var identity = await GeminiFlatPanelProtocol.IdentifyAsync(conn, cancellationToken).ConfigureAwait(false);
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
