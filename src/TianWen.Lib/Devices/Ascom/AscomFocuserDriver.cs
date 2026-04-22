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

    internal AscomFocuserDriver(AscomDevice device, IServiceProvider sp) : base(device, sp)
    {
        _focuser = new AscomDispatchFocuser(_dispatchDevice.Dispatch);
    }

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        // Cache immutable hardware capabilities — avoids a COM round-trip per property read
        // and, more importantly, avoids each read being another place a hung hub can throw.
        TempCompAvailable = SafeGet(() => _focuser.TempCompAvailable, false);
        Absolute = SafeGet(() => _focuser.Absolute, false);
        MaxIncrement = SafeGet(() => _focuser.MaxIncrement, int.MaxValue);
        MaxStep = SafeGet(() => _focuser.MaxStep, int.MaxValue);

        StepSize = SafeGet(() => _focuser.StepSize, double.NaN);
        CanGetStepSize = !double.IsNaN(StepSize);

        return ValueTask.FromResult(true);
    }

    public ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && Absolute ? SafeGet(() => _focuser.Position, int.MinValue) : int.MinValue);

    public bool Absolute { get; private set; }

    public ValueTask<bool> GetIsMovingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && SafeGet(() => _focuser.IsMoving, false));

    public int MaxIncrement { get; private set; } = int.MaxValue;

    public int MaxStep { get; private set; } = int.MaxValue;

    public double StepSize { get; private set; } = double.NaN;

    public bool CanGetStepSize { get; private set; }

    public ValueTask<bool> GetTempCompAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && TempCompAvailable && SafeGet(() => _focuser.TempComp, false));

    public ValueTask SetTempCompAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (Connected && TempCompAvailable)
        {
            return SafeValueTask(() => _focuser.TempComp = value);
        }
        return ValueTask.CompletedTask;
    }

    public bool TempCompAvailable { get; private set; }

    public int BacklashStepsIn { get; set; } = -1;

    public int BacklashStepsOut { get; set; } = -1;

    public ValueTask<double> GetTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? SafeGet(() => _focuser.Temperature, double.NaN) : double.NaN);

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

        return SafeTask(() => _focuser.Move(position));
    }

    public async Task BeginHaltAsync(CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Focuser not connected");
        }
        else if (await GetIsMovingAsync(cancellationToken))
        {
            SafeDo(() => _focuser.Halt());
        }
    }
}
