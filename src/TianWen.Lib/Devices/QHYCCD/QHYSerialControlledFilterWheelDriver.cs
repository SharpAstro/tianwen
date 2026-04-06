using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.QHYCCD;

/// <summary>
/// Device info wrapper for the serial connection used by <see cref="QHYSerialControlledFilterWheelDriver"/>.
/// </summary>
internal record struct QHYSerialFilterWheelInfo(ISerialConnection? SerialDevice);

/// <summary>
/// Filter wheel driver for standalone QHY CFW (e.g. QHYCFW3) connected via USB-to-serial.
/// Uses the QHYCFW3 ASCII protocol: single-char hex goto commands, VRS/MXP/NOW queries.
/// </summary>
internal class QHYSerialControlledFilterWheelDriver(QHYDevice device, IExternal external)
    : DeviceDriverBase<QHYDevice, QHYSerialFilterWheelInfo>(device, external), IFilterWheelDriver
{
    internal const int CFW_BAUD = 9600;

    private int _filterCount;
    private volatile bool _moveStarted;

    public IReadOnlyList<InstalledFilter> Filters
    {
        get
        {
            if (_filterCount > 0)
            {
                var filters = new List<InstalledFilter>(_filterCount);

                for (var i = 0; i < _filterCount; i++)
                {
                    filters.Add(new InstalledFilter(_device.Query[DeviceQueryKeyExtensions.FilterKey(i + 1)] ?? $"Filter {i + 1}",
                        int.TryParse(_device.Query[DeviceQueryKeyExtensions.FilterOffsetKey(i + 1)], out int focusOffset) ? focusOffset : 0));
                }

                return filters;
            }

            return [];
        }
    }

    public override string? DriverInfo => $"QHY Serial Filter Wheel Driver v{DriverVersion}";

    public override string? Description => "QHY QHYCFW3 serial protocol driver";

    protected override Task<(bool Success, int ConnectionId, QHYSerialFilterWheelInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_device.ConnectSerialDevice(External, CFW_BAUD, Encoding.ASCII) is { IsOpen: true } conn)
            {
                return Task.FromResult((true, CONNECTION_ID_EXCLUSIVE, new QHYSerialFilterWheelInfo(conn)));
            }
        }
        catch (Exception)
        {
            // Failed to open serial port
        }

        return Task.FromResult((false, CONNECTION_ID_UNKNOWN, default(QHYSerialFilterWheelInfo)));
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

        // Query slot count via MXP command
        using var @lock = await serial.WaitAsync(cancellationToken);
        if (!await serial.TryWriteAsync("MXP", cancellationToken))
        {
            return false;
        }

        // MXP returns 1 byte: hex digit where value + 1 = number of positions
        var mxpResp = await serial.TryReadExactlyAsync(1, cancellationToken);
        if (mxpResp is { Length: 1 } && int.TryParse(mxpResp, NumberStyles.HexNumber, null, out var maxPos))
        {
            _filterCount = maxPos + 1;
            return _filterCount > 0;
        }

        return false;
    }

    public async ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        if (_deviceInfo.SerialDevice is not { IsOpen: true } serial)
        {
            throw new QHYDriverException("Serial connection is not open");
        }

        using var @lock = await serial.WaitAsync(cancellationToken);

        if (_moveStarted)
        {
            // A goto command was sent — the wheel sends back the position when it arrives.
            // The response may be multi-char for positions >= 10, read until no more data.
            // If the wheel is still moving, the read blocks until arrival.
            var gotoResp = await serial.TryReadExactlyAsync(1, cancellationToken);
            _moveStarted = false;
            if (gotoResp is { Length: > 0 } && int.TryParse(gotoResp, out var arrived))
            {
                return arrived;
            }
        }

        // Query current position via NOW command
        if (!await serial.TryWriteAsync("NOW", cancellationToken))
        {
            throw new QHYDriverException("Failed to send NOW command");
        }

        var resp = await serial.TryReadExactlyAsync(1, cancellationToken);
        if (resp is { Length: > 0 } && int.TryParse(resp, out var pos))
        {
            return pos;
        }

        return -1;
    }

    public async Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (_deviceInfo.SerialDevice is not { IsOpen: true } serial)
        {
            throw new QHYDriverException("Serial connection is not open");
        }

        using var @lock = await serial.WaitAsync(cancellationToken);

        // Drain any pending response from a previous goto that wasn't consumed
        if (_moveStarted)
        {
            _ = await serial.TryReadExactlyAsync(1, cancellationToken);
            _moveStarted = false;
        }

        // Goto command: 0-based position as string — the ASCOM driver sends decimal
        // Protocol uses '0'-'9' for positions 1-10, 'A'-'F' for 11-16 (hex encoding)
        var cmd = position.ToString();
        if (!await serial.TryWriteAsync(cmd, cancellationToken))
        {
            throw new QHYDriverException($"Failed to send goto command for position {position}");
        }

        // Don't wait for response — the wheel sends back the position when it arrives.
        // GetPositionAsync will consume it.
        _moveStarted = true;
    }

    /// <summary>
    /// Probes a serial port to check if a QHYCFW3 is connected by sending the VRS command.
    /// Returns the firmware version string (yyyymmdd) on success, or <c>null</c> if not a QHYCFW3.
    /// </summary>
    internal static async ValueTask<string?> ProbeAsync(ISerialConnection serial, CancellationToken cancellationToken)
    {
        using var @lock = await serial.WaitAsync(cancellationToken);

        if (!await serial.TryWriteAsync("VRS", cancellationToken))
        {
            return null;
        }

        // VRS returns 8 bytes: yyyymmdd
        var resp = await serial.TryReadExactlyAsync(8, cancellationToken);
        if (resp is { Length: 8 } && int.TryParse(resp[..4], out var year) && year >= 2014 && year <= 2099)
        {
            return resp;
        }

        return null;
    }

    /// <summary>
    /// Queries the slot count from an already-probed QHYCFW3 via the MXP command.
    /// Returns the number of positions (e.g. 5, 7, 10, 16), or 0 on failure.
    /// MXP returns a single hex digit: the max 0-based position index (4→5 slots, 6→7, 9→10, F→16).
    /// </summary>
    internal static async ValueTask<int> QuerySlotCountAsync(ISerialConnection serial, CancellationToken cancellationToken)
    {
        using var @lock = await serial.WaitAsync(cancellationToken);

        if (!await serial.TryWriteAsync("MXP", cancellationToken))
        {
            return 0;
        }

        var resp = await serial.TryReadExactlyAsync(1, cancellationToken);
        if (resp is { Length: 1 } && int.TryParse(resp, NumberStyles.HexNumber, null, out var maxPosIndex))
        {
            // Response is max 0-based position index, so slot count = index + 1
            return maxPosIndex + 1;
        }

        return 0;
    }
}
