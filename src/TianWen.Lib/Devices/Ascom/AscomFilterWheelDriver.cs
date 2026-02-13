using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using AscomFilterWheel = ASCOM.Com.DriverAccess.FilterWheel;

namespace TianWen.Lib.Devices.Ascom;

[SupportedOSPlatform("windows")]
internal class AscomFilterWheelDriver(AscomDevice device, IExternal external)
    : AscomDeviceDriverBase<AscomFilterWheel>(device, external, (progId, logger) => new AscomFilterWheel(progId, new AscomLoggerWrapper(logger))), IFilterWheelDriver
{
    string[] Names => _comObject?.Names is string[] names ? names : [];

    int[] FocusOffsets => _comObject?.FocusOffsets is int[] focusOffsets ? focusOffsets : [];

    public int Position => _comObject.Position;

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (Filters is { Count: > 0 } filters && position is >= 0 and <= short.MaxValue && position < filters.Count)
        {
            _comObject.Position = (short)position;
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
                filters[i] = new InstalledFilter(names[i], i < offsets.Length ? offsets[i] : 0);
            }

            return filters;
        }
    }
}