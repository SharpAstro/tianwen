using System.Collections.Generic;
using static ZWOptical.SDK.EFW1_7;
using static ZWOptical.SDK.EFW1_7.EFW_ERROR_CODE;

namespace Astap.Lib.Devices.ZWO;

public class ZWOFilterWheelDriver(ZWODevice device) : ZWODeviceDriverBase<EFW_INFO>(device), IFilterWheelDriver
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
                    int focusOffset;
                    if (!int.TryParse(_device.Query[$"offset{i + 1}"], out focusOffset))
                    {
                        focusOffset = 0;
                    }

                    filters.Add(new Filter(_device.Query[$"filter{i + 1}"] ?? $"Filter {i + 1}", focusOffset));
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

    public override string? Description => "ZWO EFW driver using C# SDK wrapper";

    public override string? DriverVersion => EFWGetSDKVersion().ToString();

    protected override void DisposeNative()
    {
        // nothing to do
    }
}
