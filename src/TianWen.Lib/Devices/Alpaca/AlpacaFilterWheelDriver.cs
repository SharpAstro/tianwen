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

    private string[]? _names;
    private int[]? _focusOffsets;

    public IReadOnlyList<InstalledFilter> Filters
    {
        get
        {
            // Slot count is authoritative from the Alpaca API (populated on connect)
            if (_names is not { Length: > 0 } names)
            {
                return [];
            }

            var query = _device.Query;
            var offsets = _focusOffsets;
            var filters = new List<InstalledFilter>(names.Length);

            for (var i = 0; i < names.Length; i++)
            {
                // URI query params override API values per slot (profile is source of truth)
                var uriName = query[DeviceQueryKeyExtensions.FilterKey(i + 1)];
                var name = uriName ?? names[i];
                var offset = int.TryParse(query[DeviceQueryKeyExtensions.FilterOffsetKey(i + 1)], out var o)
                    ? o
                    : (offsets is not null && i < offsets.Length ? offsets[i] : 0);
                filters.Add(new InstalledFilter(name, offset));
            }

            return filters;
        }
    }

    /// <summary>
    /// Reads filter names and focus offsets from the Alpaca API after connecting.
    /// </summary>
    internal async Task ReadFilterConfigAsync(CancellationToken cancellationToken)
    {
        _names = await Client.GetStringArrayAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "names", cancellationToken);
        _focusOffsets = await Client.GetIntArrayAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "focusoffsets", cancellationToken);
    }
}
