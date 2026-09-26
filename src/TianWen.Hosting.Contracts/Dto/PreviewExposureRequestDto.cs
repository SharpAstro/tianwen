namespace TianWen.Hosting.Dto;

/// <summary>
/// A preview exposure with the camera of an OTA of the node's active profile, outside a session:
/// <c>POST /api/v1/preview/ota/{index}/exposure</c> (P5 part 2 of docs/plans/hardware-in-the-server.md, #934). Answered with
/// a job; the frame is then served as the OTA's (<c>GET /api/v1/frames/ota/{index}/latest</c>).
/// </summary>
public sealed class PreviewExposureRequestDto
{
    /// <summary>Exposure, in seconds.</summary>
    public required double ExposureSeconds { get; init; }

    /// <summary>Gain, or null to leave the camera's.</summary>
    public short? Gain { get; init; }

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>Binning, both axes.</summary>
    public int Binning { get; set; } = 1;
}
