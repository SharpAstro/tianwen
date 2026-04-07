namespace TianWen.Lib.Hosting.Dto.NinaV2;

/// <summary>
/// Stub DTO for unsupported devices (rotator, flat device, dome, switch, weather, safety monitor).
/// Returns <c>{ Connected: false }</c> which TNS handles gracefully.
/// </summary>
public sealed class NinaStubInfoDto
{
    public bool Connected { get; init; }

    public static NinaStubInfoDto Disconnected { get; } = new NinaStubInfoDto { Connected = false };
}
