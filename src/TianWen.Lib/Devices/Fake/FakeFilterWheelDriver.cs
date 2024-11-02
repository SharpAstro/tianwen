using System.Collections.Generic;

namespace TianWen.Lib.Devices.Fake;

internal sealed class FakeFilterWheelDriver(FakeDevice fakeDevice, IExternal external) : FakePositionBasedDriver(fakeDevice, external), IFilterWheelDriver
{
    public IReadOnlyList<Filter> Filters => [
        new Filter("Luminance"),
        new Filter("H-Alpha + OIII", +11),
        new Filter("Red", +20),
        new Filter("Green"),
        new Filter("Blue", -15),
        new Filter("SII", +25),
        new Filter("H-Alpha", +21),
        new Filter("OIII", -3)
    ];

    public int Position
    {
        get => _isMoving ? -1 : _position;
        set
        {
            if (Position < 0 || Position >= Filters.Count)
            {
                throw new FakeDeviceException($"Invalid position {value}");
            }

            if (!SetPosition(value))
            {
                throw new FakeDeviceException($"Failed to move to position {value}");
            }
        }
    }

    public override DeviceType DriverType => DeviceType.FilterWheel;
}