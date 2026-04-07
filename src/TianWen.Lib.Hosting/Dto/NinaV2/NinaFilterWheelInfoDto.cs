using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Hosting.Dto.NinaV2;

/// <summary>
/// Filter wheel info DTO matching ninaAPI v2 <c>/v2/api/equipment/filterwheel/info</c> response shape.
/// </summary>
public sealed class NinaFilterWheelInfoDto
{
    public required bool Connected { get; init; }
    public required string Name { get; init; }
    public required int Position { get; init; }
    public required string[] Filters { get; init; }
    public NinaSelectedFilterDto? SelectedFilter { get; init; }

    public static async Task<NinaFilterWheelInfoDto> FromDriverAsync(IFilterWheelDriver driver, CancellationToken ct)
    {
        var position = await driver.GetPositionAsync(ct);
        var filters = driver.Filters;
        var filterNames = filters.Select(f => f.DisplayName).ToArray();
        NinaSelectedFilterDto? selected = position >= 0 && position < filters.Count
            ? new NinaSelectedFilterDto { Name = filters[position].DisplayName, Id = position }
            : null;

        return new NinaFilterWheelInfoDto
        {
            Connected = driver.Connected,
            Name = driver.Name,
            Position = position,
            Filters = filterNames,
            SelectedFilter = selected,
        };
    }

    public static NinaFilterWheelInfoDto Disconnected { get; } = new NinaFilterWheelInfoDto
    {
        Connected = false, Name = "", Position = -1, Filters = [], SelectedFilter = null,
    };
}

public sealed class NinaSelectedFilterDto
{
    public required string Name { get; init; }
    public required int Id { get; init; }
}
