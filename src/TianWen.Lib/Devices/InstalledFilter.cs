using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

public readonly record struct InstalledFilter(Filter Filter, int Position = 0, string? CustomName = null)
{
    public InstalledFilter(string Name, int Position = 0)
        : this(Filter.FromName(Name), Position, Filter.FromName(Name) == Filter.Unknown ? Name : null) { }

    /// <summary>
    /// Display name: uses <see cref="CustomName"/> for unknown filters,
    /// otherwise <see cref="Filter.DisplayName"/>.
    /// </summary>
    public string DisplayName => CustomName ?? Filter.DisplayName;

    public static implicit operator Filter(InstalledFilter installedFilter) => installedFilter.Filter;
}