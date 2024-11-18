using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

internal abstract class DeviceDriverBase<TDevice, TDeviceInfo>(TDevice device, IExternal external) : IDeviceDriver
    where TDevice : DeviceBase
    where TDeviceInfo : struct
{
    public delegate void ProcessDeviceInfoDelegate(in TDeviceInfo deviceInfo);

    protected readonly TDevice _device = device;
    protected TDeviceInfo _deviceInfo;

    public virtual string Name => _device.DisplayName;

    public abstract string? DriverInfo { get; }

    public abstract string? Description { get; }

    public virtual string? DriverVersion { get; } = typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "0.0.1";

    public virtual DeviceType DriverType => _device.DeviceType;

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    public IExternal External { get; } = external;

    private int _connectionId;

    protected int ConnectionId => _connectionId;

    protected void ProcessDeviceInfo(ProcessDeviceInfoDelegate processor) => processor(_deviceInfo);

    internal const int CONNECTION_ID_EXCLUSIVE = -100;
    internal const int CONNECTION_ID_UNKNOWN   = -200;

    const int CONNECTED = 1;
    const int CONNECTING = 2;
    const int DISCONNECTING = 9;
    const int DISCONNECTED = 10;
    const int CONNECTION_FAILURE = 99;

    private int _connectionState = DISCONNECTED;
    private bool disposedValue;

    public bool Connected => Interlocked.CompareExchange(ref _connectionState, CONNECTED, CONNECTED) == CONNECTED;

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => SetConnectionStateAsync(CONNECTED, cancellationToken);

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => SetConnectionStateAsync(DISCONNECTED, cancellationToken);

    private async ValueTask SetConnectionStateAsync(int desiredState, CancellationToken cancellationToken)
    {
        if (!await TrySetConnectionStateAsync(desiredState, cancellationToken))
        {
            var desiredStateStr = desiredState switch { CONNECTED => "connect", DISCONNECTED => "disconnect", _ => $"reach unknown state {desiredState}" };

            throw new InvalidOperationException($"Failed to {desiredStateStr} to device {_device.DeviceId} ({_device.DisplayName}), current state is {StateToString(Volatile.Read(ref _connectionState))}");
        }
    }

    /// <summary>
    /// Tries to transition device into <paramref name="desiredState"/> (either <see cref="CONNECTED"/> or <see cref="DISCONNECTED"/>) via intermediate states (<see cref="CONNECTING"/>, <see cref="DISCONNECTING"/>).
    /// Will trigger <see cref="DoConnectDeviceAsync(CancellationToken)"/> and <see cref="DoDisconnectDeviceAsync(int, CancellationToken)"/> <em>once</em> respectively.
    /// </summary>
    /// <param name="desiredState"></param>
    /// <returns><see langword="true"/> if desired state has been reached</returns>
    /// <exception cref="InvalidOperationException"></exception>
    private async ValueTask<bool> TrySetConnectionStateAsync(int desiredState, CancellationToken cancellationToken)
    {
        var wantsConnect = desiredState is CONNECTED;
        var intermediateState = wantsConnect ? CONNECTING : DISCONNECTING;
        var oppositeState = wantsConnect ? DISCONNECTED : CONNECTED;
        var prevState = Interlocked.CompareExchange(ref _connectionState, intermediateState, oppositeState);

        if (prevState == desiredState)
        {
            return true;
        }

        if (desiredState == CONNECTED)
        {
            (var connectSuccess, var connectionId, _deviceInfo) = await DoConnectDeviceAsync(cancellationToken);
            if (connectSuccess)
            {
                // only trigger connected event once
                if (Interlocked.CompareExchange(ref _connectionState, desiredState, intermediateState) == intermediateState)
                {
                    bool initSuccess; 
                    try
                    {
                        initSuccess = await InitDeviceAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        initSuccess = false;
                        // revert to failed state as initization failed.
                        Interlocked.CompareExchange(ref _connectionState, CONNECTION_FAILURE, desiredState);

                        External.AppLogger.LogError(ex, "Failed to initialize device {DeviceId} ({DisplayName}): {ErrorMessage}", _device.DeviceId, _device.DisplayName, ex.Message);
                    }

                    if (initSuccess)
                    {
                        Volatile.Write(ref _connectionId, connectionId);
                        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(wantsConnect));

                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            else if (Interlocked.CompareExchange(ref _connectionState, CONNECTION_FAILURE, intermediateState) == intermediateState)
            {
                throw new InvalidOperationException($"Could not connect to device {_device.DeviceId}");
            }
        }
        else if (desiredState == DISCONNECTED)
        {
            if (await DoDisconnectDeviceAsync(Volatile.Read(ref _connectionId), cancellationToken))
            {
                // only trigger disconnect event once
                if (Interlocked.CompareExchange(ref _connectionState, desiredState, intermediateState) == intermediateState)
                {
                    Volatile.Write(ref _connectionId, CONNECTION_ID_UNKNOWN);
                    DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(wantsConnect));

                    return true;
                }
            }
            else if (Interlocked.CompareExchange(ref _connectionState, CONNECTION_FAILURE, intermediateState) == intermediateState)
            {
                throw new InvalidOperationException($"Could not disconnect device {_device.DeviceId}");
            }
        }

        return Interlocked.CompareExchange(ref _connectionState, desiredState, desiredState) != desiredState;
    }

    /// <summary>
    /// Called after a connect is successful, but before the events are issued. Only called once
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected virtual ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    static string StateToString(int state) => state switch
    {
        CONNECTED => "connected",
        DISCONNECTED => "disconnected",
        CONNECTING => "connecting",
        DISCONNECTING => "disconnecting",
        CONNECTION_FAILURE => "failure",
        _ => $"unknown state {state}"
    };

    protected abstract Task<(bool Success, int ConnectionId, TDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken);

    protected abstract Task<bool> DoDisconnectDeviceAsync(int connectionId, CancellationToken cancellationToken);

    protected virtual ValueTask DisposeAsyncCore() => DisconnectAsync();

    protected virtual void DisposeUnmanaged()
    {
        // empty
    }

    public async ValueTask DisposeAsync()
    {
        // Perform async cleanup.
        await DisposeAsyncCore();

        // Dispose of unmanaged resources.
        Dispose(false);

        // Suppress finalization.
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (!disposing)
            {
                DisposeUnmanaged();
            }

            disposedValue = true;
        }
    }

    ~DeviceDriverBase()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
