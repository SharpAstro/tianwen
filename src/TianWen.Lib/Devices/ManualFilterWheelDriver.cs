using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
internal sealed class ManualFilterWheelDriver(ManualFilterWheelDevice device, IServiceProvider serviceProvider) : IFilterWheelDriver
{
    private bool _connected;

    public IReadOnlyList<InstalledFilter> Filters
    {
        get
        {
            // Single slot — name and offset from URI query params (profile is source of truth)
            var query = device.Query;
            var name = query[DeviceQueryKeyExtensions.FilterKey(1)] ?? device.InstalledFilter.Name;
            var offset = int.TryParse(query[DeviceQueryKeyExtensions.FilterOffsetKey(1)], out var o) ? o : 0;
            return [new InstalledFilter(name, offset)];
        }
    }

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

    public IExternal External { get; } = serviceProvider.GetRequiredService<IExternal>();

    public ILogger Logger { get; } = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ManualFilterWheelDriver));

    public ITimeProvider TimeProvider { get; } = serviceProvider.GetRequiredService<ITimeProvider>();

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
