using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

public interface IFilterWheelDriver : IDeviceDriver
{
    IReadOnlyList<InstalledFilter> Filters { get; }

    ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default);

    async ValueTask<InstalledFilter> GetCurrentFilterAsync(CancellationToken cancellationToken = default)
    {
        if (Connected)
        {
            var filters = Filters;
            var position = await GetPositionAsync(cancellationToken);

            if (position >= 0 && position < filters.Count)
            {
                return filters[position];
            }
        }

        return new InstalledFilter(Filter.Unknown);
    }

    Task BeginMoveAsync(int position, CancellationToken cancellationToken = default);
}
