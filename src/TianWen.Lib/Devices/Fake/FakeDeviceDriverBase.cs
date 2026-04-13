using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal abstract class FakeDeviceDriverBase(FakeDevice fakeDevice, IServiceProvider serviceProvider) : IDeviceDriver
{
    protected readonly FakeDevice _fakeDevice = fakeDevice;
    private bool _connected;

    protected bool disposedValue;

    public string Name => _fakeDevice.DisplayName;

    public string? Description => $"Fake device that implements a fake {DriverType}";

    public string? DriverInfo => Description;

    public string? DriverVersion => typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "1.0";

    public bool Connected => Volatile.Read(ref _connected);

    public DeviceType DriverType => _fakeDevice.DeviceType;

    public IExternal External { get; } = serviceProvider.GetRequiredService<IExternal>();

    public ILogger Logger { get; } = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(fakeDevice.GetType().Name);

    public ITimeProvider TimeProvider { get; } = serviceProvider.GetRequiredService<ITimeProvider>();

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetConnect(true);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetConnect(false);

        return ValueTask.CompletedTask;
    }

    private void SetConnect(bool connected)
    {
        Volatile.Write(ref _connected, connected);

        if (connected)
        {
            OnConnected();
        }

        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(connected));
    }

    /// <summary>Called after the device transitions to connected. Override to initialize state.</summary>
    protected virtual void OnConnected() { }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
