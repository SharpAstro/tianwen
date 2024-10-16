using Astap.Lib.Devices;
using System;
using System.Threading;

namespace Astap.Lib.Tests.Fakes;

internal sealed class FakeFocuserDriver(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), IFocuserDriver
{
    private ITimer? _movingTimer;
    private volatile int _position;
    private volatile bool _isMoving;

    public int Position => _position;

    public override DeviceType DriverType => DeviceType.Focuser;

    public bool Absolute => true;

    public bool IsMoving => _isMoving;

    public int MaxIncrement => -1;

    public int MaxStep => 2000;

    public bool CanGetStepSize => false;

    public double StepSize => double.NaN;

    public bool TempComp { get => false; set => throw new NotImplementedException(); }

    public bool TempCompAvailable => false;

    public double Temperature => double.NaN;

    public bool Halt()
    {
        Interlocked.Exchange(ref _movingTimer, null)?.Dispose();
        _isMoving = false;
        return true;
    }

    public bool Move(int position)
    {
        if (position < 0)
        {
            return false;
        }

        if (position > MaxStep)
        {
            return false;
        }

        var currentPosition = position;

        if (currentPosition != position)
        {
            _isMoving = true;

            var state = new MovingState(currentPosition, position);

            Interlocked.Exchange(
                ref _movingTimer,
                External.TimeProvider.CreateTimer(MovingTimerCallback, state, TimeSpan.FromMicroseconds(50), TimeSpan.FromMilliseconds(100))
            )?.Dispose();
        }

        return false;
    }

    private void MovingTimerCallback(object? state)
    {
        if (state is MovingState movingState && _isMoving)
        {
            // check if Halt has been called
            if (!_isMoving)
            {
                return;
            }

            var currentPosition = _position;
            switch (Math.Sign(movingState.End - currentPosition))
            {
                case -1:
                    Interlocked.CompareExchange(ref _position, currentPosition - 1, currentPosition);
                    break;

                case 1:
                    Interlocked.CompareExchange(ref _position, currentPosition + 1, currentPosition);
                    break;

                case 0:
                    _isMoving = false;
                    Interlocked.Exchange(ref _movingTimer, null)?.Dispose();
                    break;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            Interlocked.Exchange(ref _movingTimer, null)?.Dispose();
        }
    }

    private record MovingState(int Start, int End);
}