using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices.DAL;
using TianWen.Lib.Imaging;
using ZWOptical.SDK;
using static ZWOptical.SDK.EFW1_7;
using static ZWOptical.SDK.EFW1_7.EFW_ERROR_CODE;

namespace TianWen.Lib.Devices.ZWO;

internal class ZWOFilterWheelDriver(ZWODevice device, IExternal external) : DALDeviceDriverBase<ZWODevice, EFW_INFO>(device, external), IFilterWheelDriver
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

    public override string? DriverInfo => $"ZWO Electronic Filter Wheel Driver v{DriverVersion}";

    protected override INativeDeviceIterator<EFW_INFO> NewIterator() => new DeviceIterator<EFW_INFO>();

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        _filterCount = _deviceInfo.NumberOfSlots;
        if (_filterCount > 0)
        {
            return ValueTask.FromResult(true);
        }
        else
        {
            // close this device again as we failed to initalize it
            _deviceInfo.Close();

            return ValueTask.FromResult(false);
        }
    }

    public int Position  => EFWGetPosition(ConnectionId, out var position) is var code && code is EFW_SUCCESS
        ? position
        : throw new ZWODriverException(code, "Failed to get filter wheel position");

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (EFWSetPosition(ConnectionId, position) is var code && code is EFW_SUCCESS)
        {
            return Task.CompletedTask;
        }
        else
        {
            throw new ZWODriverException(code, $"Failed to set filter wheel position to {position}");
        }
    }

    public override string? Description { get; } = $"ZWO EFW driver using C# SDK wrapper v{EFWGetSDKVersion()}";
}
