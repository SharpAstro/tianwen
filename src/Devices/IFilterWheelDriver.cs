using System.Collections.Generic;

namespace Astap.Lib.Devices;

public interface IFilterWheelDriver : IDeviceDriver
{
    IEnumerable<string> Names { get; }

    int Position { get; set; }
}