using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Ascom;

public abstract class AscomDeviceDriverBase(AscomDevice device, IExternal external) : DynamicComObject(device.DeviceId), IDeviceDriver
{
    private readonly AscomDevice _device = device;

    public string Name => _comObject?.Name as string ?? _device.DisplayName;

    public string? Description => _comObject?.Description as string;

    public string? DriverInfo => _comObject?.DriverInfo as string;

    public string? DriverVersion => _comObject?.DriverVersion as string;

    public DeviceType DriverType => _device.DeviceType;

    public IExternal External { get; } = external;

    [DebuggerHidden]
    public void SetupDialog() => _comObject?.SetupDialog();

    const int STATE_UNKNOWN = 0;
    const int CONNECTED = 1;
    const int DISCONNECTED = 2;
    private int _connectionState = STATE_UNKNOWN;
    public bool Connected
    {
        get
        {
            var state = Volatile.Read(ref _connectionState);
            switch (state)
            {
                case STATE_UNKNOWN:
                    if (_comObject?.Connected is bool connected)
                    {
                        Volatile.Write(ref _connectionState, connected ? CONNECTED : DISCONNECTED);
                        return connected;
                    }
                    return false;

                case CONNECTED:
                    return true;

                case DISCONNECTED:
                default:
                    return false;
            }
        }
    }

    protected void Connect(bool connect)
    {
        if (_comObject is { } obj)
        {
            if (obj.Connected is bool currentConnected)
            {
                var actualState = currentConnected ? CONNECTED : DISCONNECTED;
                var desiredState = connect ? CONNECTED : DISCONNECTED;
                var prevState = Volatile.Read(ref _connectionState);
                if (prevState != actualState || actualState != desiredState)
                {
                    Volatile.Write(ref _connectionState, desiredState);

                    DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(obj.Connected = connect));
                }
            }
            else
            {
                Volatile.Write(ref _connectionState, STATE_UNKNOWN);
            }
        }
        else
        {
            Volatile.Write(ref _connectionState, STATE_UNKNOWN);
        }
    }

    /// <summary>
    /// Connects device asynchronously.
    /// TODO: Support async interface in ASCOM 7
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => Connect(true), cancellationToken);
    }

    /// <summary>
    /// Disconnects device asynchronously.
    /// TODO: Support async interface in ASCOM 7
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => Connect(false), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();

        GC.SuppressFinalize(this);
    }

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;
}