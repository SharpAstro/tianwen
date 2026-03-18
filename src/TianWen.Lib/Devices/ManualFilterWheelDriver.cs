using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

/// <summary>
/// Filter wheel driver for a manual (fixed) filter holder — always at position 0
/// with a single installed filter. <see cref="BeginMoveAsync"/> is a no-op since
/// there is only one position. Used for cameras with a fixed filter (e.g., a
/// dual-band narrowband filter in a nose adapter or drop-in holder).
/// </summary>
internal sealed class ManualFilterWheelDriver(ManualFilterWheelDevice device, IExternal external) : IFilterWheelDriver
{
    private bool _connected;

    public IReadOnlyList<InstalledFilter> Filters => [new InstalledFilter(device.InstalledFilter)];

    public ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(0);

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public string Name => device.DisplayName;

    public string? Description => "Manual filter holder with a single fixed filter";

    public string? DriverInfo => Description;

    public string? DriverVersion => typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "1.0";

    public bool Connected => _connected;

    public DeviceType DriverType => DeviceType.FilterWheel;

    public IExternal External { get; } = external;

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(true));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(false));
        return ValueTask.CompletedTask;
    }

    public void Dispose() { }

    public ValueTask DisposeAsync() => DisconnectAsync();
}
