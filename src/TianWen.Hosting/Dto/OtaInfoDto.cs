using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto;

/// <summary>
/// Static OTA configuration (from Setup, not per-frame state).
/// </summary>
public sealed class OtaInfoDto
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required int FocalLength { get; init; }
    public required int? Aperture { get; init; }
    public required string OpticalDesign { get; init; }
    public required string CameraName { get; init; }
    public required string? FocuserName { get; init; }
    public required string? FilterWheelName { get; init; }
    public required string? CoverName { get; init; }

    public static OtaInfoDto FromOta(int index, OTA ota) => new()
    {
        Index = index,
        Name = ota.Name,
        FocalLength = ota.FocalLength,
        Aperture = ota.Aperture,
        OpticalDesign = ota.OpticalDesign.ToString(),
        CameraName = ota.Camera.Device.DisplayName,
        FocuserName = ota.Focuser?.Device.DisplayName,
        FilterWheelName = ota.FilterWheel?.Device.DisplayName,
        CoverName = ota.Cover?.Device.DisplayName,
    };
}
