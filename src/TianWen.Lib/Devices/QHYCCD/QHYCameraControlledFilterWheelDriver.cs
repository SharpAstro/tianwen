using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices.DAL;
using QHYCCD.SDK;
using static QHYCCD.SDK.QHYCamera;

namespace TianWen.Lib.Devices.QHYCCD;

/// <summary>
/// Filter wheel driver for QHY camera-cable-connected CFWs.
/// Shares the camera's native handle via reference-counted <see cref="QHYCCD_CAMERA_INFO.Open"/>.
/// The filter wheel is discovered by iterating cameras and probing <see cref="QHYCCD_CAMERA_INFO.IsCfwPlugged"/>.
/// </summary>
internal class QHYCameraControlledFilterWheelDriver(QHYDevice device, IExternal external) : DALDeviceDriverBase<QHYDevice, QHYCCD_CAMERA_INFO>(device, external), IFilterWheelDriver
{
    private int _filterCount;
    private volatile bool _moveRequested;
    private volatile int _destinationPosition = -1;

    public IReadOnlyList<InstalledFilter> Filters
    {
        get
        {
            if (_filterCount > 0)
            {
                var filters = new List<InstalledFilter>(_filterCount);

                for (var i = 0; i < _filterCount; i++)
                {
                    filters.Add(new InstalledFilter(_device.Query[DeviceQueryKeyExtensions.FilterKey(i + 1)] ?? $"Filter {i + 1}",
                        int.TryParse(_device.Query[DeviceQueryKeyExtensions.FilterOffsetKey(i + 1)], out int focusOffset) ? focusOffset : 0));
                }

                return filters;
            }

            return [];
        }
    }

    public override string? DriverInfo => $"QHY Filter Wheel Driver v{DriverVersion}";

    public override string? Description { get; } = $"QHY CFW driver using C# SDK wrapper v{GetSDKVersion()}";

    protected override INativeDeviceIterator<QHYCCD_CAMERA_INFO> NewIterator() => new DeviceIterator<QHYCCD_CAMERA_INFO>();

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        // The CFW requires the camera to be initialised (stream mode + InitQHYCCD)
        if (!_deviceInfo.Init())
        {
            _deviceInfo.Close();
            return ValueTask.FromResult(false);
        }

        if (!_deviceInfo.IsCfwPlugged)
        {
            _deviceInfo.Close();
            return ValueTask.FromResult(false);
        }

        _filterCount = _deviceInfo.CfwSlotCount;
        if (_filterCount > 0)
        {
            return ValueTask.FromResult(true);
        }

        _deviceInfo.Close();
        return ValueTask.FromResult(false);
    }

    /// <summary>
    /// Gets the current 0-based filter position, or -1 if moving.
    /// Handles A-series cameras that return the position number while moving (instead of "N")
    /// by tracking the destination position from <see cref="BeginMoveAsync"/> and returning -1
    /// until the reported position matches the target.
    /// </summary>
    public ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        var position = _deviceInfo.GetCfwPosition();

        // GetCfwPosition returns -1 for "N" (CFW2/3 moving) and "/" (A-series initialising).
        // A-series cameras report the current slot while moving — detect this via move tracking.
        if (_moveRequested && position != _destinationPosition)
        {
            return ValueTask.FromResult(-1);
        }

        if (_moveRequested && position == _destinationPosition)
        {
            _moveRequested = false;
            _destinationPosition = -1;
        }

        return ValueTask.FromResult(position);
    }

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        _destinationPosition = position;
        _moveRequested = true;

        if (_deviceInfo.SetCfwPosition(position))
        {
            return Task.CompletedTask;
        }

        _moveRequested = false;
        _destinationPosition = -1;
        throw new QHYDriverException($"Failed to set filter wheel position to {position}");
    }
}
