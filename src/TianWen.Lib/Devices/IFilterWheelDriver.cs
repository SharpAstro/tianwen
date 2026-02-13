using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

public interface IFilterWheelDriver : IDeviceDriver
{
    IReadOnlyList<InstalledFilter> Filters { get; }

    int Position { get; }

    bool IsMoving => Position == -1;

    InstalledFilter CurrentFilter
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

            return new InstalledFilter(Filter.Unknown);
        }
    }

    Task BeginMoveAsync(int position, CancellationToken cancellationToken = default);
}