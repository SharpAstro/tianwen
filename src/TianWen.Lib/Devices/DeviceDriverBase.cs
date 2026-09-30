using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

internal abstract class DeviceDriverBase<TDevice, TDeviceInfo>(TDevice device, IServiceProvider serviceProvider) : IDeviceDriver
    where TDevice : DeviceBase
    where TDeviceInfo : struct
{
    protected readonly TDevice _device = device;
    protected TDeviceInfo _deviceInfo;

    public virtual string Name => _device.DisplayName;

    public abstract string? DriverInfo { get; }

    public abstract string? Description { get; }

    public virtual string? DriverVersion { get; } = typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "0.0.1";

    public virtual DeviceType DriverType => _device.DeviceType;

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    public IExternal External { get; } = serviceProvider.GetRequiredService<IExternal>();

    public ILogger Logger { get; } = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(device.GetType().Name);

    public ITimeProvider TimeProvider { get; } = serviceProvider.GetRequiredService<ITimeProvider>();

    // CONNECTION_ID_UNKNOWN while nothing is open: 0 is a real id to an SDK that numbers its devices from 0.
    private int _connectionId = CONNECTION_ID_UNKNOWN;

    protected int ConnectionId => _connectionId;

    internal const int CONNECTION_ID_EXCLUSIVE = -100;
    internal const int CONNECTION_ID_UNKNOWN   = -200;

    const int CONNECTED = 1;
    const int CONNECTING = 2;
    const int DISCONNECTING = 9;
    const int DISCONNECTED = 10;
    const int CONNECTION_FAILURE = 99;

    private int _connectionState = DISCONNECTED;
    private bool disposedValue;

    // One connect or disconnect at a time (#806): an async transition awaits I/O, so a compare-and-swap on the
    // state cannot hold the second caller back while the first opens the port. Never disposed: a SemaphoreSlim
    // allocates a wait handle only when AvailableWaitHandle is read, which nothing here does.
    private readonly SemaphoreSlim _transition = new SemaphoreSlim(1, 1);

    public bool Connected => Volatile.Read(ref _connectionState) == CONNECTED;

    /// <summary>
    /// Opens the transport and initialises the device. A connect that fails, whether the transport would not open or the
    /// initialisation refused or threw, closes what it opened and throws, leaving the driver not connected, so the next
    /// connect runs the whole of it again rather than reading as a success (#806).
    /// </summary>
    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _connectionState) == CONNECTED)
            {
                return;
            }

            // A disconnect that failed left its transport open: closed first, or this would open a second one.
            if (Volatile.Read(ref _connectionId) is var stale and not CONNECTION_ID_UNKNOWN)
            {
                await ReleaseFailedConnectionAsync(stale);
            }

            Volatile.Write(ref _connectionState, CONNECTING);
            bool connectSuccess;
            int connectionId;
            try
            {
                (connectSuccess, connectionId, _deviceInfo) = await DoConnectDeviceAsync(cancellationToken);
            }
            catch
            {
                Volatile.Write(ref _connectionState, CONNECTION_FAILURE);
                throw;
            }

            if (!connectSuccess)
            {
                Volatile.Write(ref _connectionState, CONNECTION_FAILURE);
                throw new InvalidOperationException($"Could not connect to device {_device.DeviceId} ({_device.DisplayName})");
            }

            // CONNECTED while it initialises, since an initialisation talks to the device through the driver's own
            // members, and they may ask whether it is connected; the connection id is the one it was opened with.
            Volatile.Write(ref _connectionId, connectionId);
            Volatile.Write(ref _connectionState, CONNECTED);

            Exception? initFailure = null;
            bool initSuccess;
            try
            {
                initSuccess = await InitDeviceAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                initSuccess = false;
                initFailure = ex;
            }

            if (!initSuccess)
            {
                // A device that would not initialise is not connected, and the transport it was reached through goes
                // back: left open, a retry found the driver CONNECTED and skipped init, and its disconnect was handed
                // no connection id to close.
                Volatile.Write(ref _connectionState, CONNECTION_FAILURE);
                await ReleaseFailedConnectionAsync(connectionId);

                throw new InvalidOperationException($"Failed to initialise device {_device.DeviceId} ({_device.DisplayName})", initFailure);
            }

            DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(true));
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>
    /// Closes the transport. A driver with nothing open (it never connected, it was disconnected already, or a connect
    /// failed and released what it opened) has nothing to close, and this returns.
    /// </summary>
    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            // Nothing open: never connected, disconnected already, or a connect that failed and released what it opened.
            // A DISCONNECT that failed keeps its connection id, and is tried again.
            var state = Volatile.Read(ref _connectionState);
            if (state != CONNECTED && (state != CONNECTION_FAILURE || Volatile.Read(ref _connectionId) == CONNECTION_ID_UNKNOWN))
            {
                Volatile.Write(ref _connectionState, DISCONNECTED);
                return;
            }

            Volatile.Write(ref _connectionState, DISCONNECTING);
            bool disconnectSuccess;
            try
            {
                disconnectSuccess = await DoDisconnectDeviceAsync(Volatile.Read(ref _connectionId), cancellationToken);
            }
            catch
            {
                Volatile.Write(ref _connectionState, CONNECTION_FAILURE);
                throw;
            }

            if (!disconnectSuccess)
            {
                Volatile.Write(ref _connectionState, CONNECTION_FAILURE);
                throw new InvalidOperationException($"Could not disconnect device {_device.DeviceId} ({_device.DisplayName})");
            }

            Volatile.Write(ref _connectionId, CONNECTION_ID_UNKNOWN);
            Volatile.Write(ref _connectionState, DISCONNECTED);
            DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(false));
        }
        finally
        {
            _transition.Release();
        }
    }

    private async ValueTask ReleaseFailedConnectionAsync(int connectionId)
    {
        try
        {
            // Not the caller's token: a connect cancelled during its initialisation must still close what it opened.
            if (!await DoDisconnectDeviceAsync(connectionId, CancellationToken.None))
            {
                Logger.LogWarning("Could not close {DeviceId} ({DisplayName}) after a failed connect or disconnect", _device.DeviceId, _device.DisplayName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not close {DeviceId} ({DisplayName}) after a failed connect or disconnect: {ErrorMessage}", _device.DeviceId, _device.DisplayName, ex.Message);
        }
        finally
        {
            Volatile.Write(ref _connectionId, CONNECTION_ID_UNKNOWN);
        }
    }

    /// <summary>
    /// Called after a connect is successful, but before the events are issued. Only called once
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected virtual ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

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
