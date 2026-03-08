using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

public interface IFilterWheelDriver : IDeviceDriver
{
    IReadOnlyList<InstalledFilter> Filters { get; }

    int Position { get; }

    /// <summary>
    /// Async alternative to <see cref="Position"/>. Default delegates to the sync property.
    /// </summary>
    ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Position);

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

    /// <summary>
    /// Async alternative to <see cref="CurrentFilter"/>. Default delegates to the sync property.
    /// </summary>
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