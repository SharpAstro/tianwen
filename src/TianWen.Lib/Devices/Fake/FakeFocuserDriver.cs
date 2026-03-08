using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal class FakeFocuserDriver(FakeDevice fakeDevice, IExternal external) : FakePositionBasedDriver(fakeDevice, external), IFocuserDriver
{
    public ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_position);

    public bool Absolute => true;

    public ValueTask<bool> GetIsMovingAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_isMoving);

    public int MaxIncrement => -1;

    public int MaxStep => 2000;

    public bool CanGetStepSize => false;

    public double StepSize => double.NaN;

    public ValueTask<bool> GetTempCompAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    public ValueTask SetTempCompAsync(bool value, CancellationToken cancellationToken = default)
        => throw new FakeDeviceException($"TempComp is not supported (trying to set to {value})");

    public bool TempCompAvailable => false;

    public ValueTask<double> GetTemperatureAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(double.NaN);

    public Task BeginHaltAsync(CancellationToken cancellationToken = default) => BeginStopMovingAsync(cancellationToken);

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Focuser is not connected");
        }
        else if (position < 0 || position > MaxStep)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, $"Position out of range (0..{MaxStep})");
        }

        return BeginSetPositionAsync(position, cancellationToken);
    }
}
