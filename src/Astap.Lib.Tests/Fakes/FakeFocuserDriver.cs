using Astap.Lib.Devices;
using System;

namespace Astap.Lib.Tests.Fakes;

internal sealed class FakeFocuserDriver(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), IFocuserDriver
{
    public int Position { get; set; }

    public override DeviceType DriverType => DeviceType.Focuser;

    public bool Absolute => true;

    public bool IsMoving { get; private set; }

    public int MaxIncrement => -1;

    public int MaxStep => 2000;

    public bool CanGetStepSize => false;

    public double StepSize => double.NaN;

    public bool TempComp { get => false; set => throw new NotImplementedException(); }

    public bool TempCompAvailable => false;

    public double Temperature => double.NaN;

    public bool Halt()
    {
        throw new NotImplementedException();
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

        Position = position;

        throw new NotImplementedException();
    }
}