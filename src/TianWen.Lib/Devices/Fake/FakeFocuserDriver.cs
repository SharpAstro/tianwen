namespace TianWen.Lib.Devices.Fake;

internal class FakeFocuserDriver(FakeDevice fakeDevice, IExternal external) : FakePositionBasedDriver(fakeDevice, external), IFocuserDriver
{
    public int Position => _position;

    public override DeviceType DriverType => DeviceType.Focuser;

    public bool Absolute => true;

    public bool IsMoving => _isMoving;

    public int MaxIncrement => -1;

    public int MaxStep => 2000;

    public bool CanGetStepSize => false;

    public double StepSize => double.NaN;

    public bool TempComp { get => false; set => throw new FakeDeviceException($"{nameof(TempComp)} is not supported (trying to set to {value})"); }

    public bool TempCompAvailable => false;

    public double Temperature => double.NaN;

    public bool Halt() => StopMoving();

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

        return SetPosition(position);
    }
}