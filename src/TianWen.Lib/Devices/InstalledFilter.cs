using System;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

public readonly record struct InstalledFilter(Filter Filter, int Position = 0, string? CustomName = null)
{
    public InstalledFilter(string name, int position = 0)
        : this(MakeFilter(name), position,
               Filter.FromName(name).IsUnknown ? name : null) { }

    /// <summary>Creates a Filter from a name, preserving the raw name when non-standard.</summary>
    private static Filter MakeFilter(string name)
    {
        // An unrecognised name already carries its text. A recognised one keeps a spelling that is
        // not one of its standard names ("ha"), and otherwise stays equal to the static instance.
        var filter = Filter.FromName(name);
        if (!filter.IsUnknown
            && !string.Equals(name, filter.DisplayName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, filter.ShortName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, filter.Name, StringComparison.OrdinalIgnoreCase))
        {
            return filter with { RawName = name };
        }
        return filter;
    }

    /// <summary>
    /// Display name: uses <see cref="CustomName"/> for unknown filters,
    /// otherwise <see cref="Filter.DisplayName"/>.
    /// </summary>
    public string DisplayName => CustomName ?? Filter.DisplayName;

    public static implicit operator Filter(InstalledFilter installedFilter) => installedFilter.Filter;
}