using System.Collections.Generic;

namespace Astap.Lib.Devices;

public interface IFilterWheelDriver : IDeviceDriver
{
    IReadOnlyList<Filter> Filters { get; }

    int Position { get; set; }
}