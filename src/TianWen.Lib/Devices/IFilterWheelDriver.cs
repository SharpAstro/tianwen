using System.Collections.Generic;

namespace TianWen.Lib.Devices;

public interface IFilterWheelDriver : IDeviceDriver
{
    IReadOnlyList<Filter> Filters { get; }

    int Position { get; set; }

    Filter CurrentFilter
    {
        get
        {
            if (Connected)
            {
                var filters = Filters;
                var position = Position;

                if (position >= 0 && position < filters.Count)
                {
                    return filters[position];
                }
            }

            return Filter.Unknown;
        }
    }
}