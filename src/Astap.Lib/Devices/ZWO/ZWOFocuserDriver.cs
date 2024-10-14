using static ZWOptical.SDK.EAFFocuser1_6;
using static ZWOptical.SDK.EAFFocuser1_6.EAF_ERROR_CODE;

namespace Astap.Lib.Devices.ZWO;

public class ZWOFocuserDriver(ZWODevice device, IExternal external) : ZWODeviceDriverBase<EAF_INFO>(device, external), IFocuserDriver
{
    public bool Absolute => true;

    public bool IsMoving => EAFIsMoving(ConnectionId, out var isMoving, out _) is var code && code is EAF_SUCCESS
        ? isMoving
        : throw new ZWODriverException(code, "Failed to determine if focuser is moving");

    public int MaxIncrement => int.MinValue;

    public int MaxStep => EAFGetMaxStep(ConnectionId, out var maxStep) is var code && code is EAF_SUCCESS
        ? maxStep
        : throw new ZWODriverException(code, "Failed get max step size");

    public int Position => EAFGetPosition(ConnectionId, out var position) is var code && code is EAF_SUCCESS
        ? position
        : throw new ZWODriverException(code, "Failed to get focuser position");

    public bool CanGetStepSize => false;

    public double StepSize => throw new ZWODriverException(EAF_ERROR_NOT_SUPPORTED, "Step size is not supported");

    public bool TempComp { get => false; set => throw new ZWODriverException(EAF_ERROR_NOT_SUPPORTED, "Temperature compensation is not supported"); }

    public bool TempCompAvailable => false;

    public double Temperature => EAFGetTemp(ConnectionId, out var temp) is EAF_SUCCESS ? temp : double.NaN;

    public override string? Description => "ZWO EAF driver using C# SDK wrapper";

    public override string? DriverVersion => EAFGetSDKVersion().ToString();

    public bool Halt() => EAFStop(ConnectionId) is EAF_SUCCESS;

    public bool Move(int position) => EAFMove(ConnectionId, position) is EAF_SUCCESS;

    protected override void DisposeNative()
    {
        // nothing to do
    }
}