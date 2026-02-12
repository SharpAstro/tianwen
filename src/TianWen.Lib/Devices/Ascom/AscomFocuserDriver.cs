namespace TianWen.Lib.Devices.Ascom;

using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AscomFocuser = ASCOM.Com.DriverAccess.Focuser;

[SupportedOSPlatform("windows")]
internal class AscomFocuserDriver(AscomDevice device, IExternal external)
    : AscomDeviceDriverBase<AscomFocuser>(device, external, (progId, logger) => new AscomFocuser(progId, new AscomLoggerWrapper(logger))), IFocuserDriver
{
    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        TempCompAvailable = _comObject.TempCompAvailable;

        try
        {
            StepSize = _comObject.StepSize is double stepSize && !double.IsNaN(stepSize) ? stepSize : double.NaN;
            CanGetStepSize = !double.IsNaN(StepSize);
        }
        catch
        {
            StepSize = double.NaN;
            CanGetStepSize = false;
        }

        return ValueTask.FromResult(true);
    }

    public int Position => Connected && Absolute && _comObject?.Position is int pos ? pos : int.MinValue;

    public bool Absolute => _comObject.Absolute;

    public bool IsMoving => Connected && _comObject?.IsMoving is bool moving && moving;

    public int MaxIncrement => _comObject.MaxIncrement;

    public int MaxStep => _comObject.MaxStep;

    public double StepSize { get; private set; } = double.NaN;

    public bool CanGetStepSize { get; private set; }

    public bool TempComp
    {
        get => Connected && TempCompAvailable && _comObject?.TempComp is bool tempComp && tempComp;

        set
        {
            if (Connected && TempCompAvailable && _comObject is { } obj)
            {
                obj.TempComp = value;
            }
        }
    }

    public bool TempCompAvailable { get; private set; }

    public double Temperature => Connected && _comObject?.Temperature is double temperature ? temperature : double.NaN;

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
            _comObject.Move(position);
        }

        return Task.CompletedTask;
    }

    public Task BeginHaltAsync(CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Focuser not connected");
        }
        else if (IsMoving)
        {
            _comObject.Halt();
        }

        return Task.CompletedTask;
    }
}