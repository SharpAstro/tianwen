using System;

namespace TianWen.Lib.Devices.Fake;

public class FakeDeviceException : Exception
{
    public FakeDeviceException(string message)
        : base(message)
    {

    }
}
