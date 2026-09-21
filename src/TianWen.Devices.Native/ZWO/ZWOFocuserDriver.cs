using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices.DAL;
using ZWOptical.SDK;
using static ZWOptical.SDK.EAFFocuser1_6;
using static ZWOptical.SDK.EAFFocuser1_6.EAF_ERROR_CODE;

namespace TianWen.Lib.Devices.ZWO;

internal class ZWOFocuserDriver(ZWODevice device, IServiceProvider sp) : DALDeviceDriverBase<ZWODevice, EAF_INFO>(device, sp), IFocuserDriver
{
    public override string? DriverInfo => $"ZWO Electronic Focuser Driver v{DriverVersion}";

    protected override INativeDeviceIterator<EAF_INFO> NewIterator() => new DeviceIterator<EAF_INFO>();

    public bool Absolute => true;

    public ValueTask<bool> GetIsMovingAsync(CancellationToken cancellationToken = default)
        => EAFIsMoving(ConnectionId, out var isMoving, out _) is var code && code is EAF_SUCCESS
            ? ValueTask.FromResult(isMoving)
            : throw new ZWODriverException(code, "Failed to determine if focuser is moving");

    public int MaxIncrement => int.MinValue;

    public int MaxStep => EAFGetMaxStep(ConnectionId, out var maxStep) is var code && code is EAF_SUCCESS
        ? maxStep
        : throw new ZWODriverException(code, "Failed get max step size");

    public ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default)
        => EAFGetPosition(ConnectionId, out var position) is var code && code is EAF_SUCCESS
            ? ValueTask.FromResult(position)
            : throw new ZWODriverException(code, "Failed to get focuser position");

    public bool CanGetStepSize => false;

    public double StepSize => throw new ZWODriverException(EAF_ERROR_NOT_SUPPORTED, "Step size is not supported");

    public ValueTask<bool> GetTempCompAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    public ValueTask SetTempCompAsync(bool value, CancellationToken cancellationToken = default)
        => throw new ZWODriverException(EAF_ERROR_NOT_SUPPORTED, "Temperature compensation is not supported");

    public bool TempCompAvailable => false;

    public int BacklashStepsIn { get; set; } = -1;

    public int BacklashStepsOut { get; set; } = -1;

    public ValueTask<double> GetTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(EAFGetTemp(ConnectionId, out var temp) is EAF_SUCCESS ? temp : double.NaN);

    public override string? Description { get; } = $"ZWO EAF driver using C# SDK wrapper v{EAFGetSDKVersion()}";

    public Task BeginHaltAsync(CancellationToken cancellationToken = default)
    {
        if (EAFStop(ConnectionId) is var code && code is EAF_SUCCESS)
        {
            return Task.CompletedTask;
        }

        throw new ZWODriverException(code, $"Failed to halt focuser");
    }

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (EAFMove(ConnectionId, position) is var code && code is EAF_SUCCESS)
        {
            return Task.CompletedTask;
        }

        throw new ZWODriverException(code, $"Failed to move focuser to {position}");
    }
}