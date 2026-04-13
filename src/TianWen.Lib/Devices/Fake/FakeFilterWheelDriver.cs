using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal sealed class FakeFilterWheelDriver(FakeDevice fakeDevice, IServiceProvider serviceProvider) : FakePositionBasedDriver(fakeDevice, serviceProvider), IFilterWheelDriver
{
    // Per-device-ID presets (1-based ID, mod 3)
    private static readonly IReadOnlyList<InstalledFilter> LrgbFilters =
    [
        new InstalledFilter("Luminance"),
        new InstalledFilter("Red", +20),
        new InstalledFilter("Green"),
        new InstalledFilter("Blue", -15)
    ];

    private static readonly IReadOnlyList<InstalledFilter> NarrowbandFilters =
    [
        new InstalledFilter("Luminance"),
        new InstalledFilter("H-Alpha", +21),
        new InstalledFilter("OIII", -3),
        new InstalledFilter("SII", +25),
        new InstalledFilter("Red", +20),
        new InstalledFilter("Green"),
        new InstalledFilter("Blue", -15)
    ];

    private static readonly IReadOnlyList<InstalledFilter> SimpleFilters =
    [
        new InstalledFilter("Luminance"),
        new InstalledFilter("Red", +20),
        new InstalledFilter("Green")
    ];

    private static readonly IReadOnlyList<IReadOnlyList<InstalledFilter>> Presets = [LrgbFilters, NarrowbandFilters, SimpleFilters];

    internal const int MaxSlots = 8;

    internal static IReadOnlyList<InstalledFilter> GetDefaultFiltersForId(int id) => Presets[(id - 1) % Presets.Count];

    private IReadOnlyList<InstalledFilter>? _filters;

    /// <summary>
    /// Slot count is determined by the per-device-ID preset (max 8 slots).
    /// URI query params (filter1, offset1, ...) override names and offsets per slot.
    /// </summary>
    public IReadOnlyList<InstalledFilter> Filters
    {
        get
        {
            if (_filters is not null)
            {
                return _filters;
            }

            var query = _fakeDevice.Query;
            var defaults = GetDefaultFiltersForId(Math.Max(1, FakeCameraDriver.ExtractId(_fakeDevice.DeviceUri)));
            var slotCount = defaults.Count;
            var filters = new List<InstalledFilter>(slotCount);

            for (var i = 0; i < slotCount; i++)
            {
                var name = query[DeviceQueryKeyExtensions.FilterKey(i + 1)] ?? defaults[i].Filter.Name;
                var offset = int.TryParse(query[DeviceQueryKeyExtensions.FilterOffsetKey(i + 1)], out var o) ? o : defaults[i].Position;
                filters.Add(new InstalledFilter(name, offset));
            }

            _filters = filters;
            return _filters;
        }
    }

    public ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_isMoving ? -1 : _position);

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default) => BeginSetPositionAsync(position, cancellationToken);
}
