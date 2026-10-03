using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;

namespace TianWen.Hosting.Dto;

/// <summary>
/// A live view as the node's run: <c>POST /api/v1/live</c> (P4 of docs/plans/live-session-preview.md, #1111). The node
/// streams the OTA's camera over its whole sensor, natively where the camera has video (a Canon's Live View) and as a loop
/// of short exposures where it has not, and keeps none of the frames: a client watches them at <c>/frames/live</c>. It
/// ends on <c>DELETE /api/v1/live</c>, and by itself once no client has watched it for the node's detach grace.
/// </summary>
public sealed class LiveViewRequestDto
{
    /// <summary>The OTA whose camera streams.</summary>
    public int OtaIndex { get; init; }

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>
    /// Exposure per frame, in milliseconds. A camera without video takes exposures this long one after another; a Canon's
    /// Live View runs at the body's own rate and SIMULATES this exposure (the shutter speed a still would take, at most 30 s),
    /// so the frames are as bright as that still.
    /// </summary>
    public double ExposureMs { get; set; } = 100.0;

    /// <summary>Gain (a Canon's ISO), or null to keep the camera's.</summary>
    public short? Gain { get; init; }

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>The binning, as the Preview's still takes it; clamped to what the camera can bin (a Canon bins nothing).</summary>
    public short Binning { get; set; } = 1;
}

/// <summary>
/// A change to the live view going on: <c>PUT /api/v1/live/controls</c>, taking effect after the next frame. Each field
/// left null is left as it is.
/// </summary>
public sealed class LiveViewControlsDto
{
    public double? ExposureMs { get; init; }

    public short? Gain { get; init; }

    /// <summary>
    /// A step of the camera's own lens drive (#681), where <see cref="LiveViewStateDto.CanDriveLens"/>: numeric, its sign
    /// the direction (negative Near, positive Far) and its magnitude the size, 1 to 3.
    /// </summary>
    public LensFocusStep? LensStep { get; init; }
}

/// <summary>The live view going on, or the last one to end until the node's next run replaces it: <c>GET /api/v1/live</c>.</summary>
public sealed class LiveViewStateDto
{
    public int OtaIndex { get; init; }

    /// <summary>The camera's name.</summary>
    public required string Camera { get; init; }

    /// <summary>The newest frame's size; zero before the first.</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    public bool Running { get; init; }

    public int FramesReceived { get; init; }

    /// <summary>Frames the camera reported dropped; 0 for one that cannot report it.</summary>
    public int DroppedFrames { get; init; }

    /// <summary>The camera's measured rate; null until it can be measured.</summary>
    public double? FramesPerSecond { get; init; }

    /// <summary>The depth the newest frame came in; null before the first.</summary>
    public BitDepth? BitDepth { get; init; }

    /// <summary>Why it failed, in words; null while it runs, and when it ended on a stop.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Whether the camera can drive its own lens now (a Canon in its live view): the Near and Far buttons are offered.</summary>
    public bool CanDriveLens { get; init; }
}
