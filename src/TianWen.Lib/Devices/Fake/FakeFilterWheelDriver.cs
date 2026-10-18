using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal sealed class FakeFilterWheelDriver(FakeDevice fakeDevice, IServiceProvider serviceProvider) : FakePositionBasedDriver(fakeDevice, serviceProvider), IFilterWheelDriver
{
    // Per-device-ID presets (1-based ID, cycling through Presets). Ids 1 and 2 are what the tests' FakeFilterWheel1
    // and its neighbours have always been; 3 to 5 are the wheels a planetary or deep-sky rig is set up with.
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

    // A planetary wheel: the UV/IR cut as luminance, the colours, an IR pass for the seeing and methane for the bands.
    private static readonly IReadOnlyList<InstalledFilter> PlanetaryFilters =
    [
        new InstalledFilter("Luminance"),
        new InstalledFilter("Red", +8),
        new InstalledFilter("Green"),
        new InstalledFilter("Blue", -6),
        new InstalledFilter("IR 685", +14),
        new InstalledFilter("CH4", +18)
    ];

    // A five-slot narrowband wheel: luminance, the three lines, and a blank for darks.
    private static readonly IReadOnlyList<InstalledFilter> LshodFilters =
    [
        new InstalledFilter("Luminance"),
        new InstalledFilter("SII", +25),
        new InstalledFilter("H-Alpha", +21),
        new InstalledFilter("OIII", -3),
        new InstalledFilter("Dark")
    ];

    private static readonly IReadOnlyList<InstalledFilter> LhrgbFilters =
    [
        new InstalledFilter("Luminance"),
        new InstalledFilter("H-Alpha", +21),
        new InstalledFilter("Red", +20),
        new InstalledFilter("Green"),
        new InstalledFilter("Blue", -15)
    ];

    private static readonly IReadOnlyList<(string Name, IReadOnlyList<InstalledFilter> Filters)> Presets =
    [
        ("LRGB", LrgbFilters),
        ("LRGBSHO", NarrowbandFilters),
        ("Planetary", PlanetaryFilters),
        ("LSHOD", LshodFilters),
        ("LHRGB", LhrgbFilters),
    ];

    /// <summary>How many fake filter wheels discovery lists: one per preset.</summary>
    internal static int PresetCount => Presets.Count;

    internal const int MaxSlots = 8;

    internal static IReadOnlyList<InstalledFilter> GetDefaultFiltersForId(int id) => Presets[(id - 1) % Presets.Count].Filters;

    /// <summary>The preset's name, for the wheel's display name ("LRGB", "Planetary", ...).</summary>
    internal static string GetPresetNameForId(int id) => Presets[(id - 1) % Presets.Count].Name;

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
