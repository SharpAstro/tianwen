using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

public record InstalledFilter(Filter Filter, int Position = 0)
{
    public InstalledFilter(string Name, int Position = 0) : this(Filter.FromName(Name), Position) { }

    public static implicit operator Filter(InstalledFilter installedFilter) => installedFilter.Filter;
}
