using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal sealed class FakeFilterWheelDriver(FakeDevice fakeDevice, IExternal external) : FakePositionBasedDriver(fakeDevice, external), IFilterWheelDriver
{
    private static readonly IReadOnlyList<InstalledFilter> DefaultFilters =
    [
        new InstalledFilter("Luminance"),
        new InstalledFilter("H-Alpha + OIII", +11),
        new InstalledFilter("Red", +20),
        new InstalledFilter("Green"),
        new InstalledFilter("Blue", -15),
        new InstalledFilter("SII", +25),
        new InstalledFilter("H-Alpha", +21),
        new InstalledFilter("OIII", -3)
    ];

    private IReadOnlyList<InstalledFilter>? _filters;

    /// <summary>
    /// Reads filter names and focus offsets from the device URI query parameters
    /// (filter1, offset1, filter2, offset2, ...), same as the ZWO driver.
    /// Falls back to a default 8-slot wheel when no query params are present.
    /// </summary>
    public IReadOnlyList<InstalledFilter> Filters
    {
        get
        {
            if (_filters is not null)
            {
                return _filters;
            }

            var query = fakeDevice.Query;
            var filters = new List<InstalledFilter>();

            for (var i = 1; ; i++)
            {
                var name = query[DeviceQueryKeyExtensions.FilterKey(i)];
                if (name is null)
                {
                    break;
                }

                var offset = int.TryParse(query[DeviceQueryKeyExtensions.FilterOffsetKey(i)], out var o) ? o : 0;
                filters.Add(new InstalledFilter(name, offset));
            }

            _filters = filters.Count > 0 ? filters : DefaultFilters;
            return _filters;
        }
    }

    public ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_isMoving ? -1 : _position);

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default) => BeginSetPositionAsync(position, cancellationToken);
}
