using Astap.Lib.Devices;
using System;

namespace Astap.Lib.Tests.Fakes;

internal abstract class FakeDeviceDriverBase(FakeDevice fakeDevice, IExternal external) : IDeviceDriver
{
    private readonly FakeDevice _fakeDevice = fakeDevice;
    private bool _connected;

    protected IExternal _external = external;
    protected bool disposedValue;

    public string Name => _fakeDevice.DisplayName;

    public string? Description => $"Fake device that implements a fake {DriverType}";

    public string? DriverInfo => Description;

    public string? DriverVersion => typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "1.0";

    public bool Connected
    {
        get => _connected;
        set
        {
            _connected = value;

            DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(value));
        }
    }

    public abstract DeviceType DriverType { get; }

    public IExternal External { get; } = external;

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
}
