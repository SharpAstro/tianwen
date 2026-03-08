namespace TianWen.Lib.Devices.Ascom;

using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Ascom.ComInterop;

[SupportedOSPlatform("windows")]
internal class AscomFocuserDriver : AscomDeviceDriverBase, IFocuserDriver
{
    private readonly AscomDispatchFocuser _focuser;

    internal AscomFocuserDriver(AscomDevice device, IExternal external) : base(device, external)
    {
        _focuser = new AscomDispatchFocuser(_dispatchDevice.Dispatch);
    }

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        TempCompAvailable = _focuser.TempCompAvailable;

        try
        {
            StepSize = _focuser.StepSize is double stepSize && !double.IsNaN(stepSize) ? stepSize : double.NaN;
            CanGetStepSize = !double.IsNaN(StepSize);
        }
        catch
        {
            StepSize = double.NaN;
            CanGetStepSize = false;
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && Absolute ? _focuser.Position : int.MinValue);

    public bool Absolute => _focuser.Absolute;

    public ValueTask<bool> GetIsMovingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && _focuser.IsMoving);

    public int MaxIncrement => _focuser.MaxIncrement;

    public int MaxStep => _focuser.MaxStep;

    public double StepSize { get; private set; } = double.NaN;

    public bool CanGetStepSize { get; private set; }

    public ValueTask<bool> GetTempCompAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && TempCompAvailable && _focuser.TempComp);

    public ValueTask SetTempCompAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (Connected && TempCompAvailable)
        {
            _focuser.TempComp = value;
        }
        return ValueTask.CompletedTask;
    }

    public bool TempCompAvailable { get; private set; }

    public ValueTask<double> GetTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? _focuser.Temperature : double.NaN);

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Focuser not connected");
        }
        else if (Absolute && (position is < 0 || position > MaxStep))
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, $"Absolute position must be between 0 and {MaxStep}");
        }
        else if (!Absolute && (position < -MaxIncrement || position > MaxIncrement))
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, $"Relative position must be between -{MaxIncrement} and {MaxIncrement}");
        }
        else
        {
            _focuser.Move(position);
        }

        return Task.CompletedTask;
    }

    public async Task BeginHaltAsync(CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Focuser not connected");
        }
        else if (await GetIsMovingAsync(cancellationToken))
        {
            _focuser.Halt();
        }
    }
}
