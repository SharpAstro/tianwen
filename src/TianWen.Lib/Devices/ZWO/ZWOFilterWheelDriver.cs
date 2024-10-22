using System.Collections.Generic;
using static ZWOptical.SDK.EFW1_7;
using static ZWOptical.SDK.EFW1_7.EFW_ERROR_CODE;

namespace TianWen.Lib.Devices.ZWO;

internal class ZWOFilterWheelDriver(ZWODevice device, IExternal external) : ZWODeviceDriverBase<EFW_INFO>(device, external), IFilterWheelDriver
{
    private int? _filterCount = null;

    public IReadOnlyList<Filter> Filters
    {
        get
        {
            if (_filterCount is { } filterCount && filterCount > 0)
            {
                var filters = new List<Filter>(filterCount);

                for (var i = 0; i < filterCount; i++)
                {
                    filters.Add(new Filter(_device.Query[$"filter{i + 1}"] ?? $"Filter {i + 1}",
                        int.TryParse(_device.Query[$"offset{i + 1}"], out int focusOffset) ? focusOffset : 0));
                }

                return filters;
            }

            return [];
        }
    }

    protected override bool ConnectDevice(out int connectionId, out EFW_INFO connectedDeviceInfo)
    {
        if (base.ConnectDevice(out connectionId, out connectedDeviceInfo))
        {
            _filterCount = connectedDeviceInfo.slotNum;
            return true;
        }

        _filterCount = null;
        return false;
    }

    public int Position
    {
        get => EFWGetPosition(ConnectionId, out var position) is var code && code is EFW_SUCCESS
            ? position
            : throw new ZWODriverException(code, "Failed to get filter wheel position");

        set
        {
            if (EFWSetPosition(ConnectionId, value) is var code && code is not EFW_SUCCESS)
            {
                throw new ZWODriverException(code, $"Failed to set filter wheel position to {value}");
            }
        }
    }

    public override string? Description { get; } = $"ZWO EFW driver using C# SDK wrapper v{EFWGetSDKVersion()}";
}
