using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.QHYCCD;

/// <summary>
/// Device info wrapper for the serial connection used by <see cref="QHYFocuserDriver"/>.
/// </summary>
internal record struct QHYFocuserInfo(ISerialConnection? SerialDevice);

/// <summary>
/// Focuser driver for QHY QFOC (Standard and High Precision) connected via USB-Serial.
/// Uses a JSON-over-serial protocol at 9600 baud.
///
/// <para><b>Supported URI query parameters:</b></para>
/// <list type="bullet">
///   <item><see cref="DeviceQueryKey.Port"/> — serial port name (required)</item>
///   <item><see cref="DeviceQueryKey.FocuserInitialPosition"/> — saved position to restore on connect</item>
///   <item><see cref="DeviceQueryKey.FocuserBacklashIn"/> — known backlash steps inward</item>
///   <item><see cref="DeviceQueryKey.FocuserBacklashOut"/> — known backlash steps outward</item>
/// </list>
///
/// <para><b>Protocol commands (JSON, 9600 baud):</b></para>
/// <list type="table">
///   <item><term>cmd_id 1 (init)</term><description>Returns firmware version + board version</description></item>
///   <item><term>cmd_id 3 (stop)</term><description>Halt movement immediately</description></item>
///   <item><term>cmd_id 4 (temp)</term><description>Returns temperature, StallGuard alarm, 12V status</description></item>
///   <item><term>cmd_id 5 (pos)</term><description>Returns current position</description></item>
///   <item><term>cmd_id 6 (runto)</term><description>Absolute move to target position</description></item>
///   <item><term>cmd_id 7</term><description>Set reverse direction flag</description></item>
///   <item><term>cmd_id 13 (set_speed)</term><description>Set motor speed (0=normal, -2=high)</description></item>
/// </list>
///
/// <para>Responses are JSON objects terminated by '}'. The <c>idx</c> field identifies the response type:
/// idx=1 → position arrived, idx=4 → temperature/status.</para>
/// </summary>
internal class QHYFocuserDriver(QHYDevice device, IServiceProvider serviceProvider)
    : DeviceDriverBase<QHYDevice, QHYFocuserInfo>(device, serviceProvider), IFocuserDriver
{
    internal const int QFOC_BAUD = 9600;

    /// <summary>
    /// Terminator byte for JSON responses from the QFOC firmware.
    /// </summary>
    private static readonly byte[] JsonTerminator = [(byte)'}'];

    private volatile int _position;
    private volatile int _targetPosition;
    private volatile bool _isMoving;
    private double _lastTemperature = double.NaN;
    private string? _firmwareVersion;
    private string? _boardVersion;
    private readonly bool _useExternalNtc = string.Equals(device.Query.QueryValue(DeviceQueryKey.Data), "ntc_ext", StringComparison.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    public override string? DriverInfo => _firmwareVersion is not null
        ? $"QHY QFOC Focuser Driver (FW {_firmwareVersion}, Board {_boardVersion})"
        : "QHY QFOC Focuser Driver";

    public override string? Description => "QHY QFOC serial protocol focuser driver";

    /// <summary>
    /// Firmware version string (yyyyMMdd format) returned by the init command, or <c>null</c> if not yet connected.
    /// </summary>
    public string? FirmwareVersion => _firmwareVersion;

    /// <summary>
    /// Board version string (e.g. "1.0", "2.0") distinguishing Standard vs High Precision models.
    /// </summary>
    public string? BoardVersion => _boardVersion;

    #region IFocuserDriver

    public bool Absolute => true;

    public int MaxIncrement => 1_000_000;

    public int MaxStep => 1_000_000;

    public bool CanGetStepSize => false;

    public double StepSize => double.NaN;

    public bool TempCompAvailable => false;

    public int BacklashStepsIn => int.TryParse(_device.Query.QueryValue(DeviceQueryKey.FocuserBacklashIn), out var bi) ? bi : -1;

    public int BacklashStepsOut => int.TryParse(_device.Query.QueryValue(DeviceQueryKey.FocuserBacklashOut), out var bo) ? bo : -1;

    public ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_position);

    public ValueTask<bool> GetIsMovingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_isMoving);

    public ValueTask<double> GetTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_lastTemperature);

    public ValueTask<bool> GetTempCompAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);

    public ValueTask SetTempCompAsync(bool value, CancellationToken cancellationToken = default)
        => throw new QHYDriverException("Temperature compensation is not supported on QFOC");

    public async Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (_deviceInfo.SerialDevice is not { IsOpen: true })
        {
            throw new QHYDriverException("Serial connection is not open");
        }

        _targetPosition = position;
        _isMoving = true;

        if (!await SendCommandAsync(new QfocRuntoCommand(position), QfocJsonContext.Default.QfocRuntoCommand, cancellationToken))
        {
            _isMoving = false;
            throw new QHYDriverException($"Failed to send runto command for position {position}");
        }
    }

    public async Task BeginHaltAsync(CancellationToken cancellationToken = default)
    {
        if (_deviceInfo.SerialDevice is not { IsOpen: true })
        {
            throw new QHYDriverException("Serial connection is not open");
        }

        if (!await SendCommandAsync(new QfocStopCommand(), QfocJsonContext.Default.QfocStopCommand, cancellationToken))
        {
            throw new QHYDriverException("Failed to send stop command");
        }

        _isMoving = false;
    }

    #endregion

    #region Connection lifecycle

    protected override Task<(bool Success, int ConnectionId, QHYFocuserInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_device.ConnectSerialDevice(External, QFOC_BAUD, Encoding.ASCII) is { IsOpen: true } conn)
            {
                return Task.FromResult((true, CONNECTION_ID_EXCLUSIVE, new QHYFocuserInfo(conn)));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to open serial port for QFOC focuser {DeviceId}", _device.DeviceId);
        }

        return Task.FromResult((false, CONNECTION_ID_UNKNOWN, default(QHYFocuserInfo)));
    }

    protected override Task<bool> DoDisconnectDeviceAsync(int connectionId, CancellationToken cancellationToken)
    {
        _deviceInfo.SerialDevice?.TryClose();
        return Task.FromResult(true);
    }

    protected override async ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        if (_deviceInfo.SerialDevice is not { IsOpen: true } serial)
        {
            return false;
        }

        // Drain any stale data from the serial buffer
        await DrainSerialBufferAsync(serial, cancellationToken);

        await Task.Delay(150, cancellationToken);

        // Send init command and read response
        var initResponse = await SendAndReadAsync<QfocInitCommand, QfocInitResponse>(
            serial, new QfocInitCommand(), QfocJsonContext.Default.QfocInitCommand, QfocJsonContext.Default.QfocInitResponse, cancellationToken);

        if (initResponse is null || initResponse.Version is not { Length: > 0 })
        {
            Logger.LogWarning("QFOC init response missing version");
            return false;
        }

        _firmwareVersion = initResponse.Version;
        _boardVersion = initResponse.BoardVersion;

        Logger.LogInformation("QFOC connected: firmware={FirmwareVersion}, board={BoardVersion}", _firmwareVersion, _boardVersion);

        // Set speed: normal (0) — caller can change via URI params or settings later
        await SendCommandAsync(new QfocSetSpeedCommand(0), QfocJsonContext.Default.QfocSetSpeedCommand, cancellationToken);
        await Task.Delay(200, cancellationToken);

        // Query initial position
        await PollPositionAsync(serial, cancellationToken);

        // Query initial temperature
        await PollTemperatureAsync(serial, cancellationToken);

        return true;
    }

    #endregion

    #region Polling

    /// <summary>
    /// Polls the current position from the focuser. Called periodically by the session or on demand.
    /// Updates <see cref="_position"/> and <see cref="_isMoving"/> state.
    /// </summary>
    internal async ValueTask PollPositionAsync(ISerialConnection serial, CancellationToken cancellationToken)
    {
        var response = await SendAndReadAsync<QfocPosCommand, QfocPositionResponse>(
            serial, new QfocPosCommand(), QfocJsonContext.Default.QfocPosCommand, QfocJsonContext.Default.QfocPositionResponse, cancellationToken);

        if (response is not null)
        {
            _position = response.Pos;
            _isMoving = _position != _targetPosition;
        }
    }

    /// <summary>
    /// Polls temperature and 12V status from the focuser.
    /// Updates <see cref="_lastTemperature"/> state.
    /// The QFOC has dual temperature sensors: external NTC (<c>o_t</c>) and chip (<c>c_t</c>),
    /// both reported in milli-degrees (divide by 1000 for °C).
    /// </summary>
    internal async ValueTask PollTemperatureAsync(ISerialConnection serial, CancellationToken cancellationToken)
    {
        var response = await SendAndReadAsync<QfocTempCommand, QfocTemperatureResponse>(
            serial, new QfocTempCommand(), QfocJsonContext.Default.QfocTempCommand, QfocJsonContext.Default.QfocTemperatureResponse, cancellationToken);

        if (response is not null)
        {
            var milliC = _useExternalNtc ? response.ExternalNtcMilliC : response.ChipTempMilliC;
            _lastTemperature = Math.Round(milliC / 1000.0, 1);
        }
    }

    #endregion

    #region Serial protocol helpers

    /// <summary>
    /// Sends a typed command and deserialises the response, or returns <c>null</c> on failure.
    /// </summary>
    private async ValueTask<TResponse?> SendAndReadAsync<TCommand, TResponse>(
        ISerialConnection serial,
        TCommand command,
        JsonTypeInfo<TCommand> commandTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        if (!await SendCommandAsync(command, commandTypeInfo, cancellationToken))
        {
            return null;
        }
        await Task.Delay(50, cancellationToken);
        return await ReadJsonResponseAsync(serial, responseTypeInfo, cancellationToken);
    }

    /// <summary>
    /// Sends a typed command to the QFOC. Acquires the serial and command locks.
    /// </summary>
    private async ValueTask<bool> SendCommandAsync<TCommand>(TCommand command, JsonTypeInfo<TCommand> typeInfo, CancellationToken cancellationToken)
    {
        if (_deviceInfo.SerialDevice is not { IsOpen: true } serial)
        {
            return false;
        }

        var json = JsonSerializer.Serialize(command, typeInfo);

        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            using var @lock = await serial.WaitAsync(cancellationToken);
            return await serial.TryWriteAsync(json, cancellationToken);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>
    /// Reads a JSON response from the QFOC terminated by '}' and deserialises it.
    /// </summary>
    private static async ValueTask<TResponse?> ReadJsonResponseAsync<TResponse>(
        ISerialConnection serial,
        JsonTypeInfo<TResponse> typeInfo,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        using var @lock = await serial.WaitAsync(cancellationToken);
        var response = await serial.TryReadTerminatedAsync(JsonTerminator, cancellationToken);
        if (response is { Length: > 0 })
        {
            // TryReadTerminatedAsync strips the terminator, add it back for valid JSON
            return JsonSerializer.Deserialize(response + "}", typeInfo);
        }
        return null;
    }

    /// <summary>
    /// Drains any stale data from the serial buffer by reading with a short timeout.
    /// </summary>
    private static async ValueTask DrainSerialBufferAsync(ISerialConnection serial, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(100));
            using var @lock = await serial.WaitAsync(cts.Token);
            _ = await serial.TryReadExactlyAsync(1024, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected — drain timeout
        }
    }

    #endregion

    #region Probing

    /// <summary>
    /// Probes a serial port to check if a QFOC focuser is connected by sending the init command.
    /// Returns a tuple of (firmwareVersion, boardVersion) on success, or <c>null</c> if not a QFOC.
    /// </summary>
    internal static async ValueTask<(string FirmwareVersion, string BoardVersion)?> ProbeAsync(ISerialConnection serial, CancellationToken cancellationToken)
    {
        // Drain stale data
        await DrainSerialBufferAsync(serial, cancellationToken);

        await Task.Delay(150, cancellationToken);

        // Send init command
        var initJson = JsonSerializer.Serialize(new QfocInitCommand(), QfocJsonContext.Default.QfocInitCommand);
        using (var writeLock = await serial.WaitAsync(cancellationToken))
        {
            if (!await serial.TryWriteAsync(initJson, cancellationToken))
            {
                return null;
            }
        }

        await Task.Delay(300, cancellationToken);

        // Read and deserialise response
        var response = await ReadJsonResponseAsync(serial, QfocJsonContext.Default.QfocInitResponse, cancellationToken);
        if (response?.Version is not { Length: > 0 } firmware)
        {
            return null;
        }

        return (firmware, response.BoardVersion ?? "???");
    }

    #endregion
}
