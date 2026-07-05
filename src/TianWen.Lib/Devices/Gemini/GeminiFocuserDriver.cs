using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Native (ASCOM-free) driver for the Gemini Focuser Pro, a serial absolute focuser built on a
/// <c>myFocuserPro2</c> Arduino controller. Drives the <see cref="GeminiFocuserProtocol"/> <c>:NN#</c> wire
/// codec over an <see cref="ISerialConnection"/> and maps it to <see cref="IFocuserDriver"/>. Works on any
/// platform with a COM port (no ASCOM Platform required).
/// </summary>
internal sealed class GeminiFocuserDriver(GeminiFocuserDevice device, IServiceProvider serviceProvider) : IFocuserDriver
{
    // The Arduino resets when the port opens (DTR auto-reset) and ignores input for ~2s while it boots — the
    // vendor ASCOM driver hard-sleeps on connect. We sleep through the boot before the first handshake so
    // early writes aren't dropped and their late replies can't desync later reads. Reconnect goes through the
    // liveness path (already-open conn), not here, so this cost is paid only on a genuine cold open.
    private static readonly TimeSpan BootDelay = TimeSpan.FromMilliseconds(2200);
    private const int HandshakeAttempts = 3;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HandshakeRetryInterval = TimeSpan.FromMilliseconds(400);

    private volatile ISerialConnection? _conn;
    private volatile bool _connected;

    // Immutable-ish capabilities cached at connect (a COM round-trip per read would be another place a
    // stalled controller could throw on the hot path).
    private int _maxStep = int.MaxValue;
    private double _stepSize = double.NaN;
    private bool _tempCompAvailable;

    public bool Absolute => true;

    public int MaxStep => _maxStep;

    // A myFocuserPro2 accepts an absolute move anywhere in [0, MaxStep], so the largest single increment is
    // the full travel — the vendor driver reports MaxStep for MaxIncrement too.
    public int MaxIncrement => _maxStep;

    public bool CanGetStepSize => !double.IsNaN(_stepSize);

    public double StepSize => _stepSize;

    public bool TempCompAvailable => _tempCompAvailable;

    // Unknown/unmeasured — TianWen's own backlash auto-tuning owns these (mirrors the ZWO/ASCOM native paths).
    public int BacklashStepsIn => -1;

    public int BacklashStepsOut => -1;

    public async ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default)
        => _conn is { IsOpen: true } conn && await GeminiFocuserProtocol.GetPositionAsync(conn, cancellationToken).ConfigureAwait(false) is { } pos
            ? pos
            : int.MinValue;

    public ValueTask<bool> GetIsMovingAsync(CancellationToken cancellationToken = default)
        => _conn is { IsOpen: true } conn
            ? GeminiFocuserProtocol.GetIsMovingAsync(conn, cancellationToken)
            : ValueTask.FromResult(false);

    public ValueTask<double> GetTemperatureAsync(CancellationToken cancellationToken = default)
        => _conn is { IsOpen: true } conn
            ? GeminiFocuserProtocol.GetTemperatureAsync(conn, cancellationToken)
            : ValueTask.FromResult(double.NaN);

    public ValueTask<bool> GetTempCompAsync(CancellationToken cancellationToken = default)
        => _conn is { IsOpen: true } conn && _tempCompAvailable
            ? GeminiFocuserProtocol.GetTempCompAsync(conn, cancellationToken)
            : ValueTask.FromResult(false);

    public async ValueTask SetTempCompAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (_conn is { IsOpen: true } conn && _tempCompAvailable)
        {
            await GeminiFocuserProtocol.SetTempCompAsync(conn, value, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (_conn is not { IsOpen: true } conn)
        {
            throw new InvalidOperationException($"{device.DisplayName} is not connected");
        }

        if (position is < 0 || position > _maxStep)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, $"Absolute position must be between 0 and {_maxStep}");
        }

        await GeminiFocuserProtocol.MoveAsync(conn, position, cancellationToken).ConfigureAwait(false);
    }

    public async Task BeginHaltAsync(CancellationToken cancellationToken = default)
    {
        if (_conn is { IsOpen: true } conn)
        {
            await GeminiFocuserProtocol.HaltAsync(conn, cancellationToken).ConfigureAwait(false);
        }
    }

    public string Name => device.DisplayName;

    public string? Description => "Gemini Focuser Pro serial focuser";

    public string? DriverInfo => Description;

    public string? DriverVersion => typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "1.0";

    public bool Connected => _connected;

    public DeviceType DriverType => DeviceType.Focuser;

    public IExternal External { get; } = serviceProvider.GetRequiredService<IExternal>();

    public ILogger Logger { get; } = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(GeminiFocuserDriver));

    public ITimeProvider TimeProvider { get; } = serviceProvider.GetRequiredService<ITimeProvider>();

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        // SerialPort.IsOpen only records that Open() succeeded — a dead USB bridge (unplugged CH34x,
        // re-enumerated port) keeps reporting IsOpen until an actual read/write fails. Re-verify with the
        // cheap :02# handshake so a reconnect (e.g. ResilientCall's fault callback) rebuilds the connection
        // instead of no-opping against a dead handle. TryClose evicts it from IExternal's per-address cache.
        if (_conn is { IsOpen: true } existing)
        {
            string? identity = null;
            try
            {
                identity = await GeminiFocuserProtocol.IdentifyAsync(existing, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "Gemini Focuser {DeviceId}: liveness handshake threw on a nominally open connection.", device.DeviceId);
            }

            if (identity == GeminiFocuserProtocol.PresentReply)
            {
                _connected = true;
                return;
            }

            Logger.LogWarning("Gemini Focuser {DeviceId}: open connection did not answer the handshake; rebuilding the connection.", device.DeviceId);
            existing.TryClose();
            _conn = null;
        }

        if (await device.ConnectSerialDeviceAsync(External, Logger, TimeProvider, GeminiFocuserProtocol.Baud, Encoding.ASCII, cancellationToken).ConfigureAwait(false)
            is not { IsOpen: true } conn)
        {
            throw new InvalidOperationException($"Could not open serial port for {device.DisplayName}");
        }

        try
        {
            // Sleep through the controller's power-on-reset boot before the first handshake (see BootDelay).
            await TimeProvider.SleepAsync(BootDelay, cancellationToken).ConfigureAwait(false);

            // Booted now: a couple of clean handshake attempts. Retry only on NO answer.
            string? identity = null;
            for (var attempt = 0; attempt < HandshakeAttempts; attempt++)
            {
                using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    attemptCts.CancelAfter(HandshakeTimeout);
                    try
                    {
                        identity = await GeminiFocuserProtocol.IdentifyAsync(conn, attemptCts.Token).ConfigureAwait(false);
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

            if (identity != GeminiFocuserProtocol.PresentReply)
            {
                throw new InvalidOperationException($"Device on {device.DeviceId} is not a Gemini Focuser (handshake: '{identity ?? "<none>"}')");
            }

            var firmware = await GeminiFocuserProtocol.GetFirmwareAsync(conn, cancellationToken).ConfigureAwait(false);
            if (firmware is { } fw)
            {
                Logger.LogInformation("Gemini Focuser {DeviceId}: firmware '{Name}' version {Version}.", device.DeviceId, fw.Name, fw.Version?.ToString() ?? "?");
            }

            // Cache the immutable-ish capabilities once (see field comment).
            if (await GeminiFocuserProtocol.GetMaxStepAsync(conn, cancellationToken).ConfigureAwait(false) is { } maxStep)
            {
                _maxStep = maxStep;
            }
            else
            {
                Logger.LogWarning("Gemini Focuser {DeviceId}: MaxStep query returned no value; defaulting to {Default}.", device.DeviceId, _maxStep);
            }

            _stepSize = await GeminiFocuserProtocol.GetStepSizeAsync(conn, cancellationToken).ConfigureAwait(false) is { } stepSize && stepSize > 0
                ? stepSize
                : double.NaN;

            _tempCompAvailable = await GeminiFocuserProtocol.GetTempCompAvailableAsync(conn, cancellationToken).ConfigureAwait(false);
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
