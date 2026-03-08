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

    internal AscomFilterWheelDriver(AscomDevice device, IExternal external) : base(device, external)
    {
        _filterWheel = new AscomDispatchFilterWheel(_dispatchDevice.Dispatch);
    }

    string[] Names => _filterWheel.Names;

    int[] FocusOffsets => _filterWheel.FocusOffsets;

    public ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult((int)_filterWheel.Position);

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (Filters is { Count: > 0 } filters && position is >= 0 and <= short.MaxValue && position < filters.Count)
        {
            _filterWheel.Position = (short)position;
        }
        else
        {
            throw new InvalidOperationException($"Cannot change filter wheel position to {position}");
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<InstalledFilter> Filters
    {
        get
        {
            var names = Names;
            var offsets = FocusOffsets;
            var filters = new List<InstalledFilter>(names.Length);
            for (var i = 0; i < names.Length; i++)
            {
                filters.Add(new InstalledFilter(names[i], i < offsets.Length ? offsets[i] : 0));
            }

            return filters;
        }
    }
}
