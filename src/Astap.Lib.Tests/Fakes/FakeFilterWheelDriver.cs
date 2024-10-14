using Astap.Lib.Devices;
using System;
using System.Collections.Generic;

namespace Astap.Lib.Tests.Fakes;

internal sealed class FakeFilterWheelDriver(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), IFilterWheelDriver
{
    public IReadOnlyList<Filter> Filters => throw new NotImplementedException();

    public int Position { get; set; }

    public override DeviceType DriverType => DeviceType.FilterWheel;
}