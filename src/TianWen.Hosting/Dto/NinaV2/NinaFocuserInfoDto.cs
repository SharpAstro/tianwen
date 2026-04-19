using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Hosting.Dto.NinaV2;

/// <summary>
/// Focuser info DTO matching ninaAPI v2 <c>/v2/api/equipment/focuser/info</c> response shape.
/// </summary>
public sealed class NinaFocuserInfoDto
{
    public required bool Connected { get; init; }
    public required string Name { get; init; }
    public required int Position { get; init; }
    public required bool IsMoving { get; init; }
    public required double Temperature { get; init; }
    public required bool TempCompAvailable { get; init; }

    public static async Task<NinaFocuserInfoDto> FromDriverAsync(IFocuserDriver driver, CancellationToken ct)
    {
        return new NinaFocuserInfoDto
        {
            Connected = driver.Connected,
            Name = driver.Name,
            Position = await driver.GetPositionAsync(ct),
            IsMoving = await driver.GetIsMovingAsync(ct),
            Temperature = await driver.GetTemperatureAsync(ct),
            TempCompAvailable = driver.TempCompAvailable,
        };
    }

    public static NinaFocuserInfoDto Disconnected { get; } = new NinaFocuserInfoDto
    {
        Connected = false, Name = "", Position = 0, IsMoving = false,
        Temperature = double.NaN, TempCompAvailable = false,
    };
}
