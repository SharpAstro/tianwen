using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Alpaca;

internal class AlpacaFilterWheelDriver(AlpacaDevice device, IExternal external)
    : AlpacaDeviceDriverBase(device, external), IFilterWheelDriver
{
    // Dynamic property — sync version throws, callers should use async alternative
    public int Position => throw new NotSupportedException("Use GetPositionAsync instead");

    public async ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default)
        => await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "position", cancellationToken);

    public async Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (Filters is { Count: > 0 } filters && position >= 0 && position < filters.Count)
        {
            await PutMethodAsync("position", [new("Position", position.ToString(CultureInfo.InvariantCulture))], cancellationToken);
        }
        else
        {
            throw new InvalidOperationException($"Cannot change filter wheel position to {position}");
        }
    }

    public IReadOnlyList<InstalledFilter> Filters
    {
        get => []; // TODO: implement string[] and int[] typed getters for filter names and focus offsets
    }
}
