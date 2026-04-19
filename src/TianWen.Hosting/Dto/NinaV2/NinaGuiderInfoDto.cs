namespace TianWen.Hosting.Dto.NinaV2;

/// <summary>
/// Guider info DTO matching ninaAPI v2 <c>/v2/api/equipment/guider/info</c> response shape.
/// </summary>
public sealed class NinaGuiderInfoDto
{
    public required bool Connected { get; init; }
    public required string Name { get; init; }
    public required string State { get; init; }

    public static NinaGuiderInfoDto Disconnected { get; } = new NinaGuiderInfoDto
    {
        Connected = false, Name = "", State = "Disconnected",
    };
}
