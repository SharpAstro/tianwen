using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Ascom.ComInterop;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Ascom;

[SupportedOSPlatform("windows")]
internal class AscomFilterWheelDriver : AscomDeviceDriverBase, IFilterWheelDriver
{
    private readonly AscomDispatchFilterWheel _filterWheel;
    private string[] _names = [];
    private int[] _focusOffsets = [];

    internal AscomFilterWheelDriver(AscomDevice device, IServiceProvider sp) : base(device, sp)
    {
        _filterWheel = new AscomDispatchFilterWheel(_dispatchDevice.Dispatch);
    }

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        // Slot metadata is hardware-fixed; cache it here so the Filters accessor — which the UI
        // reads frequently — doesn't re-dispatch through COM on every render.
        _names = SafeGet(() => _filterWheel.Names, []);
        _focusOffsets = SafeGet(() => _filterWheel.FocusOffsets, []);
        return ValueTask.FromResult(true);
    }

    public ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SafeGet(() => (int)_filterWheel.Position, -1));

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (Filters is { Count: > 0 } filters && position is >= 0 and <= short.MaxValue && position < filters.Count)
        {
            return SafeTask(() => _filterWheel.Position = (short)position);
        }
        else
        {
            throw new InvalidOperationException($"Cannot change filter wheel position to {position}");
        }
    }

    public IReadOnlyList<InstalledFilter> Filters
    {
        get
        {
            var query = _device.Query;
            var slotCount = _names.Length;

            var filters = new List<InstalledFilter>(slotCount);
            for (var i = 0; i < slotCount; i++)
            {
                // URI query params override COM values per slot (profile is source of truth)
                var uriName = query[DeviceQueryKeyExtensions.FilterKey(i + 1)];
                var name = uriName ?? _names[i];
                var offset = int.TryParse(query[DeviceQueryKeyExtensions.FilterOffsetKey(i + 1)], out var o)
                    ? o
                    : (i < _focusOffsets.Length ? _focusOffsets[i] : 0);
                filters.Add(new InstalledFilter(name, offset));
            }

            return filters;
        }
    }
}
