using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal class FakeFocuserDriver(FakeDevice fakeDevice, IExternal external) : FakePositionBasedDriver(fakeDevice, external), IFocuserDriver
{
    public int Position => _position;

    public bool Absolute => true;

    public bool IsMoving => _isMoving;

    public int MaxIncrement => -1;

    public int MaxStep => 2000;

    public bool CanGetStepSize => false;

    public double StepSize => double.NaN;

    public bool TempComp { get => false; set => throw new FakeDeviceException($"{nameof(TempComp)} is not supported (trying to set to {value})"); }

    public bool TempCompAvailable => false;

    public double Temperature => double.NaN;

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